using CubeRadar.Core.Protocol;

namespace CubeRadar.Core.Radar;

public enum CubeStatus
{
    /// <summary>In view range.</summary>
    Visible,
    /// <summary>Left the view range (35 36 reason 01): most likely still standing there.</summary>
    OutOfView,
    /// <summary>A player started opening it (38 36).</summary>
    Opening,
    /// <summary>Opened (3a 36) or destroyed after opening (35 36 reason 04).</summary>
    Opened,
    /// <summary>Removed without us seeing who opened it, or not sent again although we came close.</summary>
    Gone,
}

public sealed class CubeState
{
    public required uint ObjectId { get; init; }
    public required uint Template { get; init; }
    public string? Zone { get; set; }
    /// <summary>Null for a configured template; "key-item" or "spot" when the template was learned in operation.</summary>
    public string? Learned { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public long CreatedMs { get; set; }
    public long FirstSeenMs { get; init; }
    public long LastSeenMs { get; set; }
    public CubeStatus Status { get; set; }
    public long StatusSinceMs { get; set; }
    public uint? OpenedBy { get; set; }
    public bool OpenedBySelf { get; set; }
    /// <summary>Planar distance (units) to the own position when first seen, if a fresh fix existed.</summary>
    public double? FirstDistance { get; set; }
    public int? MapId { get; set; }
    public string? Note { get; set; }
}

public sealed record RingEstimate(double X, double Y, double Uncertainty, long TimeMs, int Events);

/// <summary>
/// Turns decoded server→client records into the radar state: Hidden Cubes and their status, the own entity and its
/// position (event-driven fixes, ring-fix fallback). Thread-safe: frames come from the capture thread, snapshots are
/// read by the web server. Semantics are documented in docs/FINDINGS.md (B5–B8, P1–P6).
/// </summary>
public sealed class RadarEngine
{
    private readonly object _gate = new();
    private readonly RadarConfig _cfg;
    private readonly IRadarStore? _store;
    private readonly SpotIndex? _spots;
    private readonly Orientation _north;

    private readonly Dictionary<uint, CubeTemplate> _templates;
    private readonly Dictionary<uint, ObjectInfo> _objects = new();
    private readonly Dictionary<uint, CubeState> _cubes = new();
    private readonly Dictionary<uint, long> _goneDeadline = new();
    private readonly Queue<(uint Id, int Weight)> _votes = new();
    private readonly Dictionary<uint, (PositionFix Fix, long T)> _lastFixByEntity = new();
    private readonly List<(long T, float X, float Y)> _trail = new();
    private readonly List<RingEvent> _events = new();
    private readonly Dictionary<(uint, bool), List<double>> _radii;
    private readonly Dictionary<RecordKind, long> _lastKind = new();
    private bool _selfFromInfo;
    private long _startMs;

    /// <summary>Every object spawn seen (any template); <see cref="Removed"/> is the 35 36 reason, 0 while present.</summary>
    private sealed record ObjectInfo(uint Template, float X, float Y, float Z, long T, long CreatedMs)
    {
        public byte Removed { get; set; }
    }

    private sealed class RingEvent(long t, uint template, bool enter, float x, float y)
    {
        public long T { get; } = t;
        public uint Template { get; } = template;
        public bool Enter { get; } = enter;
        public float X { get; } = x;
        public float Y { get; } = y;
        public bool Sampled { get; set; }
    }

    public RadarEngine(RadarConfig cfg, IRadarStore? store = null, SpotIndex? spots = null)
    {
        _cfg = cfg;
        _store = store;
        _spots = spots;
        _north = Orientation.Parse(cfg.North);
        _templates = new Dictionary<uint, CubeTemplate>(cfg.CubeTemplates);
        foreach (var t in store?.LoadLearnedTemplates() ?? []) _templates.TryAdd(t.Template, t);
        _radii = store?.LoadRadiusSamples() ?? new Dictionary<(uint, bool), List<double>>();
    }

    public event Action<CubeState>? CubeAppeared;
    public event Action<CubeState>? CubeChanged;
    public event Action<CubeTemplate>? TemplateLearned;

    public uint? SelfId { get; private set; }
    public PositionFix? SelfFix { get; private set; }
    public long SelfFixMs { get; private set; }
    public RingEstimate? Ring { get; private set; }
    public int? MapId { get; private set; }
    public string? Zone { get; private set; }
    public long LastPacketMs { get; private set; }
    public long Frames { get; private set; }

    public IReadOnlyCollection<CubeTemplate> Templates
    {
        get { lock (_gate) return _templates.Values.ToList(); }
    }

