using System.IO.Compression;
using System.Security.Cryptography;
using InfraLauncher.Core.Models;

namespace InfraLauncher.Core;

public sealed class SyncException(string message, Exception? inner = null) : Exception(message, inner);

public enum SyncItemKind { Engine, Pakset }

/// <summary>zip を丸ごと入れ替えるか、ファイル一覧で差分だけを落とすか。</summary>
public enum SyncMethod { Zip, FileIndex }

/// <summary>
/// 1つのダウンロード対象（本体または pakset）と、その展開先。
/// ファイル一覧方式では Url と Sha256 は一覧ファイル（index.json）のもの。
/// </summary>
/// Components は本体の部品の選び方（null なら推奨）。Selection はそれを文字列にしたもの（記録と比べる）。
/// DeleteUnknown は一覧にないファイルを片付けるか（pakset は片付ける。本体はランチャーが入れたものだけ片付ける）。
public sealed record SyncItem(SyncItemKind Kind, string Label, string Url, string Sha256, string TargetDir, bool Needed,
    SyncMethod Method = SyncMethod.Zip, string? ExePath = null,
    IReadOnlyCollection<string>? Components = null, string? Selection = null, bool DeleteUnknown = true);

/// <summary>サーバー1つ分の同期計画。</summary>
public sealed record SyncPlan(ServerEntry Server, string ExePath, string PaksetFolder, IReadOnlyList<SyncItem> Items)
{
    public bool UpToDate => Items.All(i => !i.Needed);
}

public sealed record SyncProgress(SyncItem Item, string Stage, long BytesDone, long? BytesTotal);

/// <summary>同期の結果。Downloads はダウンロードしたファイル（zip）の数、Removed は片付けたファイルの数。</summary>
public sealed record SyncSummary(int Downloads, long Bytes, int Removed)
{
    public SyncSummary Add(SyncSummary o) => new(Downloads + o.Downloads, Bytes + o.Bytes, Removed + o.Removed);
}

/// <summary>
/// サーバーリストと installed.json を比べ、必要なものだけをダウンロード・展開する。
/// zip 方式: ダウンロード → sha256 確認 → 展開先の横の一時フォルダに展開 → 置き換え。
/// ファイル一覧方式: <see cref="FileIndexSync"/> を参照。
/// </summary>
public sealed class SyncService(InstallLayout layout, HttpClient http)
{
    private readonly FileIndexSync _fileIndex = new(layout, http);

    /// <summary>ダウンロード先のフォルダ。サーバーごとの設定 → 全体の設定 → 既定の順。</summary>
    public string InstallRoot(LauncherSettings settings, InstallOptions? options) =>
        Path.GetFullPath(options?.InstallRoot is { Length: > 0 } r ? r
            : settings.InstallRoot is { Length: > 0 } g ? g
            : layout.DefaultInstallRoot);

    /// <summary>本体のファイル一覧（部品の一覧とサイズを画面に出すため）。本体を落とせないサーバーなら null。</summary>
    public async Task<PaksetIndex?> LoadEngineIndexAsync(ServerEntry server, CancellationToken ct = default)
    {
        var build = server.Engine?.Builds?.GetValueOrDefault(PlatformInfo.CurrentKey);
        if (build is not { UsesFileIndex: true } || !server.EngineDownloadAllowed)
        {
            return null;
        }
        return await _fileIndex.LoadIndexAsync(build.IndexUrl!, build.IndexSha256!, $"simutrans {server.Engine!.Revision}", build.Exe, ct);
    }

