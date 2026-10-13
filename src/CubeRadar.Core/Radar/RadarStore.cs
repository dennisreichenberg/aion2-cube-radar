using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CubeRadar.Core.Radar;

public sealed record HistorySpot(string Zone, double X, double Y, double Z, int Sightings, long LastSeenMs, int OpenedBySelf);

public interface IRadarStore
{
    void UpsertSighting(CubeState cube);
    void AddRadiusSample(uint template, bool enter, double distance, long timeMs);
    Dictionary<(uint Template, bool Enter), List<double>> LoadRadiusSamples();
    void SaveLearnedTemplate(CubeTemplate template, long timeMs);
    List<CubeTemplate> LoadLearnedTemplates();
    List<HistorySpot> History(string? zone);
}

/// <summary>
/// Own spot database (SQLite): one row per cube instance seen (updated as its status changes), the radius samples that
/// feed the ring fix, and cube templates learned in operation.
/// </summary>
public sealed class SqliteRadarStore : IRadarStore, IDisposable
{
    private readonly SqliteConnection _db;
    private readonly object _gate = new();
    private readonly string _session;

    public SqliteRadarStore(string path, string session)
    {
        _session = session;
        if (path != ":memory:") Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        _db.Open();
        Exec("""
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS sightings (
              id INTEGER PRIMARY KEY, session TEXT NOT NULL, object_id INTEGER NOT NULL, template INTEGER NOT NULL,
              zone TEXT, map_id INTEGER, x REAL, y REAL, z REAL, created_ms INTEGER, first_seen_ms INTEGER NOT NULL,
              last_seen_ms INTEGER, status TEXT, status_ms INTEGER, opened_by INTEGER, opened_by_self INTEGER,
              first_distance REAL, learned TEXT,
              UNIQUE (session, object_id, first_seen_ms));
            CREATE TABLE IF NOT EXISTS radius_samples (template INTEGER NOT NULL, enter INTEGER NOT NULL, dist REAL NOT NULL, t_ms INTEGER);
            CREATE TABLE IF NOT EXISTS learned_templates (template INTEGER PRIMARY KEY, zone TEXT, how TEXT, t_ms INTEGER);
            """);
    }

