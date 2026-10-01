using System.Security.Cryptography;
using InfraLauncher.Core.Models;

namespace InfraLauncher.Core;

/// <summary>
/// ファイル一覧方式の同期。手元のフォルダを一覧と照合し、変わったファイルだけを落とす。
/// 1. 照合: サイズと更新日時が前回の記録と同じならハッシュ計算を省く。違えば計算し直す
/// 2. ダウンロード: 足りないファイルを展開先の横の一時フォルダに落とし、1つずつSHA256を確かめる
///    （一時フォルダは中断しても残るので、次回は落とし終えたファイルを使い回す）
/// 3. 反映: 全部そろってから置き換え、一覧にないファイルを片付ける
///    ランチャーが入れたフォルダなら消し、ユーザーが自分で入れたフォルダなら退避する
/// </summary>
internal sealed class FileIndexSync(InstallLayout layout, HttpClient http)
{
    private const int Parallelism = 4;
    /// <summary>1つのファイルを何回まで試すか（通信が途切れたときにやり直す）。</summary>
    private const int Attempts = 3;
    /// <summary>やり直す前に待つ時間（回数に応じて伸ばす）。</summary>
    internal static TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Windows などが自動で作るファイル。遊ぶのに要らず、IIS は隠しファイルを配信しないので、一覧にあっても落とさない。
    /// 手元にあっても片付けない（エクスプローラーが作り直すため）。
    /// </summary>
    internal static readonly HashSet<string> IgnoredNames = new(StringComparer.OrdinalIgnoreCase) { "desktop.ini", "thumbs.db", "ehthumbs.db", ".ds_store" };

    private static bool IsIgnored(string rel) => IgnoredNames.Contains(rel[(rel.LastIndexOf('/') + 1)..]);

    /// <summary>
    /// 手元で書き換えてよい設定ファイル。config/simuconf.tabはユーザーが自分で書き換えたり、
    /// ランチャーがプレイヤー名を書き込んだりするので、手元になければ入れるが、あれば書き換えない（サーバー側で変わっても上書きしない）。
    /// </summary>
    internal static readonly HashSet<string> PreservedFiles = new(StringComparer.OrdinalIgnoreCase) { "config/simuconf.tab" };

    /// <summary>手元のフォルダと一覧を照合した結果。</summary>
    private sealed record Comparison(string Target, Uri IndexUri, List<PaksetFile> Wanted, InstalledState State, InstalledRecord? Record,
        Dictionary<string, FileStamp> Stamps, Dictionary<string, FileStamp> NewStamps, List<PaksetFile> Missing, List<string> Extras)
    {
        public bool Managed => Record is not null;
    }

    /// <summary>
    /// 同期が必要かを確かめるだけで、何も書き換えない（「アップデートチェック」用）。
    /// 落とすファイルの数と大きさ、片付けるファイルの数を返す。
    /// </summary>
    public async Task<CheckResult> CheckAsync(SyncItem item, IProgress<SyncProgress>? progress, CancellationToken ct)
    {
        var c = await CompareAsync(item, progress, ct);
        var upToDate = c.Missing.Count == 0 && c.Extras.Count == 0 && c.Managed
            && SameSha(c.Record!.Sha256, item.Sha256) && c.Record.Selection == item.Selection;
        return new CheckResult(item, upToDate, c.Missing.Count, c.Missing.Sum(f => f.Size), c.Extras.Count);
    }

