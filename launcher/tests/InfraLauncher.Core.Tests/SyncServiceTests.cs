using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using InfraLauncher.Core;
using InfraLauncher.Core.Models;

namespace InfraLauncher.Core.Tests;

public sealed class SyncServiceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("infralauncher-test-").FullName;
    private readonly FakeHandler _handler = new();
    private readonly InstallLayout _layout;
    private readonly SyncService _sync;

    public SyncServiceTests()
    {
        _layout = new InstallLayout(Path.Combine(_dir, "data"));
        _sync = new SyncService(_layout, new HttpClient(_handler));
    }

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

    private static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private ServerEntry Server(byte[] engineZip, byte[] pakZip) => new()
    {
        Id = "s",
        Name = "テスト鯖",
        Address = "example.net",
        EngineDownloadAllowed = true,
        Engine = new EngineInfo
        {
            Revision = "r1",
            Builds = new() { [PlatformInfo.CurrentKey] = new EngineBuild { Url = "https://x/engine.zip", Sha256 = Sha(engineZip), Exe = "simutrans.exe" } },
        },
        Pakset = new PaksetInfo { Name = "pak64", Folder = "pak64", Url = "https://x/pak.zip", Sha256 = Sha(pakZip) },
    };

    [Fact]
    public async Task InstallsEngineAndPaksetThenSkipsSecondTime()
    {
        var engine = Zip(("simutrans/simutrans.exe", "engine"));
        var pak = Zip(("pak64/ground.pak", "pak"));
        _handler.Files["https://x/engine.zip"] = engine;
        _handler.Files["https://x/pak.zip"] = pak;
        var server = Server(engine, pak);

        var plan = _sync.Plan(server, new LauncherSettings());
        Assert.All(plan.Items, i => Assert.True(i.Needed));
        await _sync.SyncAsync(plan);

        var engineDir = _layout.EngineDir("r1");
        // zip の最上位のフォルダ1つは取り除かれ、pakset は実行ファイルの横に入る
        Assert.Equal("engine", File.ReadAllText(Path.Combine(engineDir, "simutrans.exe")));
        Assert.Equal("pak", File.ReadAllText(Path.Combine(engineDir, "pak64", "ground.pak")));
        Assert.Equal(Path.Combine(engineDir, "simutrans.exe"), plan.ExePath);
        Assert.True(File.Exists(_layout.InstalledStatePath));
        Assert.Equal(2, _handler.Requests);

        var again = _sync.Plan(server, new LauncherSettings());
        Assert.True(again.UpToDate);
        await _sync.SyncAsync(again);
        Assert.Equal(2, _handler.Requests);
    }

    [Fact]
    public async Task UpdatesPaksetWhenHashChanges()
    {
        var engine = Zip(("simutrans.exe", "engine"));
        var pak1 = Zip(("old.pak", "1"));
        var pak2 = Zip(("new.pak", "2"));
        _handler.Files["https://x/engine.zip"] = engine;
        _handler.Files["https://x/pak.zip"] = pak1;
        await _sync.SyncAsync(_sync.Plan(Server(engine, pak1), new LauncherSettings()));

        _handler.Files["https://x/pak.zip"] = pak2;
        var plan = _sync.Plan(Server(engine, pak2), new LauncherSettings());
        Assert.False(plan.Items.Single(i => i.Kind == SyncItemKind.Engine).Needed);
        Assert.True(plan.Items.Single(i => i.Kind == SyncItemKind.Pakset).Needed);
        await _sync.SyncAsync(plan);

        var pakDir = Path.Combine(_layout.EngineDir("r1"), "pak64");
        Assert.True(File.Exists(Path.Combine(pakDir, "new.pak")));
        Assert.False(File.Exists(Path.Combine(pakDir, "old.pak")));
        // ランチャーが入れたものは退避せずに消す
        Assert.Empty(Directory.GetDirectories(_layout.EngineDir("r1"), "pak64.backup-*"));
    }

    [Fact]
    public async Task RejectsHashMismatchAndLeavesNothingInstalled()
    {
        var engine = Zip(("simutrans.exe", "engine"));
        var pak = Zip(("a.pak", "a"));
        _handler.Files["https://x/engine.zip"] = Zip(("simutrans.exe", "tampered"));
        _handler.Files["https://x/pak.zip"] = pak;

        var e = await Assert.ThrowsAsync<SyncException>(() => _sync.SyncAsync(_sync.Plan(Server(engine, pak), new LauncherSettings())));
        Assert.Contains("ハッシュ", e.Message);
        Assert.False(Directory.Exists(_layout.EngineDir("r1")));
        Assert.Empty(Directory.GetFiles(_layout.DownloadDir));
    }

    [Fact]
    public async Task RejectsZipSlip()
    {
        var engine = Zip(("simutrans.exe", "engine"));
        var pak = Zip(("../../escaped.txt", "evil"));
        _handler.Files["https://x/engine.zip"] = engine;
        _handler.Files["https://x/pak.zip"] = pak;

        await Assert.ThrowsAsync<SyncException>(() => _sync.SyncAsync(_sync.Plan(Server(engine, pak), new LauncherSettings())));
        Assert.Empty(Directory.GetFiles(_dir, "escaped.txt", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task UsesLocalSimutransAndBacksUpUserPakset()
    {
        // マニフェストにこの OS 用の本体がない場合は、設定の simutrans を使う
        var local = Path.Combine(_dir, "my-simutrans");
        Directory.CreateDirectory(Path.Combine(local, "pak64"));
        File.WriteAllText(Path.Combine(local, "simutrans.exe"), "");
        File.WriteAllText(Path.Combine(local, "pak64", "mine.pak"), "user");

        var pak = Zip(("a.pak", "a"));
        _handler.Files["https://x/pak.zip"] = pak;
        var server = Server([], pak);
        server.Engine = null;

        var plan = _sync.Plan(server, new LauncherSettings { SimutransExe = Path.Combine(local, "simutrans.exe") });
        Assert.Single(plan.Items);
        await _sync.SyncAsync(plan);

        Assert.True(File.Exists(Path.Combine(local, "pak64", "a.pak")));
        // ユーザーが自分で入れていたフォルダは消さずに退避する
        var backup = Assert.Single(Directory.GetDirectories(local, "pak64.backup-*"));
        Assert.True(File.Exists(Path.Combine(backup, "mine.pak")));
    }

    [Fact]
    public void FailsClearlyWithoutEngine()
    {
        var server = Server([], Zip(("a", "a")));
        server.Engine = null;
        var e = Assert.Throws<SyncException>(() => _sync.Plan(server, new LauncherSettings()));
        Assert.Contains(PlatformInfo.CurrentKey, e.Message);
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Files { get; } = new();
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            return Task.FromResult(Files.TryGetValue(request.RequestUri!.ToString(), out var data)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    [Fact]
    public void RefusesEngineFromUntrustedList()
    {
        var server = Server(Zip(("simutrans.exe", "x")), Zip(("a.pak", "a")));
        server.EngineDownloadAllowed = false;
        var e = Assert.Throws<SyncException>(() => _sync.Plan(server, new LauncherSettings()));
        Assert.Contains("確認コード", e.Message);

        // 手元の simutrans があればそれを使い、本体はダウンロードしない
        var local = Path.Combine(_dir, "mine", "simutrans.exe");
        var plan = _sync.Plan(server, new LauncherSettings { SimutransExe = local });
        Assert.DoesNotContain(plan.Items, i => i.Kind == SyncItemKind.Engine);
        Assert.Equal(Path.GetFullPath(local), plan.ExePath);
    }
}