    public void OnFrame(long t, ushort opcode, ReadOnlySpan<byte> body)
    {
        lock (_gate)
        {
            Frames++;
            if (_startMs == 0) _startMs = t;
            LastPacketMs = Math.Max(LastPacketMs, t);

            if (_cfg.SelfIdOpcodes.Contains(opcode) && FirstVarint(body) is { } voter) Vote(voter, 2);
            if (_cfg.Opcodes.TryGetValue(opcode, out var kind))
            {
                _lastKind[kind] = t;
                Dispatch(kind, t, body);
            }
            if (_goneDeadline.Count > 0) CheckGone(t);
        }
    }

    private void Dispatch(RecordKind kind, long t, ReadOnlySpan<byte> body)
    {
        switch (kind)
        {
            case RecordKind.SelfInfo:
                if (FirstVarint(body) is { } self)
                {
                    _selfFromInfo = true;
                    SetSelf(self);
                }
                break;
            case RecordKind.MapLoad:
                if (body.Length >= 8) OnMap((int)Wire.U32(body, 4));
                break;
            case RecordKind.ObjectSpawn:
                if (ObjectMessages.TryParseSpawn(body, out var spawn)) OnSpawn(spawn, t);
                break;
            case RecordKind.ObjectRemoved:
                if (ObjectMessages.TryParseRemoved(body, out var removed)) OnRemoved(removed, t);
                break;
            case RecordKind.OpenStart:
            case RecordKind.OpenDone:
                if (ObjectMessages.TryParseInteraction(body, kind == RecordKind.OpenDone, out var interaction)) OnInteraction(interaction, t);
                break;
            case RecordKind.PosBuffA:
            case RecordKind.PosBuffB:
            case RecordKind.PosCast:
            case RecordKind.PosTarget:
                var pk = kind switch
                {
                    RecordKind.PosBuffA => PositionKind.BuffA,
                    RecordKind.PosBuffB => PositionKind.BuffB,
                    RecordKind.PosCast => PositionKind.Cast,
                    _ => PositionKind.Target,
                };
                if (PositionMessages.TryParse(pk, body, out var fix))
                {
                    // A buff the entity cast on itself also tells us who we are (our own buffs always arrive).
                    if (pk is PositionKind.BuffA or PositionKind.BuffB && FirstVarint(body) == fix.EntityId) Vote(fix.EntityId, 1);
                    OnPosition(fix, t);
                }
                break;
        }
    }

    // ------------------------------------------------------------------ own entity

    private void Vote(uint id, int weight)
    {
        _votes.Enqueue((id, weight));
        while (_votes.Count > 40) _votes.Dequeue();
        var tally = _votes.GroupBy(v => v.Id).Select(g => (Id: g.Key, W: g.Sum(v => v.Weight))).OrderByDescending(g => g.W).First();
        var total = _votes.Sum(v => v.Weight);
        if (tally.Id == SelfId) return;
        var needed = _selfFromInfo ? (W: 10, Share: 0.8) : (W: 4, Share: 0.6);
        if (tally.W >= needed.W && tally.W >= total * needed.Share) SetSelf(tally.Id);
    }

    private void SetSelf(uint id)
    {
        if (SelfId == id) return;
        SelfId = id;
        SelfFix = null;
        SelfFixMs = 0;
        Ring = null;
        _trail.Clear();
        if (_lastFixByEntity.TryGetValue(id, out var last)) AdoptFix(last.Fix, last.T);
    }

    private void OnPosition(PositionFix fix, long t)
    {
        _lastFixByEntity[fix.EntityId] = (fix, t);
        if (_lastFixByEntity.Count > 2000)
            foreach (var k in _lastFixByEntity.Where(kv => t - kv.Value.T > 300_000).Select(kv => kv.Key).ToList())
                _lastFixByEntity.Remove(k);
        if (SelfId == fix.EntityId) AdoptFix(fix, t);
    }

    private void AdoptFix(PositionFix fix, long t)
    {
        SelfFix = fix;
        SelfFixMs = t;
        Ring = null;
        _trail.Add((t, fix.X, fix.Y));
        if (_trail.Count > 600) _trail.RemoveRange(0, _trail.Count - 600);

        foreach (var e in _events)
            if (!e.Sampled && t - e.T is >= 0 and <= 1000) Sample(e, fix);

        foreach (var c in _cubes.Values)
            if (c.Status == CubeStatus.OutOfView && !_goneDeadline.ContainsKey(c.ObjectId) &&
                Units.Planar(fix.X, fix.Y, c.X, c.Y) < _cfg.GoneCheckUnits)
                _goneDeadline[c.ObjectId] = t + 3_000; // must be sent again within 3 s, else someone opened it

        if (_spots?.Nearest(fix.X, fix.Y, 30_000) is { } spot) Zone = spot.Zone;
    }