    private async Task<Comparison> CompareAsync(SyncItem item, IProgress<SyncProgress>? progress, CancellationToken ct)
    {
        var target = Path.GetFullPath(item.TargetDir);
        var exeRel = item.ExePath is null ? null : Path.GetRelativePath(target, item.ExePath).Replace('\\', '/');
        var index = await LoadIndexAsync(item.Url, item.Sha256, item.Label, exeRel, ct);
        var indexUri = new Uri(item.Url);
        var wanted = ComponentSelection.SelectFiles(index, item.Components).Where(f => !IsIgnored(f.Path)).ToList();
        var state = InstalledState.Load(layout);
        var record = state.Get(target);
        var managed = record is not null;
        var stamps = record?.Files ?? new Dictionary<string, FileStamp>();

        // 1. 照合
        SyncLog.Write($"[{item.Label}]同期を始めます: {target}（一覧{item.Url}、{wanted.Count}ファイル）");
        progress?.Report(new SyncProgress(item, "確認中", 0, null));
        var newStamps = new Dictionary<string, FileStamp>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<PaksetFile>();
        // 照合の進み具合は大きさで出す（大きいファイルはハッシュの計算に時間がかかるため）
        long totalBytes = Math.Max(1, wanted.Sum(f => f.Size)), checkedBytes = 0;
        var checkedFiles = 0;
        foreach (var f in wanted)
        {
            progress?.Report(new SyncProgress(item, $"確認中 {checkedFiles}/{wanted.Count}ファイル", checkedBytes, totalBytes));
            checkedFiles++;
            checkedBytes += f.Size;
            var local = LocalPath(target, f.Path);
            var info = new FileInfo(local);
            if (PreservedFiles.Contains(f.Path) && info.Exists)
            {
                // 手元で書き換えてよい設定ファイル。あればそのまま使い、照合の記録にも入れない
                continue;
            }
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

        SyncLog.Write($"[{item.Label}]照合しました: 足りない・違うファイル{missing.Count}件");
        progress?.Report(new SyncProgress(item, $"確認中 {wanted.Count}/{wanted.Count}ファイル", totalBytes, totalBytes));
        var keep = new HashSet<string>(wanted.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
        List<string> extras;
        if (item.DeleteUnknown)
        {
            // pakset: サーバーと完全に同じにするため、一覧にないファイルは片付ける
            extras = Directory.Exists(target)
                ? Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories)
                    .Select(p => Path.GetRelativePath(target, p).Replace('\\', '/'))
                    .Where(rel => !keep.Contains(rel) && !IsIgnored(rel))
                    .ToList()
                : new List<string>();
        }
        else
        {
            // 本体: フォルダの中にはpaksetやユーザーのファイルもあるので、ランチャーが入れたファイルだけを片付ける
            extras = stamps.Keys.Where(rel => !keep.Contains(rel) && File.Exists(LocalPath(target, rel))).ToList();
        }

        return new Comparison(target, indexUri, wanted, state, record, stamps, newStamps, missing, extras);
    }

    public async Task<SyncSummary> SyncAsync(SyncItem item, IProgress<SyncProgress>? progress, CancellationToken ct)
    {
        var c = await CompareAsync(item, progress, ct);
        var (target, indexUri, state, record, stamps, newStamps, missing, extras, managed) =
            (c.Target, c.IndexUri, c.State, c.Record, c.Stamps, c.NewStamps, c.Missing, c.Extras, c.Managed);

        if (missing.Count == 0 && extras.Count == 0 && managed && SameSha(record!.Sha256, item.Sha256) && record.Selection == item.Selection)
        {
            if (!newStamps.OrderBy(k => k.Key).SequenceEqual(stamps.OrderBy(k => k.Key)))
            {
                SaveRecord(state, target, item, newStamps);
            }
            return new SyncSummary(0, 0, 0);
        }

        // 2. ダウンロード（同じ中身のファイルは1回だけ落とす）
        // 途中のファイルは中身のSHA256を名前にして一時フォルダに置き、全部そろって確かめてから本来の名前で置く。
        // 一時フォルダはランチャーのデータフォルダ（%LOCALAPPDATA%）に置く。ダウンロード先がOneDriveなどの中だと、
        // 書いている途中のファイルを同期ソフトがつかんで止まることがあるため。展開先ごとに決まった場所なので、
        // 途中で止めてもやり直したときに続きから使える（終われば消す）
        TryDeleteDir(Path.Combine(Path.GetDirectoryName(target)!, $".{Path.GetFileName(target)}.partial")); // 以前の版の場所
        var staging = StagingDirFor(layout, target);
        Directory.CreateDirectory(staging);
        var unique = missing.GroupBy(f => f.Sha256.ToLowerInvariant()).Select(g => g.First()).ToList();

        // 手元のほかのpakset（本体の別リビジョン用など）に同じ中身のファイルがあれば、コピーして使う。
        // ただし、OneDriveなどで中身がクラウドにしかないファイル（開くとダウンロードが始まる）は使わない。
        // 取り寄せに時間がかかったり、止まったりするため
        var reused = 0;
        var copies = unique.Count > 0 ? LocalCopies(state, target) : new();
        var checkedCount = 0;
        foreach (var f in unique.ToList())
        {
            ct.ThrowIfCancellationRequested();
            var candidates = copies.GetValueOrDefault(f.Sha256.ToLowerInvariant());
            if (candidates is null)
            {
                continue;
            }
            var local = candidates.FirstOrDefault(c => c.Stamp.Size == f.Size && IsUsableLocalCopy(c.Path, c.Stamp)).Path;
            if (local is null)
            {
                continue;
            }
            if (++checkedCount % 20 == 1)
            {
                progress?.Report(new SyncProgress(item, $"手元にある同じファイルを使い回しています（{checkedCount}件目）", 0, null));
            }
            var tmp = Path.Combine(staging, f.Sha256.ToLowerInvariant());
            try
            {
                File.Copy(local, tmp, overwrite: true);
                if (SameSha(await Downloader.HashFileAsync(tmp, ct), f.Sha256))
                {
                    unique.Remove(f);
                    reused++;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // 使い回せなければダウンロードすればよいので、同期は止めない
                SyncLog.Write($"[{item.Label}]手元のファイルを使い回せませんでした（ダウンロードします）: {local}（{e.Message}）");
            }
        }
        SyncLog.Write($"[{item.Label}]手元のファイルを{reused}件使い回しました。ダウンロードするのは{unique.Count}件です");
        if (reused > 0)
        {
            progress?.Report(new SyncProgress(item, $"手元のファイルを{reused}件使い回しました", 0, null));
        }

        long total = unique.Sum(f => f.Size), done = 0;
        var finished = 0;
        var inFlight = new System.Collections.Concurrent.ConcurrentDictionary<PaksetFile, long>();
        // 何ファイル目か・何MB届いたかを出す（大きいファイルが残っていても止まって見えないように）。
        // 残りが少なくなったら、どのファイルを待っているかも出す（止まったときに原因を調べられるように）
        void Report()
        {
            var left = unique.Count - Volatile.Read(ref finished);
            // 残りのファイルは、届いた量とサーバーリストに書かれた大きさも出す（どこで止まっているか分かるように）
            var waiting = left is > 0 and <= 3 && !inFlight.IsEmpty
                ? "　残り: " + string.Join("、", inFlight.Select(kv => $"{kv.Key.Path}（{kv.Value:N0} / {kv.Key.Size:N0}バイト）"))
                : "";
            progress?.Report(new SyncProgress(item,
                $"ダウンロード中{unique.Count - left}/{unique.Count}ファイル（{FormatSize(Interlocked.Read(ref done))} / {FormatSize(total)}）{waiting}",
                Interlocked.Read(ref done), total));
        }
        using (var gate = new SemaphoreSlim(Parallelism))
        {
            await Task.WhenAll(unique.Select(async f =>
            {
                await gate.WaitAsync(ct);
                inFlight[f] = 0;
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
                        // 通信が途切れたり止まったりしたら、少し待ってやり直す
                        for (var attempt = 1; ; attempt++)
                        {
                            long mine = 0;
                            try
                            {
                                await Downloader.DownloadAsync(http, FileUri(indexUri, f.Path), tmp, f.Sha256, $"{item.Label}の{f.Path}",
                                    n => { mine += n; inFlight[f] = mine; Interlocked.Add(ref done, n); Report(); }, ct);
                                break;
                            }
                            catch (DownloadInterruptedException e) when (attempt < Attempts && !ct.IsCancellationRequested)
                            {
                                SyncLog.Write($"[{item.Label}]やり直します（{attempt + 1}/{Attempts}回目）: {e.Message}");
                                Interlocked.Add(ref done, -mine);
                                progress?.Report(new SyncProgress(item, $"{f.Path}の通信が途切れたので、やり直しています（{attempt + 1}/{Attempts}回目）",
                                    Interlocked.Read(ref done), total));
                                await Task.Delay(RetryDelay * attempt, ct);
                            }
                        }
                    }
                    Interlocked.Increment(ref finished);
                    Report();
                }
                finally
                {
                    inFlight.TryRemove(f, out _);
                    gate.Release();
                }
            }));
        }

        // 3. 反映
        SyncLog.Write($"[{item.Label}]ダウンロードが終わりました。反映します");
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
        var placed = 0;
        foreach (var f in missing)
        {
            ct.ThrowIfCancellationRequested();
            if (++placed % 20 == 0 || placed == missing.Count)
            {
                progress?.Report(new SyncProgress(item, $"反映中{placed}/{missing.Count}ファイル　{f.Path}", placed, missing.Count));
            }
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
        SyncLog.Write($"[{item.Label}]同期が終わりました");
        return new SyncSummary(unique.Count, total, extras.Count);
    }

    /// <summary>ほかの展開先の記録にあるファイルを、SHA256ごとにまとめる（記録どおり変わっていないかは使う直前に確かめる）。</summary>
    /// <summary>
    /// 使い回してよい手元のファイルか。記録どおり変わっておらず、中身が手元にある（クラウドにしかないファイルではない）こと。
    /// </summary>
    private static bool IsUsableLocalCopy(string path, FileStamp stamp)
    {
        var info = new FileInfo(path);
        if (!stamp.Matches(info))
        {
            return false;
        }
        // OneDriveの「オンライン専用」など。0x400000 = FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS、0x40000 = RECALL_ON_OPEN
        const FileAttributes recallOnDataAccess = (FileAttributes)0x400000, recallOnOpen = (FileAttributes)0x40000;
        return (info.Attributes & (FileAttributes.Offline | recallOnDataAccess | recallOnOpen)) == 0;
    }

    private static Dictionary<string, List<(string Path, FileStamp Stamp)>> LocalCopies(InstalledState state, string target)
    {
        var result = new Dictionary<string, List<(string, FileStamp)>>();
        foreach (var (dir, record) in state.Items)
        {
            // 消したフォルダ（以前のダウンロード先など）の記録は使わない
            if (record.Files is null || string.Equals(Path.TrimEndingDirectorySeparator(dir), target, StringComparison.OrdinalIgnoreCase)
                || !Directory.Exists(dir))
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

    /// <summary>一覧ファイルを取得する。同じハッシュのものを取得済みなら使い回す。engineExeを渡すと本体の一覧として検証する。</summary>
    internal async Task<PaksetIndex> LoadIndexAsync(string url, string sha256, string label, string? engineExe, CancellationToken ct)
    {
        var dir = Path.Combine(layout.Root, "indexes");
        Directory.CreateDirectory(dir);
        var cached = Path.Combine(dir, $"{sha256.ToLowerInvariant()}.json");
        if (!File.Exists(cached) || !SameSha(await Downloader.HashFileAsync(cached, ct), sha256))
        {
            var tmp = cached + ".tmp";
            await Downloader.DownloadAsync(http, url, tmp, sha256, $"{label}のファイル一覧", null, ct);
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
            throw new SyncException($"paksetのファイル一覧のパスがフォルダの外を指しています: {rel}");
        }
        return path;
    }

    /// <summary>ファイルのアドレスは一覧ファイルと同じ場所からの相対位置。名前は1つずつエスケープする。</summary>
    private static string FileUri(Uri indexUri, string rel) =>
        new Uri(indexUri, string.Join('/', rel.Split('/').Select(Uri.EscapeDataString))).AbsoluteUri;

    private static bool SameSha(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);

    /// <summary>展開先ごとの、ダウンロード途中のファイルを置く一時フォルダ。</summary>
    internal static string StagingDirFor(InstallLayout layout, string target) =>
        Path.Combine(layout.DownloadDir, "partial",
            Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(target).ToLowerInvariant())))[..16]);

