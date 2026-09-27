using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using InfraLauncher.Core;
using InfraLauncher.Core.Models;

namespace InfraLauncher.Core.Tests;

public sealed class LaunchApprovalTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("infralauncher-launch-").FullName;
    private readonly Handler _handler = new();
    private readonly LauncherService _service;

    public LaunchApprovalTests() => _service = new LauncherService(new InstallLayout(Path.Combine(_dir, "data")), new HttpClient(_handler));

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static byte[] Zip(params (string Name, string Content)[] files)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create))
        {
            foreach (var (name, content) in files)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open());
                w.Write(content);
            }
        }
        return ms.ToArray();
    }

    private ServerEntry Server(string engineContent)
    {
        var engine = Zip(("simutrans.exe", engineContent));
        var pak = Zip(("a.pak", "a"));
        _handler.Files["https://x/engine.zip"] = engine;
        _handler.Files["https://x/pak.zip"] = pak;
        return new ServerEntry
        {
            Id = "s", Name = "鯖", Address = "h:1", EngineDownloadAllowed = true,
            Engine = new EngineInfo
            {
                Revision = "r-" + engineContent,
                Builds = new() { [PlatformInfo.CurrentKey] = new EngineBuild { Url = "https://x/engine.zip", Sha256 = Sha(engine), Exe = "simutrans.exe" } },
            },
            Pakset = new PaksetInfo { Name = "p", Folder = "p", Url = "https://x/pak.zip", Sha256 = Sha(pak) },
        };
    }

    private static string Sha(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

    [Fact]
    public async Task RequiresSyncBeforeLaunch()
    {
        var server = Server("v1");
        var e = Assert.Throws<SyncException>(() => _service.PrepareLaunch(server, new LauncherSettings()));
        Assert.Contains("同期", e.Message);

        // 同期しても起動はしない（プロセスを起動しないことは、同期がプロセスを作らないことで担保）
        await _service.SyncServerAsync(server, new LauncherSettings());
        var info = _service.PrepareLaunch(server, new LauncherSettings());
        Assert.True(info.ManagedEngine);
        Assert.True(info.NeedsApproval);
        Assert.Equal("https://x/engine.zip", info.SourceUrl);
    }

    [Fact]
    public async Task RefusesToLaunchUnapprovedEngine()
    {
        var server = Server("v1");
        var settings = new LauncherSettings();
        await _service.SyncServerAsync(server, settings);
        var info = _service.PrepareLaunch(server, settings);
        var e = Assert.Throws<SyncException>(() => LauncherService.Launch(info, settings));
        Assert.Contains("承認", e.Message);
    }

    [Fact]
    public async Task AsksAgainWhenEngineChanges()
    {
        var settings = new LauncherSettings();
        var v1 = Server("v1");
        await _service.SyncServerAsync(v1, settings);
        var info1 = _service.PrepareLaunch(v1, settings);
        settings.Approve(info1.ExeSha256);
        Assert.False(_service.PrepareLaunch(v1, settings).NeedsApproval);

        // サーバー側で本体が更新された
        var v2 = Server("v2");
        await _service.SyncServerAsync(v2, settings);
        Assert.True(_service.PrepareLaunch(v2, settings).NeedsApproval);
    }

    [Fact]
    public async Task DetectsTamperingAfterApproval()
    {
        var settings = new LauncherSettings();
        var server = Server("v1");
        await _service.SyncServerAsync(server, settings);
        var info = _service.PrepareLaunch(server, settings);
        settings.Approve(info.ExeSha256);

        // 確認のあとで実行ファイルが書き換えられた
        File.WriteAllText(info.ExePath, "malicious!");
        File.SetLastWriteTimeUtc(info.ExePath, DateTime.UtcNow.AddMinutes(5));
        var e = Assert.Throws<SyncException>(() => LauncherService.Launch(info, settings));
        Assert.Contains("書き換え", e.Message);

        // 書き換えられた本体は起動できず、同期で配布元のものに入れ直される
        Assert.Throws<SyncException>(() => _service.PrepareLaunch(server, settings));
        await _service.SyncServerAsync(server, settings);
        var again = _service.PrepareLaunch(server, settings);
        Assert.Equal(info.ExeSha256, again.ExeSha256);
        Assert.False(again.NeedsApproval);
        Assert.Equal("v1", File.ReadAllText(again.ExePath));
    }

    private sealed class Handler : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Files { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(Files.TryGetValue(request.RequestUri!.ToString(), out var data)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
