using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using InfraLauncher.Core;
using InfraLauncher.Core.Models;

namespace InfraLauncher.Core.Tests;

public sealed class FileIndexSyncTests : IDisposable
{
    private const string Base = "https://x/pak/";
    private readonly string _dir = Directory.CreateTempSubdirectory("infralauncher-index-").FullName;
    private readonly FakeServer _server = new();
    private readonly InstallLayout _layout;
    private readonly SyncService _sync;
    private readonly string _simutrans;

    public FileIndexSyncTests()
    {
        _layout = new InstallLayout(Path.Combine(_dir, "data"));
        _sync = new SyncService(_layout, new HttpClient(_server));
        _simutrans = Path.Combine(_dir, "simutrans");
        Directory.CreateDirectory(_simutrans);
        File.WriteAllText(Path.Combine(_simutrans, "simutrans.exe"), "");
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string PakDir => Path.Combine(_simutrans, "pak128.japan");
    private LauncherSettings Settings => new() { SimutransExe = Path.Combine(_simutrans, "simutrans.exe") };

    private static string Sha(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

    /// <summary>サーバー側のファイルを置き、一覧を作って、それを指すサーバー情報を返す。</summary>
    private ServerEntry Publish(Dictionary<string, string> files)
    {
        _server.Files.Clear();
        var index = new PaksetIndex { SchemaVersion = 1 };
        foreach (var (path, content) in files)
        {
            var bytes = Encoding.UTF8.GetBytes(content);
            _server.Files[Base + string.Join('/', path.Split('/').Select(Uri.EscapeDataString))] = bytes;
            index.Files.Add(new PaksetFile { Path = path, Size = bytes.Length, Sha256 = Sha(bytes) });
        }
        var json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(index, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }));
        _server.Files[Base + "index.json"] = json;
        return new ServerEntry
        {
            Id = "s",
            Name = "鯖",
            Address = "h",
            Pakset = new PaksetInfo { Name = "pak128.japan", Folder = "pak128.japan", IndexUrl = Base + "index.json", IndexSha256 = Sha(json) },
        };
    }

    private Task<SyncSummary> Sync(ServerEntry s) => _sync.SyncAsync(_sync.Plan(s, Settings));

    private int FileRequests => _server.Log.Count(u => !u.EndsWith("index.json"));

    [Fact]
    public async Task DownloadsOnlyChangedFiles()
    {
        var files = new Dictionary<string, string>
        {
            ["ground.pak"] = "ground",
            ["way.pak"] = "way",
            ["config/simuconf.tab"] = "conf",
            ["text/ja.tab"] = "日本語",
        };
        var s1 = Publish(files);
        var plan = _sync.Plan(s1, Settings);
        Assert.Equal(SyncMethod.FileIndex, plan.Items.Single().Method);
        Assert.True(plan.Items.Single().Needed);

        var r1 = await Sync(s1);
        Assert.Equal(4, r1.Downloads);
        Assert.Equal("日本語", File.ReadAllText(Path.Combine(PakDir, "text", "ja.tab")));
        Assert.Equal(4, FileRequests);

        // 2回目は何も落とさない
        Assert.True(_sync.Plan(s1, Settings).UpToDate);
        var r2 = await Sync(s1);
        Assert.Equal(new SyncSummary(0, 0, 0), r2);
        Assert.Equal(4, FileRequests);

        // アドオンを1つ足し、1つ差し替え、1つ消す → 落とすのは2つだけ
        files["addon-train.pak"] = "train";
        files["way.pak"] = "way v2";
        files.Remove("ground.pak");
        _server.Log.Clear();
        var s2 = Publish(files);
        Assert.False(_sync.Plan(s2, Settings).UpToDate);
        var r3 = await Sync(s2);
        Assert.Equal(2, r3.Downloads);
        Assert.Equal(1, r3.Removed);
        Assert.Equal(2, FileRequests);
        Assert.Equal("way v2", File.ReadAllText(Path.Combine(PakDir, "way.pak")));
        Assert.False(File.Exists(Path.Combine(PakDir, "ground.pak")));
        // ランチャーが入れたフォルダは退避せずに消す
        Assert.Empty(Directory.GetDirectories(_simutrans, "pak128.japan.backup-*"));
        Assert.False(Directory.Exists(Path.Combine(_simutrans, ".pak128.japan.partial")));
    }