    private bool FixFresh(long t) => SelfFix is not null && t - SelfFixMs <= _cfg.FixFreshSeconds * 1000;

    // ------------------------------------------------------------------ objects and cubes

    private void OnSpawn(ObjectSpawn s, long t)
    {
        _objects[s.Id] = new ObjectInfo(s.Template, s.X, s.Y, s.Z, t, s.CreatedMs);
        AddRingEvent(t, s.Template, enter: true, s.X, s.Y);

        var tmpl = TemplateFor(s.Template) ?? LearnBySpot(s, t);
        if (tmpl is not null) UpsertCube(s.Id, s.Template, s.X, s.Y, s.Z, s.CreatedMs, t, tmpl);
    }

    private void UpsertCube(uint id, uint template, float x, float y, float z, long createdMs, long t, CubeTemplate tmpl,
        CubeStatus status = CubeStatus.Visible)
    {
        if (_cubes.TryGetValue(id, out var known) && known.Status is not (CubeStatus.Opened or CubeStatus.Gone))
        {
            known.X = x;
            known.Y = y;
            known.Z = z;
            known.LastSeenMs = t;
            _goneDeadline.Remove(id);
            if (known.Status == CubeStatus.OutOfView) SetStatus(known, CubeStatus.Visible, t);
            else Changed(known);
            return;
        }

        var cube = new CubeState
        {
            ObjectId = id,
            Template = template,
            Zone = tmpl.Zone,
            Learned = tmpl.Status is "key-item" or "spot" ? tmpl.Status : null,
            X = x,
            Y = y,
            Z = z,
            CreatedMs = createdMs,
            FirstSeenMs = t,
            LastSeenMs = t,
            Status = status,
            StatusSinceMs = t,
            MapId = MapId,
            FirstDistance = FixFresh(t) ? Units.Planar(SelfFix!.Value.X, SelfFix.Value.Y, x, y) : null,
        };
        _cubes[id] = cube;
        _store?.UpsertSighting(cube);
        CubeAppeared?.Invoke(cube);
    }

    private void OnRemoved(ObjectRemoved r, long t)
    {
        if (_objects.TryGetValue(r.Id, out var o))
        {
            o.Removed = r.Reason;
            if (r.Reason == ObjectMessages.ReasonLeftView) AddRingEvent(t, o.Template, enter: false, o.X, o.Y);
        }
        if (!_cubes.TryGetValue(r.Id, out var c)) return;
        if (r.Reason == ObjectMessages.ReasonLeftView)
        {
            if (c.Status is CubeStatus.Visible or CubeStatus.Opening) SetStatus(c, CubeStatus.OutOfView, t);
        }
        else if (c.Status != CubeStatus.Opened)
        {
            c.Note = $"removed, reason {r.Reason:x2}";
            SetStatus(c, c.OpenedBy is null ? CubeStatus.Gone : CubeStatus.Opened, t);
        }
    }

    private void OnInteraction(ObjectInteraction i, long t)
    {
        if (i.Done && i.Count == 1 && i.Item == _cfg.OpenDoneItem && _objects.TryGetValue(i.ObjectId, out var o) &&
            TemplateFor(o.Template) is null)
        {
            // Opened with the cube key: this template is a cube (e.g. another zone's).
            Learn(new CubeTemplate(o.Template, _spots?.Nearest(o.X, o.Y, 30_000)?.Zone ?? Zone ?? "?", "key-item"), t);
        }
        if (!_cubes.TryGetValue(i.ObjectId, out var c)) return;
        c.OpenedBy = i.ActorId;
        c.OpenedBySelf = i.ActorId == SelfId;
        SetStatus(c, i.Done ? CubeStatus.Opened : CubeStatus.Opening, t);
    }

    private CubeTemplate? TemplateFor(uint template) => _templates.GetValueOrDefault(template);

    private CubeTemplate? LearnBySpot(ObjectSpawn s, long t)
    {
        if (_spots?.Nearest(s.X, s.Y, _cfg.SpotMatchUnits) is not { } spot) return null;
        var learned = new CubeTemplate(s.Template, spot.Zone, "spot");
        Learn(learned, t, except: s.Id);
        return learned;
    }

