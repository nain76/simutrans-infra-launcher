using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using InfraLauncher.Core;
using InfraLauncher.Core.Models;

namespace InfraLauncher.Core.Tests;

public sealed class EngineComponentsTests : IDisposable
{
    private const string Base = "https://x/engine/r1/";
    private readonly string _dir = Directory.CreateTempSubdirectory("infralauncher-comp-").FullName;
    private readonly Server _server = new();
    private readonly InstallLayout _layout;
    private readonly LauncherService _service;

    public EngineComponentsTests()
    {
        _layout = new InstallLayout(Path.Combine(_dir, "data"));
        _service = new LauncherService(_layout, new HttpClient(_server));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static string Sha(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

    private static readonly IndexComponent[] Components =
    [
        new() { Id = "core", Name = "本体と設定", Required = true },
        new() { Id = "music", Name = "音楽", Recommended = true },
        new() { Id = "maps", Name = "マップ画像" },
    ];

    /// <summary>本体のファイルを置き、部品付きの一覧を作って、それを指すサーバー情報を返す。</summary>
    private ServerEntry Publish(params (string Path, string Content, string Component)[] files)
    {
        var index = new PaksetIndex { SchemaVersion = 1, Components = Components.ToList() };
        foreach (var (path, content, component) in files)
        {
            var bytes = Encoding.UTF8.GetBytes(content);
            _server.Files[Base + path] = bytes;
            index.Files.Add(new PaksetFile { Path = path, Size = bytes.Length, Sha256 = Sha(bytes), Component = component });
        }
        var json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(index, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }));
        _server.Files[Base + "index.json"] = json;

        var pak = Encoding.UTF8.GetBytes("pak");
        _server.Files["https://x/pak/a.pak"] = pak;
        var pakIndex = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            new PaksetIndex { SchemaVersion = 1, Files = [new PaksetFile { Path = "a.pak", Size = pak.Length, Sha256 = Sha(pak) }] },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }));
        _server.Files["https://x/pak/index.json"] = pakIndex;

        return new ServerEntry
        {
            Id = "s", Name = "鯖", Address = "h:1", EngineDownloadAllowed = true,
            Engine = new EngineInfo
            {
                Revision = "r1",
                Builds = new() { [PlatformInfo.CurrentKey] = new EngineBuild { IndexUrl = Base + "index.json", IndexSha256 = Sha(json), Exe = "sim.exe" } },
            },
            Pakset = new PaksetInfo { Name = "p", Folder = "p", IndexUrl = "https://x/pak/index.json", IndexSha256 = Sha(pakIndex) },
        };
    }

    private ServerEntry Default() => Publish(
        ("sim.exe", "exe", "core"), ("config/simuconf.tab", "conf", "core"),
        ("music/a.ogg", "music", "music"), ("maps/map.png", "map", "maps"));

    [Fact]
    public async Task KeepsLocallyEditedSimuconf()
    {
        var server = Default();
        var settings = new LauncherSettings();
        await _service.SyncServerAsync(server, settings);
        var dir = Path.Combine(_layout.DefaultInstallRoot, "r1");
        var conf = Path.Combine(dir, "config", "simuconf.tab");
        File.WriteAllText(conf, "my settings");
        var plan = _service.Sync.Plan(server, settings);
        Assert.All(await _service.Sync.CheckAsync(plan), r => Assert.True(r.UpToDate));
        await _service.SyncServerAsync(server, settings);
        Assert.Equal("my settings", File.ReadAllText(conf));

        // 消したら入れ直す
        File.Delete(conf);
        await _service.SyncServerAsync(server, settings);
        Assert.Equal("conf", File.ReadAllText(conf));
    }

    [Fact]
    public async Task UpdateCheckReportsWithoutChangingFiles()
    {
        var server = Default();
        var settings = new LauncherSettings();
        var first = (await _service.Sync.CheckAsync(_service.Sync.Plan(server, settings))).Single(r => r.Item.Kind == SyncItemKind.Engine);
        Assert.False(first.UpToDate);
        Assert.Equal(3, first.Files);
        Assert.False(Directory.Exists(Path.Combine(_layout.DefaultInstallRoot, "r1")));

        await _service.SyncServerAsync(server, settings);
        var music = Path.Combine(_layout.DefaultInstallRoot, "r1", "music", "a.ogg");
        File.WriteAllText(music, "changed!");
        var results = await _service.Sync.CheckAsync(_service.Sync.Plan(server, settings));
        Assert.True(results.Single(r => r.Item.Kind == SyncItemKind.Pakset).UpToDate);
        var check = results.Single(r => r.Item.Kind == SyncItemKind.Engine);
        Assert.False(check.UpToDate);
        Assert.Equal(1, check.Files);
        Assert.Equal("changed!", File.ReadAllText(music));
    }

    [Fact]
    public async Task UpdateCheckReportsProgress()
    {
        var server = Default();
        var settings = new LauncherSettings();
        await _service.SyncServerAsync(server, settings);
        var reports = new List<SyncProgress>();
        await _service.Sync.CheckAsync(_service.Sync.Plan(server, settings), new ListProgress(reports));
        var counted = reports.Where(r => r.BytesTotal is > 0).ToList();
        Assert.NotEmpty(counted);
        Assert.Contains(counted, r => r.Stage.StartsWith("確認中"));
        Assert.All(counted, r => Assert.InRange(r.BytesDone, 0, r.BytesTotal!.Value));
    }

    private sealed class ListProgress(List<SyncProgress> list) : IProgress<SyncProgress>
    {
        public void Report(SyncProgress value) => list.Add(value);
    }

    [Fact]
    public async Task ListsUserFilesBeforeCleaningUp()
    {
        var server = Default();
        await _service.SyncServerAsync(server, new LauncherSettings());
        var dir = Path.Combine(_layout.DefaultInstallRoot, "r1");
        Directory.CreateDirectory(Path.Combine(dir, "save"));
        File.WriteAllText(Path.Combine(dir, "save", "my.sve"), "x");
        File.WriteAllText(Path.Combine(dir, "config", "simuconf.tab"), "edited");
        var files = _service.Sync.UserFilesIn(dir);
        Assert.Equal(Path.Combine(dir, "save", "my.sve"), Assert.Single(files));
    }

    [Fact]
    public async Task RecommendedGetsRequiredAndRecommended()
    {
        var server = Default();
        await _service.SyncServerAsync(server, new LauncherSettings());
        var dir = _layout.EngineDir("r1");
        Assert.True(File.Exists(Path.Combine(dir, "sim.exe")));
        Assert.True(File.Exists(Path.Combine(dir, "config", "simuconf.tab")));
        Assert.True(File.Exists(Path.Combine(dir, "music", "a.ogg")));
        Assert.False(File.Exists(Path.Combine(dir, "maps", "map.png")));
        Assert.True(File.Exists(Path.Combine(dir, "p", "a.pak")));
        Assert.True(_service.Sync.Plan(server, new LauncherSettings()).UpToDate);
    }

    [Fact]
    public async Task CustomSelectionAddsAndRemovesOnlyOwnFiles()
    {
        var server = Default();
        var settings = new LauncherSettings();
        await _service.SyncServerAsync(server, settings);
        var dir = _layout.EngineDir("r1");
        File.WriteAllText(Path.Combine(dir, "my-notes.txt"), "user file");

        // カスタム: 音楽を外してマップを足す
        var custom = new InstallOptions { Components = ["maps"] };
        Assert.False(_service.Sync.Plan(server, settings, custom).UpToDate);
        await _service.SyncServerAsync(server, settings, options: custom);

        Assert.True(File.Exists(Path.Combine(dir, "maps", "map.png")));
        Assert.False(File.Exists(Path.Combine(dir, "music", "a.ogg")));
        Assert.True(File.Exists(Path.Combine(dir, "sim.exe")));
        // ランチャーが入れていないファイルと、中の pakset は触らない
        Assert.Equal("user file", File.ReadAllText(Path.Combine(dir, "my-notes.txt")));
        Assert.True(File.Exists(Path.Combine(dir, "p", "a.pak")));
        Assert.True(_service.Sync.Plan(server, settings, custom).UpToDate);
        // 推奨に戻すと、また同期が必要になる
        Assert.False(_service.Sync.Plan(server, settings).UpToDate);
    }

    [Fact]
    public async Task InstallsIntoChosenFolder()
    {
        var server = Default();
        var root = Path.Combine(_dir, "games", "simutrans");
        await _service.SyncServerAsync(server, new LauncherSettings(), options: new InstallOptions { InstallRoot = root });
        Assert.True(File.Exists(Path.Combine(root, "r1", "sim.exe")));
        Assert.True(File.Exists(Path.Combine(root, "r1", "p", "a.pak")));

        // 全体の既定のフォルダも使える
        var global = Path.Combine(_dir, "global");
        await _service.SyncServerAsync(server, new LauncherSettings { InstallRoot = global });
        Assert.True(File.Exists(Path.Combine(global, "r1", "sim.exe")));
        var info = _service.PrepareLaunch(server, new LauncherSettings { InstallRoot = global });
        Assert.Equal(Path.Combine(global, "r1", "sim.exe"), info.ExePath);
    }

    [Fact]
    public async Task ForgetsPreviousFolderWhenChanged()
    {
        var server = Default();
        var settings = new LauncherSettings();
        var before = new InstallOptions { InstallRoot = Path.Combine(_dir, "old") };
        var after = new InstallOptions { InstallRoot = Path.Combine(_dir, "new") };
        await _service.SyncServerAsync(server, settings, options: before);
        var oldDir = Path.Combine(_dir, "old", "r1");
        Assert.NotNull(InstalledState.Load(_layout).Get(oldDir));
        Assert.Null(_service.Sync.PreviousInstallDir(server, settings, before, before));
        Assert.Equal(oldDir, _service.Sync.PreviousInstallDir(server, settings, before, after));

        // 消さないことを選んだら、記録だけ捨ててファイルは残す
        Assert.True(_service.Sync.ForgetInstall(oldDir, deleteFiles: false));
        Assert.Empty(InstalledState.Load(_layout).Items);
        Assert.True(File.Exists(Path.Combine(oldDir, "sim.exe")));
    }

    [Fact]
    public async Task DeletesOnlyFilesTheLauncherInstalled()
    {
        var server = Default();
        var settings = new LauncherSettings();
        var before = new InstallOptions { InstallRoot = Path.Combine(_dir, "old") };
        await _service.SyncServerAsync(server, settings, options: before);
        var oldDir = Path.Combine(_dir, "old", "r1");
        // 自分で置いたセーブデータは消さない
        Directory.CreateDirectory(Path.Combine(oldDir, "save"));
        File.WriteAllText(Path.Combine(oldDir, "save", "my.sve"), "x");

        Assert.True(_service.Sync.ForgetInstall(oldDir, deleteFiles: true));
        Assert.False(File.Exists(Path.Combine(oldDir, "sim.exe")));
        Assert.False(Directory.Exists(Path.Combine(oldDir, "p")));
        Assert.True(File.Exists(Path.Combine(oldDir, "save", "my.sve")));

        // 何も置いていなければ、フォルダごと消える
        await _service.SyncServerAsync(server, settings, options: new InstallOptions { InstallRoot = Path.Combine(_dir, "old2") });
        Assert.False(_service.Sync.ForgetInstall(Path.Combine(_dir, "old2", "r1"), deleteFiles: true));
        Assert.False(Directory.Exists(Path.Combine(_dir, "old2", "r1")));
    }

    [Fact]
    public async Task DropsRecordsOfDeletedFolders()
    {
        var server = Default();
        var settings = new LauncherSettings();
        var old = new InstallOptions { InstallRoot = Path.Combine(_dir, "old") };
        await _service.SyncServerAsync(server, settings, options: old);
        Directory.Delete(Path.Combine(_dir, "old"), recursive: true);
        await _service.SyncServerAsync(server, settings, options: new InstallOptions { InstallRoot = Path.Combine(_dir, "new") });
        Assert.All(InstalledState.Load(_layout).Items.Keys, k => Assert.StartsWith(Path.Combine(_dir, "new"), k));
    }

    [Theory]
    [InlineData("other.exe", "core")]
    [InlineData("config/lib.dll", "core")]
    [InlineData("run.bat", "core")]
    [InlineData("a.txt", "unknown")]
    public async Task RejectsUnsafeEngineIndex(string path, string component)
    {
        var server = Publish(("sim.exe", "exe", "core"), (path, "x", component));
        await Assert.ThrowsAsync<SyncException>(() => _service.SyncServerAsync(server, new LauncherSettings()));
    }

    [Fact]
    public async Task AllowsTopLevelDll()
    {
        var server = Publish(("sim.exe", "exe", "core"), ("SDL2.dll", "dll", "core"));
        await _service.SyncServerAsync(server, new LauncherSettings());
        Assert.True(File.Exists(Path.Combine(_layout.EngineDir("r1"), "SDL2.dll")));
    }

    [Fact]
    public async Task ListsComponentsForTheDialog()
    {
        var index = await _service.Sync.LoadEngineIndexAsync(Default());
        Assert.NotNull(index);
        Assert.Equal(["core", "music", "maps"], index!.Components!.Select(c => c.Id));
        Assert.Equal(2, ComponentSelection.SelectFiles(index, ["maps"]).Count(f => f.Component == "core"));
        Assert.Single(ComponentSelection.SelectFiles(index, ["maps"]), f => f.Component == "maps");
    }

    private sealed class Server : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Files { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(Files.TryGetValue(request.RequestUri!.AbsoluteUri, out var data)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