    [Fact]
    public async Task RepairsLocallyModifiedOrDeletedFiles()
    {
        var s = Publish(new() { ["a.pak"] = "aaaa", ["b.pak"] = "bbbb", ["c.pak"] = "cccc" });
        await Sync(s);
        _server.Log.Clear();

        File.WriteAllText(Path.Combine(PakDir, "a.pak"), "AAAA");   // 同じサイズで中身だけ変える
        File.SetLastWriteTimeUtc(Path.Combine(PakDir, "a.pak"), DateTime.UtcNow.AddMinutes(1));
        File.Delete(Path.Combine(PakDir, "b.pak"));
        File.WriteAllText(Path.Combine(PakDir, "mine.pak"), "x");   // 勝手に足したファイル

        var r = await Sync(s);
        Assert.Equal(2, r.Downloads);
        Assert.Equal(1, r.Removed);
        Assert.Equal("aaaa", File.ReadAllText(Path.Combine(PakDir, "a.pak")));
        Assert.True(File.Exists(Path.Combine(PakDir, "b.pak")));
        Assert.False(File.Exists(Path.Combine(PakDir, "mine.pak")));
    }

    [Fact]
    public async Task ReusesUserFolderAndBacksUpDifferences()
    {
        // ユーザーが自分で入れた pakset: 同じファイルは使い回し、違うものは退避する
        Directory.CreateDirectory(PakDir);
        File.WriteAllText(Path.Combine(PakDir, "same.pak"), "same");
        File.WriteAllText(Path.Combine(PakDir, "old.pak"), "old version");
        File.WriteAllText(Path.Combine(PakDir, "extra.pak"), "extra");

        var s = Publish(new() { ["same.pak"] = "same", ["old.pak"] = "new version", ["new.pak"] = "new" });
        var r = await Sync(s);

        Assert.Equal(2, r.Downloads);
        Assert.DoesNotContain(_server.Log, u => u.EndsWith("same.pak"));
        var backup = Assert.Single(Directory.GetDirectories(_simutrans, "pak128.japan.backup-*"));
        Assert.Equal("old version", File.ReadAllText(Path.Combine(backup, "old.pak")));
        Assert.Equal("extra", File.ReadAllText(Path.Combine(backup, "extra.pak")));
        Assert.Equal(["new.pak", "old.pak", "same.pak"], Directory.GetFiles(PakDir).Select(Path.GetFileName).Order());
    }

    [Fact]
    public async Task RejectsTamperedFileAndLeavesFolderUntouched()
    {
        var s1 = Publish(new() { ["a.pak"] = "a1" });
        await Sync(s1);
        var s2 = Publish(new() { ["a.pak"] = "a2", ["b.pak"] = "b" });
        _server.Files[Base + "b.pak"] = Encoding.UTF8.GetBytes("tampered");

        var e = await Assert.ThrowsAsync<SyncException>(() => Sync(s2));
        Assert.Contains("b.pak", e.Message);
        // 全部そろうまで反映しないので、元のファイルはそのまま
        Assert.Equal("a1", File.ReadAllText(Path.Combine(PakDir, "a.pak")));
        Assert.False(File.Exists(Path.Combine(PakDir, "b.pak")));
    }

    [Fact]
    public async Task ResumesFromPartialDownloads()
    {
        var s1 = Publish(new() { ["a.pak"] = "aaa", ["b.pak"] = "bbb" });
        _server.Files[Base + "b.pak"] = Encoding.UTF8.GetBytes("broken");
        await Assert.ThrowsAsync<SyncException>(() => Sync(s1));

        // 直ったら、落とし終えていた a.pak は落とし直さない
        _server.Files[Base + "b.pak"] = Encoding.UTF8.GetBytes("bbb");
        _server.Log.Clear();
        await Sync(s1);
        Assert.DoesNotContain(_server.Log, u => u.EndsWith("a.pak"));
        Assert.Equal("aaa", File.ReadAllText(Path.Combine(PakDir, "a.pak")));
    }

    [Fact]
    public async Task RejectsTamperedIndex()
    {
        var s = Publish(new() { ["a.pak"] = "a" });
        _server.Files[Base + "index.json"] = Encoding.UTF8.GetBytes("""{"schema_version":1,"files":[]}""");
        var e = await Assert.ThrowsAsync<SyncException>(() => Sync(s));
        Assert.Contains("ファイル一覧", e.Message);
    }

    [Fact]
    public async Task EscapesFileNamesInUrls()
    {
        var s = Publish(new() { ["addons/日本語 車両.pak"] = "x" });
        await Sync(s);
        Assert.True(File.Exists(Path.Combine(PakDir, "addons", "日本語 車両.pak")));
        Assert.Contains(_server.Log, u => u.Contains("%E6%97%A5") && u.Contains("%20"));
    }