    /// <summary>Takes a template as cube template and turns the objects already seen with it into cubes.</summary>
    private void Learn(CubeTemplate template, long t, uint? except = null)
    {
        _templates[template.Template] = template;
        _store?.SaveLearnedTemplate(template, t);
        TemplateLearned?.Invoke(template);
        foreach (var (id, o) in _objects.ToList())
        {
            if (o.Template != template.Template || id == except || _cubes.ContainsKey(id) || o.Removed is not (0 or 1)) continue;
            UpsertCube(id, o.Template, o.X, o.Y, o.Z, o.CreatedMs, o.T, template,
                o.Removed == ObjectMessages.ReasonLeftView ? CubeStatus.OutOfView : CubeStatus.Visible);
        }
    }

    private void CheckGone(long t)
    {
        foreach (var (id, deadline) in _goneDeadline.ToList())
        {
            if (t < deadline) continue;
            _goneDeadline.Remove(id);
            if (_cubes.TryGetValue(id, out var c) && c.Status == CubeStatus.OutOfView)
            {
                c.Note = "not sent again although we came close: opened by someone";
                SetStatus(c, CubeStatus.Gone, t);
            }
        }
    }

    private void OnMap(int mapId)
    {
        if (MapId == mapId) return; // same map again = in-map teleport
        MapId = mapId;
        _cubes.Clear();
        _objects.Clear();
        _events.Clear();
        _goneDeadline.Clear();
        Ring = null;
        _trail.Clear();
    }

    private void SetStatus(CubeState c, CubeStatus status, long t)
    {
        c.Status = status;
        c.StatusSinceMs = t;
        if (status == CubeStatus.Visible) c.LastSeenMs = t;
        Changed(c);
    }

    private void Changed(CubeState c)
    {
        _store?.UpsertSighting(c);
        CubeChanged?.Invoke(c);
    }

    // ------------------------------------------------------------------ ring fix

    private void AddRingEvent(long t, uint template, bool enter, float x, float y)
    {
        var e = new RingEvent(t, template, enter, x, y);
        if (SelfFix is { } fix && t - SelfFixMs is >= 0 and <= 1000) Sample(e, fix);
        _events.Add(e);
        _events.RemoveAll(ev => t - ev.T > 10_000);
        if (SelfId is not null && !FixFresh(t)) TryRingFix(t);
    }

    private void Sample(RingEvent e, PositionFix fix)
    {
        e.Sampled = true;
        var d = Units.Planar(fix.X, fix.Y, e.X, e.Y);
        var key = (e.Template, e.Enter);
        if (!_radii.TryGetValue(key, out var list)) _radii[key] = list = new List<double>();
        list.Add(d);
        _store?.AddRadiusSample(e.Template, e.Enter, d, e.T);
    }

    private double? Radius(uint template, bool enter)
    {
        if (!_radii.TryGetValue((template, enter), out var list) || list.Count < 2) return null;
        var sorted = list.OrderBy(v => v).ToList();
        return sorted[sorted.Count / 2];
    }

    private void TryRingFix(long t)
    {
        var usable = new List<(double X, double Y, double R)>();
        foreach (var e in _events)
            if (t - e.T <= _cfg.RingWindowMs && Radius(e.Template, e.Enter) is { } r)
                usable.Add((e.X, e.Y, r));
        if (usable.Count < _cfg.RingMinEvents) return;

        var cx = usable.Average(u => u.X);
        var cy = usable.Average(u => u.Y);
        (double Err, double X, double Y) best = (double.MaxValue, cx, cy);
        foreach (var (step, span, ox, oy) in new[] { (1000.0, 16000.0, cx, cy), (200.0, 1200.0, double.NaN, double.NaN) })
        {
            var bx = double.IsNaN(ox) ? best.X : ox;
            var by = double.IsNaN(oy) ? best.Y : oy;
            for (var gx = -span; gx <= span; gx += step)
            for (var gy = -span; gy <= span; gy += step)
            {
                double x = bx + gx, y = by + gy, err = 0;
                foreach (var u in usable)
                {
                    var d = Units.Planar(x, y, u.X, u.Y) - u.R;
                    err += d * d;
                }
                if (err < best.Err) best = (err, x, y);
            }
        }
        var rms = Math.Sqrt(best.Err / usable.Count);
        if (rms > 3000) return;
        Ring = new RingEstimate(best.X, best.Y, Math.Max(1000, rms), t, usable.Count);
    }

    // ------------------------------------------------------------------ snapshot

