using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace CubeRadar.Core.Update;

/// <summary>A published release, as far as the updater needs it.</summary>
/// <param name="AssetUrl">Download URL of the exe; null when the release has none.</param>
/// <param name="Sha256">Lower-case hex SHA-256 of the exe as GitHub reports it; null when it reports none.</param>
public sealed record ReleaseInfo(Version Version, string Tag, string PageUrl, string? AssetUrl, long AssetSize, string? Sha256)
{
    /// <summary>Whether the exe can be downloaded and verified; otherwise only the release page can be offered.</summary>
    public bool Installable => AssetUrl is not null && Sha256 is not null;
}

/// <summary>An update could not be downloaded or installed; the message is meant for the user.</summary>
public sealed class UpdateException(string message) : Exception(message);

/// <summary>
/// Reads the latest GitHub release and downloads its exe. A download is only kept when its size and SHA-256 match what
/// GitHub reports for the asset.
/// </summary>
public sealed class UpdateClient(HttpClient http, Uri latestRelease, string assetName = UpdateClient.AssetName)
{
    public static readonly Uri GitHubLatest = new("https://api.github.com/repos/dennisreichenberg/aion2-cube-radar/releases/latest");
    public const string ReleasesPage = "https://github.com/dennisreichenberg/aion2-cube-radar/releases";

    /// <summary>GitHub turns the space in "Cube Radar.exe" into a dot when it is uploaded.</summary>
    public const string AssetName = "Cube.Radar.exe";

    /// <summary>The latest release, or null when there is none yet.</summary>
    public async Task<ReleaseInfo?> LatestAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, latestRelease);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return Parse(await response.Content.ReadAsStringAsync(ct), assetName);
    }

    /// <summary>Reads a GitHub "release" object; null for drafts, pre-releases and tags that are no version.</summary>
    public static ReleaseInfo? Parse(string json, string assetName = AssetName)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        if (Bool(r, "draft") || Bool(r, "prerelease") || Str(r, "tag_name") is not { } tag || !TryParseVersion(tag, out var version))
            return null;

        string? url = null, sha = null;
        long size = 0;
        if (r.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            foreach (var a in assets.EnumerateArray())
            {
                if (!string.Equals(Str(a, "name"), assetName, StringComparison.OrdinalIgnoreCase)) continue;
                url = Str(a, "browser_download_url");
                size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var n) ? n : 0;
                if (Str(a, "digest") is { Length: 7 + 64 } d && d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                    sha = d[7..].ToLowerInvariant();
                break;
            }
        return new ReleaseInfo(version, tag, Str(r, "html_url") ?? ReleasesPage, url, size, sha);
    }

    /// <summary>"v0.8.1" or "0.8.1" → 0.8.1.</summary>
    public static bool TryParseVersion(string text, out Version version)
    {
        var ok = Version.TryParse(text.Trim().TrimStart('v', 'V'), out var v);
        version = ok ? Normalize(v!) : new Version(0, 0, 0);
        return ok;
    }

    /// <summary>Three parts, so that the tag's 0.8.1 equals the assembly's 0.8.1.0.</summary>
    public static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    public static bool IsNewer(ReleaseInfo release, Version current) => release.Version > Normalize(current);

    /// <summary>Downloads the release's exe to <paramref name="target"/>; deletes it again unless size and SHA-256 match.</summary>
    public async Task DownloadAsync(ReleaseInfo release, string target, CancellationToken ct)
    {
        if (!release.Installable) throw new UpdateException("Das Release enthält keine prüfbare exe.");
        try
        {
            using var response = await http.GetAsync(release.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long total = 0;
            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            await using (var file = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81_920];
                int n;
                while ((n = await source.ReadAsync(buffer, ct)) > 0)
                {
                    hash.AppendData(buffer, 0, n);
                    await file.WriteAsync(buffer.AsMemory(0, n), ct);
                    total += n;
                }
            }
            if (release.AssetSize > 0 && total != release.AssetSize)
                throw new UpdateException($"Der Download ist unvollständig ({total:N0} von {release.AssetSize:N0} Bytes).");
            if (Convert.ToHexStringLower(hash.GetHashAndReset()) != release.Sha256)
                throw new UpdateException("Die Prüfsumme des Downloads stimmt nicht mit der von GitHub überein.");
        }
        catch
        {
            SelfReplace.TryDelete(target);
            throw;
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