    [Fact]
    public async Task ReusesFilesFromAnotherLocalCopy()
    {
        // 本体のリビジョンが変わって pakset の置き場所が変わっても、手元の同じファイルは落とし直さない
        var files = new Dictionary<string, string> { ["a.pak"] = "aaaa", ["b.pak"] = "bbbb" };
        var s = Publish(files);
        await Sync(s);

        var other = Path.Combine(_dir, "simutrans2");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "simutrans.exe"), "");
        files["c.pak"] = "cccc";
        var s2 = Publish(files);
        _server.Log.Clear();

        var r = await _sync.SyncAsync(_sync.Plan(s2, new LauncherSettings { SimutransExe = Path.Combine(other, "simutrans.exe") }));
        Assert.Equal(1, r.Downloads);
        Assert.Equal(1, FileRequests);
        Assert.Equal("aaaa", File.ReadAllText(Path.Combine(other, "pak128.japan", "a.pak")));
        Assert.Equal("cccc", File.ReadAllText(Path.Combine(other, "pak128.japan", "c.pak")));
    }

    [Fact]
    public async Task IgnoresDeletedFoldersWhenReusingFiles()
    {
        // 別の場所に一度入れてから、その場所を消す（ダウンロード先を変えて古いフォルダを消した場合）
        var s = Publish(new() { ["a.pak"] = "aaaa", ["b.pak"] = "bbbb" });
        var old = Path.Combine(_dir, "old");
        Directory.CreateDirectory(old);
        File.WriteAllText(Path.Combine(old, "simutrans.exe"), "");
        await _sync.SyncAsync(_sync.Plan(s, new LauncherSettings { SimutransExe = Path.Combine(old, "simutrans.exe") }));
        Directory.Delete(old, recursive: true);

        var r = await Sync(s);
        Assert.Equal(2, r.Downloads);
        Assert.Equal("bbbb", File.ReadAllText(Path.Combine(PakDir, "b.pak")));
    }

    [Fact]
    public async Task IgnoresDeletedFilesWhenReusingFiles()
    {
        var s = Publish(new() { ["a.pak"] = "aaaa", ["b.pak"] = "bbbb" });
        var old = Path.Combine(_dir, "old");
        Directory.CreateDirectory(old);
        File.WriteAllText(Path.Combine(old, "simutrans.exe"), "");
        await _sync.SyncAsync(_sync.Plan(s, new LauncherSettings { SimutransExe = Path.Combine(old, "simutrans.exe") }));
        File.Delete(Path.Combine(old, "pak128.japan", "a.pak"));

        var r = await Sync(s);
        Assert.Equal(1, r.Downloads);
        Assert.Equal("aaaa", File.ReadAllText(Path.Combine(PakDir, "a.pak")));
    }

    [Fact]
    public async Task RetriesWhenDownloadStalls()
    {
        Downloader.IdleTimeout = TimeSpan.FromMilliseconds(300);
        FileIndexSync.RetryDelay = TimeSpan.Zero;
        try
        {
            var s = Publish(new() { ["a.pak"] = "aaaa", ["big.pak"] = new string('b', 10_000) });
            _server.StallOnce.Add(Base + "big.pak");
            var r = await Sync(s);
            Assert.Equal(2, r.Downloads);
            Assert.Equal(new string('b', 10_000), File.ReadAllText(Path.Combine(PakDir, "big.pak")));
            Assert.Equal(2, _server.Log.Count(u => u.EndsWith("big.pak")));
        }
        finally
        {
            Downloader.IdleTimeout = TimeSpan.FromSeconds(30);
            FileIndexSync.RetryDelay = TimeSpan.FromSeconds(2);
        }
    }

    private sealed class FakeServer : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Files { get; } = new();
        public List<string> Log { get; } = new();
        /// <summary>最初の1回だけ、頭の数バイトを送ったあと止まる URL（通信が途切れたまま返事が来ない状態のまね）。</summary>
        public HashSet<string> StallOnce { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.AbsoluteUri;
            lock (Log)
            {
                Log.Add(url);
            }
            if (Files.TryGetValue(url, out var stalled) && StallOnce.Remove(url))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream(stalled[..10])) });
            }
            return Task.FromResult(Files.TryGetValue(url, out var data)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    /// <summary>最初に渡したバイトを返したあと、取り消されるまで何も返さないストリーム。</summary>
    private sealed class StallingStream(byte[] head) : Stream
    {
        private int _pos;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_pos < head.Length)
            {
                var n = Math.Min(buffer.Length, head.Length - _pos);
                head.AsMemory(_pos, n).CopyTo(buffer);
                _pos += n;
                return n;
            }
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }
    }
}