    public RadarSnapshot Snapshot()
    {
        lock (_gate)
        {
            var now = LastPacketMs;
            foreach (var c in _cubes.Values.ToList())
            {
                var age = now - c.StatusSinceMs;
                if ((c.Status is CubeStatus.Opened or CubeStatus.Gone && age > _cfg.KeepGoneSeconds * 1000) ||
                    (c.Status == CubeStatus.OutOfView && age > _cfg.KeepOutOfViewMinutes * 60_000))
                    _cubes.Remove(c.ObjectId);
            }

            double? ox = null, oy = null, oz = null;
            if (SelfFix is { } f)
            {
                ox = f.X;
                oy = f.Y;
                oz = f.Z;
            }
            if (Ring is { } ring && (SelfFix is null || ring.TimeMs > SelfFixMs))
            {
                ox = ring.X;
                oy = ring.Y;
            }

            var cubes = _cubes.Values.OrderBy(c => c.Status).ThenByDescending(c => c.FirstSeenMs).Select(c =>
            {
                double? dist = null, dist3 = null, bearing = null;
                if (ox is not null)
                {
                    dist = Units.Planar(ox.Value, oy!.Value, c.X, c.Y) / Units.PerMetre;
                    if (oz is not null) dist3 = Math.Sqrt(Math.Pow(dist.Value, 2) + Math.Pow((c.Z - oz.Value) / Units.PerMetre, 2));
                    bearing = _north.Bearing(ox.Value, oy.Value, c.X, c.Y);
                }
                return new CubeView(c.ObjectId, c.Template, c.Zone, c.Learned, c.X, c.Y, c.Z, c.CreatedMs, c.FirstSeenMs, c.LastSeenMs,
                    c.Status.ToString(), c.StatusSinceMs, c.OpenedBy, c.OpenedBySelf, c.MapId, dist, dist3, bearing,
                    bearing is { } b ? Orientation.Compass(b) : null, c.Z - oz, c.FirstDistance / Units.PerMetre, c.Note);
            }).ToList();

            return new RadarSnapshot(
                now, SelfId, MapId, Zone ?? _cubes.Values.Select(c => c.Zone).FirstOrDefault(z => z is not null),
                SelfFix is { } sf ? new FixView(sf.X, sf.Y, sf.Z, SelfFixMs, now - SelfFixMs, sf.Kind.ToString()) : null,
                Ring is { } r ? new RingView(r.X, r.Y, r.Uncertainty, r.TimeMs, now - r.TimeMs, r.Events) : null,
                _trail.Select(p => new[] { p.X, p.Y }).ToList(),
                cubes,
                SelfCheck(now),
                new OrientationView(_north.NorthX, _north.NorthY, _north.EastX, _north.EastY, _cfg.North),
                _cfg.FixFreshSeconds,
                Frames);
        }
    }

    /// <summary>Warnings for record kinds that should have arrived by now (patch moved an opcode?).</summary>
    public List<string> SelfCheck(long now)
    {
        var warnings = new List<string>();
        if (_startMs == 0) return warnings;
        foreach (var rule in _cfg.SelfCheck)
        {
            var last = _lastKind.GetValueOrDefault(rule.Kind);
            var since = last == 0 ? now - _startMs : now - last;
            if (since > rule.WithinSeconds * 1000L)
                warnings.Add($"{rule.Kind} ({OpcodeBook.Hex(_cfg.OpcodeOf(rule.Kind))}) seit {since / 1000} s nicht gesehen: {rule.Hint}");
        }
        if (SelfId is null && now - _startMs > 30_000) warnings.Add("eigene Entity-ID noch unbekannt (kein 33 36 / keine eigenen Buffs)");
        return warnings;
    }

    private static uint? FirstVarint(ReadOnlySpan<byte> body)
    {
        var o = 0;
        return Wire.TryVarint(body, ref o, out var v) && v is > 0 and < 10_000_000 ? v : null;
    }
}

public sealed record FixView(double X, double Y, double Z, long TimeMs, long AgeMs, string Source);

public sealed record RingView(double X, double Y, double Uncertainty, long TimeMs, long AgeMs, int Events);

public sealed record OrientationView(double NorthX, double NorthY, double EastX, double EastY, string North);

public sealed record CubeView(uint ObjectId, uint Template, string? Zone, string? Learned, double X, double Y, double Z, long CreatedMs,
    long FirstSeenMs, long LastSeenMs, string Status, long StatusSinceMs, uint? OpenedBy, bool OpenedBySelf, int? MapId,
    double? DistanceM, double? Distance3dM, double? Bearing, string? Compass, double? DzUnits, double? FirstDistanceM, string? Note);

public sealed record RadarSnapshot(long NowMs, uint? SelfId, int? MapId, string? Zone, FixView? Self, RingView? Ring,
    List<float[]> Trail, List<CubeView> Cubes, List<string> Warnings, OrientationView Orientation, double FixFreshSeconds, long Frames);
