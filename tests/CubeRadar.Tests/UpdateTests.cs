using System.Net;
using System.Security.Cryptography;
using CubeRadar.Core.Update;

namespace CubeRadar.Tests;

/// <summary>Updater: reading GitHub's release JSON, verified downloads, swapping the running exe.</summary>
public class UpdateTests
{
    private static readonly Uri Api = new("https://api.example/releases/latest");
    private static readonly byte[] NewExe = [.. Enumerable.Range(0, 200_000).Select(i => (byte)(i * 7))];
    private static readonly string NewExeSha = Convert.ToHexStringLower(SHA256.HashData(NewExe));

    private static string ReleaseJson(string tag = "v0.8.1", string? digest = null, string asset = "Cube.Radar.exe",
        bool draft = false, bool prerelease = false, long? size = null) => $$"""
        {
          "tag_name": "{{tag}}", "draft": {{(draft ? "true" : "false")}}, "prerelease": {{(prerelease ? "true" : "false")}},
          "html_url": "https://github.com/x/y/releases/tag/{{tag}}",
          "assets": [
            { "name": "notes.txt", "size": 3, "browser_download_url": "https://dl.example/notes.txt", "digest": "sha256:{{new string('0', 64)}}" },
            { "name": "{{asset}}", "size": {{size ?? NewExe.Length}}, "browser_download_url": "https://dl.example/{{asset}}"
              {{(digest is null ? "" : $", \"digest\": \"{digest}\"")}} }
          ]
        }
        """;

    [Fact]
    public void Parse_reads_version_page_asset_and_digest()
    {
        var r = UpdateClient.Parse(ReleaseJson(digest: "sha256:" + NewExeSha.ToUpperInvariant()))!;
        Assert.Equal(new Version(0, 8, 1), r.Version);
        Assert.Equal("v0.8.1", r.Tag);
        Assert.Equal("https://github.com/x/y/releases/tag/v0.8.1", r.PageUrl);
        Assert.Equal("https://dl.example/Cube.Radar.exe", r.AssetUrl);
        Assert.Equal(NewExe.Length, r.AssetSize);
        Assert.Equal(NewExeSha, r.Sha256);
        Assert.True(r.Installable);
    }

    [Fact]
    public void Parse_skips_drafts_prereleases_and_tags_that_are_no_version()
    {
        Assert.Null(UpdateClient.Parse(ReleaseJson(draft: true)));
        Assert.Null(UpdateClient.Parse(ReleaseJson(prerelease: true)));
        Assert.Null(UpdateClient.Parse(ReleaseJson(tag: "nightly")));
    }

    [Fact]
    public void Release_without_digest_or_exe_only_offers_the_page()
    {
        Assert.False(UpdateClient.Parse(ReleaseJson())!.Installable);
        var noExe = UpdateClient.Parse(ReleaseJson(digest: "sha256:" + NewExeSha, asset: "other.zip"))!;
        Assert.Null(noExe.AssetUrl);
        Assert.False(noExe.Installable);
    }

    [Theory]
    [InlineData("v0.8.1", "0.8.0.0", true)]
    [InlineData("v0.8.0", "0.8.0.0", false)] // the tag's 0.8.0 equals the assembly's 0.8.0.0
    [InlineData("0.7.9", "0.8.0.0", false)]
    [InlineData("v0.10.0", "0.9.0.0", true)]
    public void Newer_compares_versions_not_text(string tag, string current, bool newer)
    {
        Assert.True(UpdateClient.TryParseVersion(tag, out var v));
        var release = new ReleaseInfo(v, tag, "", null, 0, null);
        Assert.Equal(newer, UpdateClient.IsNewer(release, Version.Parse(current)));
    }