    public void UpsertSighting(CubeState c)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO sightings (session, object_id, template, zone, map_id, x, y, z, created_ms, first_seen_ms, last_seen_ms,
                                       status, status_ms, opened_by, opened_by_self, first_distance, learned)
                VALUES ($s, $o, $t, $zone, $map, $x, $y, $z, $c, $f, $l, $st, $sm, $ob, $obs, $fd, $le)
                ON CONFLICT (session, object_id, first_seen_ms) DO UPDATE SET
                  x = $x, y = $y, z = $z, last_seen_ms = $l, status = $st, status_ms = $sm, opened_by = $ob,
                  opened_by_self = $obs, zone = $zone, map_id = $map
                """;
            cmd.Parameters.AddWithValue("$s", _session);
            cmd.Parameters.AddWithValue("$o", (long)c.ObjectId);
            cmd.Parameters.AddWithValue("$t", (long)c.Template);
            cmd.Parameters.AddWithValue("$zone", (object?)c.Zone ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$map", (object?)c.MapId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$x", c.X);
            cmd.Parameters.AddWithValue("$y", c.Y);
            cmd.Parameters.AddWithValue("$z", c.Z);
            cmd.Parameters.AddWithValue("$c", c.CreatedMs == 0 ? DBNull.Value : c.CreatedMs);
            cmd.Parameters.AddWithValue("$f", c.FirstSeenMs);
            cmd.Parameters.AddWithValue("$l", c.LastSeenMs);
            cmd.Parameters.AddWithValue("$st", c.Status.ToString());
            cmd.Parameters.AddWithValue("$sm", c.StatusSinceMs);
            cmd.Parameters.AddWithValue("$ob", c.OpenedBy is { } ob ? (long)ob : DBNull.Value);
            cmd.Parameters.AddWithValue("$obs", c.OpenedBySelf ? 1 : 0);
            cmd.Parameters.AddWithValue("$fd", (object?)c.FirstDistance ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$le", (object?)c.Learned ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    public void AddRadiusSample(uint template, bool enter, double distance, long timeMs)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT INTO radius_samples (template, enter, dist, t_ms) VALUES ($t, $e, $d, $ms)";
            cmd.Parameters.AddWithValue("$t", (long)template);
            cmd.Parameters.AddWithValue("$e", enter ? 1 : 0);
            cmd.Parameters.AddWithValue("$d", distance);
            cmd.Parameters.AddWithValue("$ms", timeMs);
            cmd.ExecuteNonQuery();
        }
    }

    public Dictionary<(uint Template, bool Enter), List<double>> LoadRadiusSamples()
    {
        var map = new Dictionary<(uint, bool), List<double>>();
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT template, enter, dist FROM radius_samples";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var key = ((uint)r.GetInt64(0), r.GetInt64(1) == 1);
                if (!map.TryGetValue(key, out var list)) map[key] = list = new List<double>();
                list.Add(r.GetDouble(2));
            }
        }
        return map;
    }

    public void SaveLearnedTemplate(CubeTemplate t, long timeMs)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO learned_templates (template, zone, how, t_ms) VALUES ($t, $z, $h, $ms)";
            cmd.Parameters.AddWithValue("$t", (long)t.Template);
            cmd.Parameters.AddWithValue("$z", t.Zone);
            cmd.Parameters.AddWithValue("$h", t.Status);
            cmd.Parameters.AddWithValue("$ms", timeMs);
            cmd.ExecuteNonQuery();
        }
    }

    public List<CubeTemplate> LoadLearnedTemplates()
    {
        var list = new List<CubeTemplate>();
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT template, zone, how FROM learned_templates";
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(new CubeTemplate((uint)r.GetInt64(0), r.IsDBNull(1) ? "?" : r.GetString(1), r.IsDBNull(2) ? "" : r.GetString(2)));
        }
        return list;
    }

    /// <summary>Distinct cube positions ever seen (rounded to 1 m), newest first.</summary>
    public List<HistorySpot> History(string? zone)
    {
        var list = new List<HistorySpot>();
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                SELECT zone, AVG(x), AVG(y), AVG(z), COUNT(*), MAX(last_seen_ms), SUM(opened_by_self)
                FROM sightings WHERE ($zone IS NULL OR zone = $zone)
                GROUP BY zone, ROUND(x / 100), ROUND(y / 100) ORDER BY MAX(last_seen_ms) DESC
                """;
            cmd.Parameters.AddWithValue("$zone", (object?)zone ?? DBNull.Value);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new HistorySpot(r.IsDBNull(0) ? "?" : r.GetString(0), r.GetDouble(1), r.GetDouble(2), r.GetDouble(3),
                    r.GetInt32(4), r.GetInt64(5), r.GetInt32(6)));
        }
        return list;
    }

    /// <summary>All sightings as CSV (invariant culture) or JSON.</summary>
    public string Export(bool json)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                SELECT id, session, object_id, template, zone, map_id, x, y, z, created_ms, first_seen_ms, last_seen_ms,
                       status, status_ms, opened_by, opened_by_self, first_distance, learned
                FROM sightings ORDER BY first_seen_ms
                """;
            using var r = cmd.ExecuteReader();
            var names = Enumerable.Range(0, r.FieldCount).Select(r.GetName).ToArray();
            var rows = new List<object?[]>();
            while (r.Read())
            {
                var row = new object?[r.FieldCount];
                for (var i = 0; i < row.Length; i++) row[i] = r.IsDBNull(i) ? null : r.GetValue(i);
                rows.Add(row);
            }
            if (json)
                return JsonSerializer.Serialize(rows.Select(row => names.Zip(row).ToDictionary(p => p.First, p => p.Second)),
                    new JsonSerializerOptions { WriteIndented = true });
            var sb = new StringBuilder(string.Join(",", names)).Append('\n');
            foreach (var row in rows)
                sb.Append(string.Join(",", row.Select(v => v switch
                {
                    null => "",
                    double d => d.ToString("0.###", CultureInfo.InvariantCulture),
                    string s => s.Contains(',') ? $"\"{s}\"" : s,
                    _ => Convert.ToString(v, CultureInfo.InvariantCulture),
                }))).Append('\n');
            return sb.ToString();
        }
    }

    public void Dispose()
    {
        lock (_gate) _db.Dispose();
    }

    private void Exec(string sql)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