    internal static void RemoveEmptyDirectories(string root)
    {
        foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
        {
            if (!Directory.EnumerateFileSystemEntries(dir).Any())
            {
                Directory.Delete(dir);
            }
        }
    }

    internal static string FormatSize(long bytes) =>
        bytes >= 1024L * 1024 * 1024 ? $"{bytes / 1024.0 / 1024 / 1024:0.0} GB"
        : bytes >= 1024 * 1024 ? $"{bytes / 1024.0 / 1024:0.0} MB"
        : $"{Math.Max(0, bytes) / 1024} KB";

    internal static void TryDeleteDir(string path)
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

    /// <summary>記録どおりか。ファイルがなければfalse（FileInfo.Lengthはファイルがないと例外になるので先に確かめる）。</summary>
    public bool Matches(FileInfo info) => info.Exists && info.Length == Size && info.LastWriteTimeUtc.Ticks == ModifiedUtcTicks;

    public static FileStamp From(FileInfo info, string sha256) =>
        new() { Size = info.Length, ModifiedUtcTicks = info.LastWriteTimeUtc.Ticks, Sha256 = sha256 };

    public override bool Equals(object? obj) =>
        obj is FileStamp o && o.Size == Size && o.ModifiedUtcTicks == ModifiedUtcTicks && o.Sha256 == Sha256;

    public override int GetHashCode() => HashCode.Combine(Size, ModifiedUtcTicks, Sha256);
}