    [Fact]
    public async Task Latest_is_null_while_the_repository_has_no_release()
    {
        using var http = new HttpClient(new Stub(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        Assert.Null(await new UpdateClient(http, Api).LatestAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Latest_asks_github_for_its_json()
    {
        HttpRequestMessage? seen = null;
        using var http = new HttpClient(new Stub(req =>
        {
            seen = req;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ReleaseJson(digest: "sha256:" + NewExeSha)) };
        }));
        var r = await new UpdateClient(http, Api).LatestAsync(CancellationToken.None);
        Assert.Equal(new Version(0, 8, 1), r!.Version);
        Assert.Equal(Api, seen!.RequestUri);
        Assert.Contains(seen.Headers.Accept, a => a.MediaType == "application/vnd.github+json");
    }

    [Fact]
    public async Task Download_is_kept_only_when_size_and_sha256_match()
    {
        var dir = Directory.CreateTempSubdirectory("cuberadar-update").FullName;
        try
        {
            using var http = new HttpClient(new Stub(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(NewExe) }));
            var client = new UpdateClient(http, Api);
            var target = Path.Combine(dir, "Cube Radar.exe.download");

            await client.DownloadAsync(UpdateClient.Parse(ReleaseJson(digest: "sha256:" + NewExeSha))!, target, CancellationToken.None);
            Assert.Equal(NewExe, await File.ReadAllBytesAsync(target));

            var wrongHash = UpdateClient.Parse(ReleaseJson(digest: "sha256:" + new string('a', 64)))!;
            await Assert.ThrowsAsync<UpdateException>(() => client.DownloadAsync(wrongHash, target, CancellationToken.None));
            Assert.False(File.Exists(target));

            var wrongSize = UpdateClient.Parse(ReleaseJson(digest: "sha256:" + NewExeSha, size: NewExe.Length + 1))!;
            await Assert.ThrowsAsync<UpdateException>(() => client.DownloadAsync(wrongSize, target, CancellationToken.None));
            Assert.False(File.Exists(target));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Installer_copy_lives_next_to_the_installed_exe()
    {
        Assert.Equal(Path.Combine(@"C:\Games", "Cube Radar.update.exe"), SelfReplace.StagingPath(@"C:\Games\Cube Radar.exe"));
    }

    [Fact]
    public void Install_replaces_the_exe_and_cleanup_removes_the_installer_copy()
    {
        var dir = Directory.CreateTempSubdirectory("cuberadar-update").FullName;
        try
        {
            var exe = Path.Combine(dir, "Cube Radar.exe");
            var staged = SelfReplace.StagingPath(exe);
            File.WriteAllText(exe, "old");
            File.WriteAllText(staged, "new");
            File.WriteAllText(Path.Combine(dir, "unrelated.exe"), "keep");

            SelfReplace.Install(staged, exe, TimeSpan.FromSeconds(1));
            Assert.Equal("new", File.ReadAllText(exe));
            Assert.False(File.Exists(exe + ".new"));

            using (new FileStream(staged, FileMode.Open, FileAccess.Read, FileShare.Read)) // the installer still runs
                Assert.Equal(0, SelfReplace.CleanUp(exe));
            Assert.Equal(1, SelfReplace.CleanUp(exe));
            Assert.Equal(["Cube Radar.exe", "unrelated.exe"], Directory.GetFiles(dir).Select(Path.GetFileName).Order());
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Install_waits_while_the_old_radar_still_holds_the_exe()
    {
        var dir = Directory.CreateTempSubdirectory("cuberadar-update").FullName;
        try
        {
            var exe = Path.Combine(dir, "Cube Radar.exe");
            var staged = SelfReplace.StagingPath(exe);
            File.WriteAllText(exe, "old");
            File.WriteAllText(staged, "new");
            var running = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.Read); // "still running"
            var closes = Task.Delay(600).ContinueWith(_ => running.Dispose());

            SelfReplace.Install(staged, exe, TimeSpan.FromSeconds(10));
            await closes;
            Assert.Equal("new", File.ReadAllText(exe));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Install_gives_up_without_harming_the_installed_exe()
    {
        var dir = Directory.CreateTempSubdirectory("cuberadar-update").FullName;
        try
        {
            var exe = Path.Combine(dir, "Cube Radar.exe");
            var staged = SelfReplace.StagingPath(exe);
            File.WriteAllText(exe, "old");
            File.WriteAllText(staged, "new");
            Exception? error;
            using (new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.Read)) // never closes
                error = Record.Exception(() => SelfReplace.Install(staged, exe, TimeSpan.FromMilliseconds(600)));
            Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString());
            Assert.Equal("old", File.ReadAllText(exe));
            Assert.False(File.Exists(exe + ".new"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }
}
