using InfraLauncher.Core.Models;

namespace InfraLauncher.Core;

/// <summary>
/// ファイル一覧方式の同期。手元のフォルダを一覧と照合し、変わったファイルだけを落とす。
/// 1. 照合: サイズと更新日時が前回の記録と同じならハッシュ計算を省く。違えば計算し直す
/// 2. ダウンロード: 足りないファイルを展開先の横の一時フォルダに落とし、1つずつ SHA256 を確かめる
///    （一時フォルダは中断しても残るので、次回は落とし終えたファイルを使い回す）
/// 3. 反映: 全部そろってから置き換え、一覧にないファイルを片付ける
///    ランチャーが入れたフォルダなら消し、ユーザーが自分で入れたフォルダなら退避する
/// </summary>
internal sealed class FileIndexSync(InstallLayout layout, HttpClient http)
{
    private const int Parallelism = 4;

    public async Task<SyncSummary> SyncAsync(SyncItem item, IProgress<SyncProgress>? progress, CancellationToken ct)
    {
        var target = Path.GetFullPath(item.TargetDir);
        var exeRel = item.ExePath is null ? null : Path.GetRelativePath(target, item.ExePath).Replace('\\', '/');
        var index = await LoadIndexAsync(item.Url, item.Sha256, item.Label, exeRel, ct);
        var indexUri = new Uri(item.Url);
        var wanted = ComponentSelection.SelectFiles(index, item.Components);
        var state = InstalledState.Load(layout);
        var record = state.Get(target);
        var managed = record is not null;
        var stamps = record?.Files ?? new Dictionary<string, FileStamp>();

        // 1. 照合
        progress?.Report(new SyncProgress(item, "確認中", 0, null));
        var newStamps = new Dictionary<string, FileStamp>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<PaksetFile>();
        foreach (var f in wanted)
        {
            var local = LocalPath(target, f.Path);
            var info = new FileInfo(local);
            if (info.Exists && info.Length == f.Size)
            {
                if (stamps.TryGetValue(f.Path, out var st) && st.Matches(info) && SameSha(st.Sha256, f.Sha256))
                {
                    newStamps[f.Path] = st;
                    continue;
                }
                var sha = await Downloader.HashFileAsync(local, ct);
                if (SameSha(sha, f.Sha256))
                {
                    newStamps[f.Path] = FileStamp.From(info, sha);
                    continue;
                }
            }
            missing.Add(f);
        }

        var keep = new HashSet<string>(wanted.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
        List<string> extras;
        if (item.DeleteUnknown)
        {
            // pakset: サーバーと完全に同じにするため、一覧にないファイルは片付ける
            extras = Directory.Exists(target)
                ? Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories)
                    .Select(p => Path.GetRelativePath(target, p).Replace('\\', '/'))
                    .Where(rel => !keep.Contains(rel))
                    .ToList()
                : new List<string>();
        }
        else
        {
            // 本体: フォルダの中には pakset やユーザーのファイルもあるので、ランチャーが入れたファイルだけを片付ける
            extras = stamps.Keys.Where(rel => !keep.Contains(rel) && File.Exists(LocalPath(target, rel))).ToList();
        }

        if (missing.Count == 0 && extras.Count == 0 && managed && SameSha(record!.Sha256, item.Sha256) && record.Selection == item.Selection)
        {
            if (!newStamps.OrderBy(k => k.Key).SequenceEqual(stamps.OrderBy(k => k.Key)))
            {
                SaveRecord(state, target, item, newStamps);
            }
            return new SyncSummary(0, 0, 0);
        }

        // 2. ダウンロード（同じ中身のファイルは1回だけ落とす）
        // 途中のファイルは中身の SHA256 を名前にして一時フォルダに置き、全部そろって確かめてから本来の名前で置く。
        // 一時フォルダはユーザーが触らないよう、Windows では隠しフォルダにする（終われば消す）
        var staging = Path.Combine(Path.GetDirectoryName(target)!, $".{Path.GetFileName(target)}.partial");
        var stagingDir = Directory.CreateDirectory(staging);
        if (OperatingSystem.IsWindows())
        {
            stagingDir.Attributes |= FileAttributes.Hidden;
        }
        var unique = missing.GroupBy(f => f.Sha256.ToLowerInvariant()).Select(g => g.First()).ToList();

        // 手元のほかの pakset（本体の別リビジョン用など）に同じ中身のファイルがあれば、コピーして使う
        var reused = 0;
        var copies = unique.Count > 0 ? LocalCopies(state, target) : new();
        foreach (var f in unique.ToList())
        {
            var tmp = Path.Combine(staging, f.Sha256.ToLowerInvariant());
            var local = copies.GetValueOrDefault(f.Sha256.ToLowerInvariant())?
                .FirstOrDefault(c => c.Stamp.Size == f.Size && c.Stamp.Matches(new FileInfo(c.Path))).Path;
            if (local is null)
            {
                continue;
            }
            File.Copy(local, tmp, overwrite: true);
            if (SameSha(await Downloader.HashFileAsync(tmp, ct), f.Sha256))
            {
                unique.Remove(f);
                reused++;
            }
        }
        if (reused > 0)
        {
            progress?.Report(new SyncProgress(item, $"手元のファイルを {reused} 件使い回しました", 0, null));
        }

        long total = unique.Sum(f => f.Size), done = 0;
        var finished = 0;
        using (var gate = new SemaphoreSlim(Parallelism))
        {
            await Task.WhenAll(unique.Select(async f =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    var tmp = Path.Combine(staging, f.Sha256.ToLowerInvariant());
                    var reusable = File.Exists(tmp) && new FileInfo(tmp).Length == f.Size
                        && SameSha(await Downloader.HashFileAsync(tmp, ct), f.Sha256);
                    if (reusable)
                    {
                        Interlocked.Add(ref done, f.Size);
                    }
                    else
                    {
                        await Downloader.DownloadAsync(http, FileUri(indexUri, f.Path), tmp, f.Sha256, $"{item.Label} の {f.Path}",
                            n => progress?.Report(new SyncProgress(item, $"ダウンロード中 ({Volatile.Read(ref finished)}/{unique.Count})", Interlocked.Add(ref done, n), total)),
                            ct);
                    }
                    Interlocked.Increment(ref finished);
                }
                finally
                {
                    gate.Release();
                }
            }));
        }

        // 3. 反映
        progress?.Report(new SyncProgress(item, "反映中", total, total));
        string? backup = null;
        void Discard(string rel)
        {
            var local = LocalPath(target, rel);
            if (!File.Exists(local))
            {
                return;
            }
            if (managed)
            {
                File.Delete(local);
                return;
            }
            backup ??= $"{target}.backup-{DateTime.Now:yyyyMMdd-HHmmss}";
            var dest = LocalPath(backup, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Move(local, dest, overwrite: true);
        }

        Directory.CreateDirectory(target);
        foreach (var f in missing)
        {
            var local = LocalPath(target, f.Path);
            Discard(f.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(local)!);
            File.Copy(Path.Combine(staging, f.Sha256.ToLowerInvariant()), local, overwrite: true);
            newStamps[f.Path] = FileStamp.From(new FileInfo(local), f.Sha256.ToLowerInvariant());
        }
        foreach (var rel in extras)
        {
            Discard(rel);
        }
        if (item.DeleteUnknown)
        {
            RemoveEmptyDirectories(target);
        }
        if (item.ExePath is not null && !OperatingSystem.IsWindows() && File.Exists(item.ExePath))
        {
            File.SetUnixFileMode(item.ExePath, File.GetUnixFileMode(item.ExePath) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }

        SaveRecord(state, target, item, newStamps);
        TryDeleteDir(staging);
        return new SyncSummary(unique.Count, total, extras.Count);
    }

    /// <summary>ほかの展開先の記録にあるファイルを、SHA256 ごとにまとめる（記録どおり変わっていないかは使う直前に確かめる）。</summary>
    private static Dictionary<string, List<(string Path, FileStamp Stamp)>> LocalCopies(InstalledState state, string target)
    {
        var result = new Dictionary<string, List<(string, FileStamp)>>();
        foreach (var (dir, record) in state.Items)
        {
            if (record.Files is null || string.Equals(Path.TrimEndingDirectorySeparator(dir), target, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            foreach (var (rel, stamp) in record.Files)
            {
                var key = stamp.Sha256.ToLowerInvariant();
                if (!result.TryGetValue(key, out var list))
                {
                    result[key] = list = new();
                }
                list.Add((Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar)), stamp));
            }
        }
        return result;
    }

    /// <summary>一覧ファイルを取得する。同じハッシュのものを取得済みなら使い回す。engineExe を渡すと本体の一覧として検証する。</summary>
    internal async Task<PaksetIndex> LoadIndexAsync(string url, string sha256, string label, string? engineExe, CancellationToken ct)
    {
        var dir = Path.Combine(layout.Root, "indexes");
        Directory.CreateDirectory(dir);
        var cached = Path.Combine(dir, $"{sha256.ToLowerInvariant()}.json");
        if (!File.Exists(cached) || !SameSha(await Downloader.HashFileAsync(cached, ct), sha256))
        {
            var tmp = cached + ".tmp";
            await Downloader.DownloadAsync(http, url, tmp, sha256, $"{label} のファイル一覧", null, ct);
            File.Move(tmp, cached, overwrite: true);
        }
        try
        {
            return ManifestClient.ParseIndex(await File.ReadAllTextAsync(cached, ct), engineExe);
        }
        catch (ManifestException e)
        {
            throw new SyncException(e.Message, e);
        }
    }

    private void SaveRecord(InstalledState state, string target, SyncItem item, Dictionary<string, FileStamp> stamps)
    {
        state.Set(target, new InstalledRecord
        {
            Sha256 = item.Sha256.ToLowerInvariant(),
            Url = item.Url,
            InstalledAt = DateTimeOffset.Now,
            Files = stamps,
            Selection = item.Selection,
        });
        state.Save(layout);
    }

    /// <summary>一覧のパスから手元のパスを作る。一覧は検証済みだが、念のためフォルダの外を指さないことを確かめる。</summary>
    private static string LocalPath(string root, string rel)
    {
        var path = Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new SyncException($"pakset のファイル一覧のパスがフォルダの外を指しています: {rel}");
        }
        return path;
    }

    /// <summary>ファイルのアドレスは一覧ファイルと同じ場所からの相対位置。名前は1つずつエスケープする。</summary>
    private static string FileUri(Uri indexUri, string rel) =>
        new Uri(indexUri, string.Join('/', rel.Split('/').Select(Uri.EscapeDataString))).AbsoluteUri;

    private static bool SameSha(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);

    private static void RemoveEmptyDirectories(string root)
    {
        foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
        {
            if (!Directory.EnumerateFileSystemEntries(dir).Any())
            {
                Directory.Delete(dir);
            }
        }
    }

    private static void TryDeleteDir(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>ファイルの照合用の記録。サイズと更新日時が同じならハッシュを計算し直さない。</summary>
public sealed class FileStamp
{
    public long Size { get; set; }
    public long ModifiedUtcTicks { get; set; }
    public string Sha256 { get; set; } = "";

    public bool Matches(FileInfo info) => info.Length == Size && info.LastWriteTimeUtc.Ticks == ModifiedUtcTicks;

    public static FileStamp From(FileInfo info, string sha256) =>
        new() { Size = info.Length, ModifiedUtcTicks = info.LastWriteTimeUtc.Ticks, Sha256 = sha256 };

    public override bool Equals(object? obj) =>
        obj is FileStamp o && o.Size == Size && o.ModifiedUtcTicks == ModifiedUtcTicks && o.Sha256 == Sha256;

    public override int GetHashCode() => HashCode.Combine(Size, ModifiedUtcTicks, Sha256);
}