    public SyncPlan Plan(ServerEntry server, LauncherSettings settings, InstallOptions? options = null)
    {
        var state = InstalledState.Load(layout);
        var items = new List<SyncItem>();
        string exe;

        var build = server.Engine?.Builds?.GetValueOrDefault(PlatformInfo.CurrentKey);
        var blockedEngine = build is not null && !server.EngineDownloadAllowed;
        if (blockedEngine)
        {
            build = null;
        }
        if (build is not null)
        {
            var engineDir = Path.Combine(InstallRoot(settings, options), server.Engine!.Revision);
            exe = Path.GetFullPath(Path.Combine(engineDir, build.Exe));
            EnsureInside(engineDir, exe, "engine の exe");
            var record = state.Get(engineDir);
            // インストール後に実行ファイルが書き換えられていたら、入れ直す
            var exeRel = Path.GetRelativePath(engineDir, exe).Replace('\\', '/');
            var exeChanged = record?.Files?.FirstOrDefault(kv => string.Equals(kv.Key, exeRel, StringComparison.OrdinalIgnoreCase)).Value is { } stamp
                && !stamp.Matches(new FileInfo(exe));
            var engineLabel = $"simutrans {server.Engine.Revision}";
            if (build.UsesFileIndex)
            {
                var selection = options?.Components is { } c ? "custom:" + string.Join(',', c.Order(StringComparer.Ordinal)) : "recommended";
                var needed = !File.Exists(exe) || !SameHash(record, build.IndexSha256!) || record?.Selection != selection || exeChanged;
                items.Add(new SyncItem(SyncItemKind.Engine, engineLabel, build.IndexUrl!, build.IndexSha256!, engineDir, needed, SyncMethod.FileIndex,
                    ExePath: exe, Components: options?.Components, Selection: selection, DeleteUnknown: false));
            }
            else
            {
                var needed = !File.Exists(exe) || !SameHash(record, build.Sha256!) || exeChanged;
                items.Add(new SyncItem(SyncItemKind.Engine, engineLabel, build.Url!, build.Sha256!, engineDir, needed, ExePath: exe));
            }
        }
        else if (!string.IsNullOrWhiteSpace(settings.SimutransExe))
        {
            exe = Path.GetFullPath(settings.SimutransExe);
        }
        else if (blockedEngine)
        {
            throw new SyncException(
                $"サーバー '{server.Name}' のサーバーリストが HTTPS ではないため、安全のため simutrans 本体は自動で入れません。" +
                "「編集」で配信アドレスを https:// で始まるものに変えるか、「設定」で手元の simutrans 本体を指定してください");
        }
        else
        {
            throw new SyncException(
                $"サーバー '{server.Name}' のサーバーリストには、この PC（{PlatformInfo.CurrentKey}）用の simutrans 本体が含まれていません。" +
                "「設定」で手元の simutrans 本体を指定してください");
        }

        var p = server.Pakset;
        var paksetDir = Path.Combine(SimutransPaths.DataDirFor(exe), p.Folder);
        var label = p.DisplayName;
        var (url, sha, method) = p.UsesFileIndex
            ? (p.IndexUrl!, p.IndexSha256!, SyncMethod.FileIndex)
            : (p.Url!, p.Sha256!, SyncMethod.Zip);
        var paksetNeeded = !Directory.Exists(paksetDir) || !SameHash(state.Get(paksetDir), sha);
        items.Add(new SyncItem(SyncItemKind.Pakset, label, url, sha, paksetDir, paksetNeeded, method));

        return new SyncPlan(server, exe, p.Folder, items);
    }

    public async Task<SyncSummary> SyncAsync(SyncPlan plan, IProgress<SyncProgress>? progress = null, CancellationToken ct = default)
    {
        var summary = new SyncSummary(0, 0, 0);
        // 本体を先に入れる。本体を入れ直すと中の pakset も消えるので、そのあと pakset を判定し直す
        foreach (var item in plan.Items.OrderBy(i => i.Kind))
        {
            if (item.Method == SyncMethod.FileIndex)
            {
                // 照合は軽いので毎回行い、手元で消えたり書き換わったりしたファイルも直す
                summary = summary.Add(await _fileIndex.SyncAsync(item, progress, ct));
                continue;
            }
            var state = InstalledState.Load(layout);
            var needed = item.Needed || !Directory.Exists(item.TargetDir) || !SameHash(state.Get(item.TargetDir), item.Sha256)
                || item.ExePath is not null && !File.Exists(item.ExePath);
            if (!needed)
            {
                continue;
            }
            summary = summary.Add(await InstallAsync(item, progress, ct));
        }
        return summary;
    }

    private async Task<SyncSummary> InstallAsync(SyncItem item, IProgress<SyncProgress>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(layout.DownloadDir);
        var zipPath = Path.Combine(layout.DownloadDir, $"{item.Sha256.ToLowerInvariant()}.zip");
        long size;
        try
        {
            await DownloadAsync(item, zipPath, progress, ct);
            size = new FileInfo(zipPath).Length;
            progress?.Report(new SyncProgress(item, "展開中", 0, null));
            Extract(item, zipPath);
        }
        finally
        {
            Downloader.TryDelete(zipPath);
        }
        progress?.Report(new SyncProgress(item, "完了", 0, null));
        return new SyncSummary(1, size, 0);
    }

    private async Task DownloadAsync(SyncItem item, string zipPath, IProgress<SyncProgress>? progress, CancellationToken ct)
    {
        long? total = null;
        if (new Uri(item.Url) is { IsFile: true } fileUri && File.Exists(fileUri.LocalPath))
        {
            total = new FileInfo(fileUri.LocalPath).Length;
        }
        long done = 0;
        progress?.Report(new SyncProgress(item, "ダウンロード中", 0, total));
        await Downloader.DownloadAsync(http, item.Url, zipPath, item.Sha256, item.Label,
            n => progress?.Report(new SyncProgress(item, "ダウンロード中", done += n, total)), ct);
    }

    private void Extract(SyncItem item, string zipPath)
    {
        var target = Path.GetFullPath(item.TargetDir);
        var parent = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(parent);
        // 別ドライブへの移動にならないよう、一時フォルダは展開先の横に作る
        var staging = Path.Combine(parent, $".{Path.GetFileName(target)}.partial");
        TryDeleteDir(staging);
        Directory.CreateDirectory(staging);
        try
        {
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                foreach (var entry in zip.Entries)
                {
                    var dest = Path.GetFullPath(Path.Combine(staging, entry.FullName));
                    // zip slip 対策: 一時フォルダの外に書き出すエントリは拒否する
                    EnsureInside(staging, dest, $"{item.Label} の zip のエントリ '{entry.FullName}'");
                    if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                    {
                        Directory.CreateDirectory(dest);
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    entry.ExtractToFile(dest, overwrite: true);
                }
            }

            var content = SingleTopLevelDir(staging) ?? staging;
            var state = InstalledState.Load(layout);
            ReplaceDirectory(target, content, state);
            if (item.Kind == SyncItemKind.Engine)
            {
                MarkExecutables(target);
            }
            var record = new InstalledRecord { Sha256 = item.Sha256.ToLowerInvariant(), Url = item.Url, InstalledAt = DateTimeOffset.Now };
            if (item.ExePath is not null && File.Exists(item.ExePath))
            {
                // 実行ファイルのサイズと更新日時を覚えておき、あとで書き換えられていないか確かめる
                var info = new FileInfo(item.ExePath);
                record.Files = new() { [Path.GetRelativePath(target, item.ExePath).Replace('\\', '/')] = FileStamp.From(info, "") };
            }
            state.Set(target, record);
            state.Save(layout);
        }
        finally
        {
            TryDeleteDir(staging);
        }
    }

    /// <summary>
    /// 展開先を置き換える。ランチャーが入れたフォルダなら消し、そうでないもの（ユーザーが自分で入れた pakset など）は
    /// 名前を変えて残しておく。
    /// </summary>
    private static void ReplaceDirectory(string target, string content, InstalledState state)
    {
        if (Directory.Exists(target))
        {
            if (state.Get(target) is not null)
            {
                Directory.Delete(target, recursive: true);
            }
            else
            {
                var backup = $"{target}.backup-{DateTime.Now:yyyyMMdd-HHmmss}";
                Directory.Move(target, backup);
            }
            state.RemoveUnder(target);
        }
        Directory.Move(content, target);
    }

    /// <summary>zip の最上位がフォルダ1つだけなら、そのフォルダを返す（公式配布の zip は pak128.japan/ などで包まれているため）。</summary>
    private static string? SingleTopLevelDir(string dir)
    {
        var entries = Directory.GetFileSystemEntries(dir);
        return entries.Length == 1 && Directory.Exists(entries[0]) ? entries[0] : null;
    }

    private static void MarkExecutables(string dir)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        // zip によっては実行権限が残らないので、本体フォルダ直下の拡張子なしファイルなどに付けておく
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(f);
            if (name.StartsWith("simutrans", StringComparison.OrdinalIgnoreCase) && !Path.HasExtension(name) || f.Replace('\\', '/').Contains(".app/Contents/MacOS/"))
            {
                File.SetUnixFileMode(f, File.GetUnixFileMode(f) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            }
        }
    }

    private static bool SameHash(InstalledRecord? record, string sha256) =>
        record is not null && record.Sha256.Equals(sha256, StringComparison.OrdinalIgnoreCase);

    private static void EnsureInside(string root, string path, string what)
    {
        var r = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (full != r && !full.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new SyncException($"{what} が展開先の外を指しています");
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
