using System.IO.Compression;
using System.Security.Cryptography;
using InfraLauncher.Core.Models;

namespace InfraLauncher.Core;

public class SyncException(string message, Exception? inner = null) : Exception(message, inner);

public enum SyncItemKind { Engine, Pakset }

/// <summary>zipを丸ごと入れ替えるか、ファイル一覧で差分だけを落とすか。</summary>
public enum SyncMethod { Zip, FileIndex }

/// <summary>
/// 1つのダウンロード対象（本体またはpakset）と、その展開先。
/// ファイル一覧方式ではUrlとSha256は一覧ファイル（index.json）のもの。
/// </summary>
/// Componentsは本体の部品の選び方（nullなら推奨）。Selectionはそれを文字列にしたもの（記録と比べる）。
/// DeleteUnknownは一覧にないファイルを片付けるか（paksetは片付ける。本体はランチャーが入れたものだけ片付ける）。
public sealed record SyncItem(SyncItemKind Kind, string Label, string Url, string Sha256, string TargetDir, bool Needed,
    SyncMethod Method = SyncMethod.Zip, string? ExePath = null,
    IReadOnlyCollection<string>? Components = null, string? Selection = null, bool DeleteUnknown = true);

/// <summary>サーバー1つ分の同期計画。</summary>
public sealed record SyncPlan(ServerEntry Server, string ExePath, string PaksetFolder, IReadOnlyList<SyncItem> Items)
{
    public bool UpToDate => Items.All(i => !i.Needed);
}

public sealed record SyncProgress(SyncItem Item, string Stage, long BytesDone, long? BytesTotal);

/// <summary>同期の結果。Downloadsはダウンロードしたファイル（zip）の数、Removedは片付けたファイルの数。</summary>
/// <summary>アップデートチェックの結果。UpToDateでなければ、落とすファイルの数と大きさ（zip方式なら大きさは0）と片付けるファイルの数。</summary>
public sealed record CheckResult(SyncItem Item, bool UpToDate, int Files, long Bytes, int Removals);

public sealed record SyncSummary(int Downloads, long Bytes, int Removed)
{
    public SyncSummary Add(SyncSummary o) => new(Downloads + o.Downloads, Bytes + o.Bytes, Removed + o.Removed);
}

/// <summary>
/// サーバーリストとinstalled.jsonを比べ、必要なものだけをダウンロード・展開する。
/// zip方式: ダウンロード →sha256確認 → 展開先の横の一時フォルダに展開 → 置き換え。
/// ファイル一覧方式: <see cref="FileIndexSync"/>を参照。
/// </summary>
public sealed class SyncService(InstallLayout layout, HttpClient http)
{
    private readonly FileIndexSync _fileIndex = new(layout, http);

    /// <summary>ダウンロード先のフォルダ。サーバーごとの設定 → 全体の設定 → 既定の順。</summary>
    public string InstallRoot(LauncherSettings settings, InstallOptions? options) =>
        Path.GetFullPath(options?.InstallRoot is { Length: > 0 } r ? r
            : settings.InstallRoot is { Length: > 0 } g ? g
            : layout.DefaultInstallRoot);

    /// <summary>本体のファイル一覧（部品の一覧とサイズを画面に出すため）。本体を落とせないサーバーならnull。</summary>
    public async Task<PaksetIndex?> LoadEngineIndexAsync(ServerEntry server, CancellationToken ct = default)
    {
        var build = server.Engine?.Builds?.GetValueOrDefault(PlatformInfo.CurrentKey);
        if (build is not { UsesFileIndex: true } || !server.EngineDownloadAllowed)
        {
            return null;
        }
        return await _fileIndex.LoadIndexAsync(build.IndexUrl!, build.IndexSha256!, $"simutrans {server.Engine!.Revision}", build.Exe, ct);
    }

    /// <summary>ダウンロード先を変えたとき、前のダウンロード先にあるこのサーバーの本体のフォルダ（変わっていなければnull）。</summary>
    public string? PreviousInstallDir(ServerEntry server, LauncherSettings settings, InstallOptions? before, InstallOptions? after)
    {
        var oldRoot = InstallRoot(settings, before);
        return string.Equals(oldRoot, InstallRoot(settings, after), StringComparison.OrdinalIgnoreCase) || server.Engine is null
            ? null
            : Path.Combine(oldRoot, server.Engine.Revision);
    }

    /// <summary>
    /// フォルダの中にある、ランチャーが入れたもの以外のファイル（セーブデータやスクリーンショット、書き換えた設定ファイルなど）。
    /// 片付ける前に、バックアップを取るよう案内するために使う。
    /// </summary>
    public IReadOnlyList<string> UserFilesIn(string dir)
    {
        if (!Directory.Exists(dir))
        {
            return [];
        }
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
        var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, record) in InstalledState.Load(layout).Items.Where(kv => kv.Key.Equals(full, StringComparison.OrdinalIgnoreCase)
                     || kv.Key.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
        {
            // config/simuconf.tabは照合の記録に入れていないが、ランチャーが入れたものとして扱う
            foreach (var rel in (record.Files?.Keys ?? Enumerable.Empty<string>()).Concat(FileIndexSync.PreservedFiles))
            {
                installed.Add(Path.GetFullPath(Path.Combine(key, rel.Replace('/', Path.DirectorySeparatorChar))));
            }
        }
        try
        {
            return Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories).Where(f => !installed.Contains(f)).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// 使わなくなったフォルダ（前のダウンロード先）の記録と、ダウンロード途中の一時ファイルを捨てる。以後は使い回しにも使わない。
    /// <paramref name="deleteFiles"/>なら、ランチャーが入れたファイル（記録にあるもの）も消し、空になったフォルダを消す。
    /// 自分で置いたファイル（セーブデータなど）は記録にないので消さない。フォルダが残ったらtrue。
    /// </summary>
    public bool ForgetInstall(string dir, bool deleteFiles)
    {
        var state = InstalledState.Load(layout);
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
        foreach (var (key, record) in state.Items.Where(kv => kv.Key.Equals(full, StringComparison.OrdinalIgnoreCase)
                     || kv.Key.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            FileIndexSync.TryDeleteDir(FileIndexSync.StagingDirFor(layout, key));
            if (deleteFiles)
            {
                foreach (var rel in (record.Files?.Keys ?? Enumerable.Empty<string>()).Concat(FileIndexSync.PreservedFiles))
                {
                    Downloader.TryDelete(Path.Combine(key, rel.Replace('/', Path.DirectorySeparatorChar)));
                }
            }
        }
        state.RemoveUnder(full);
        state.Save(layout);
        if (deleteFiles && Directory.Exists(full))
        {
            try
            {
                FileIndexSync.RemoveEmptyDirectories(full);
                if (!Directory.EnumerateFileSystemEntries(full).Any())
                {
                    Directory.Delete(full);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return Directory.Exists(full);
    }

    /// <summary>
    /// 残骸を片付ける。消されたフォルダの記録と、長いあいだ使われていない一時ファイル（ダウンロード途中のもの、取得済みのファイル一覧）を捨てる。
    /// </summary>
    private void CleanUp()
    {
        var state = InstalledState.Load(layout);
        if (state.RemoveMissing())
        {
            state.Save(layout);
        }
        var limit = DateTime.Now.AddDays(-14);
        try
        {
            var partial = Path.Combine(layout.DownloadDir, "partial");
            if (Directory.Exists(partial))
            {
                foreach (var dir in Directory.EnumerateDirectories(partial).Where(d => Directory.GetLastWriteTime(d) < limit).ToList())
                {
                    FileIndexSync.TryDeleteDir(dir);
                }
            }
            var indexes = Path.Combine(layout.Root, "indexes");
            if (Directory.Exists(indexes))
            {
                foreach (var file in Directory.EnumerateFiles(indexes).Where(f => File.GetLastWriteTime(f) < limit.AddDays(-16)).ToList())
                {
                    Downloader.TryDelete(file);
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>paksetのファイル一覧（全体のサイズを画面に出すため）。zip方式のpaksetならnull。</summary>
    public async Task<PaksetIndex?> LoadPaksetIndexAsync(ServerEntry server, CancellationToken ct = default)
    {
        var p = server.Pakset;
        return p.UsesFileIndex ? await _fileIndex.LoadIndexAsync(p.IndexUrl!, p.IndexSha256!, $"pakset {p.DisplayName}", null, ct) : null;
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
            EnsureInside(engineDir, exe, "engineのexe");
            var record = state.Get(engineDir);
            // インストール後に実行ファイルが書き換えられていたら、入れ直す
            var exeRel = Path.GetRelativePath(engineDir, exe).Replace('\\', '/');
            var exeChanged = record?.Files?.FirstOrDefault(kv => string.Equals(kv.Key, exeRel, StringComparison.OrdinalIgnoreCase)).Value is { } stamp
                && !stamp.Matches(new FileInfo(exe));
            var engineLabel = $"simutrans本体{Path.GetFileName(build.Exe)}";
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
                $"サーバー '{server.Name}' のサーバーリストの確認コードをまだ登録していないため、安全のためsimutrans本体は自動で入れません。" +
                "「編集」でサーバー管理者から聞いた確認コードを入力するか、「設定」で手元のsimutrans本体を指定してください");
        }
        else
        {
            throw new SyncException(
                $"サーバー '{server.Name}' のサーバーリストには、このPC（{PlatformInfo.CurrentKey}）用のsimutrans本体が含まれていません。" +
                "「設定」で手元のsimutrans本体を指定してください");
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

    /// <summary>
    /// アップデートチェック。サーバーと同じにするのに何を落とす必要があるかを確かめるだけで、何も書き換えない。
    /// ファイル一覧方式なら手元のファイルを1つずつ照合する（記録と違うファイルはハッシュを計算し直す）。
    /// </summary>
    public async Task<IReadOnlyList<CheckResult>> CheckAsync(SyncPlan plan, IProgress<SyncProgress>? progress = null, CancellationToken ct = default)
    {
        var results = new List<CheckResult>();
        var state = InstalledState.Load(layout);
        foreach (var item in plan.Items.OrderBy(i => i.Kind))
        {
            if (item.Method == SyncMethod.FileIndex)
            {
                results.Add(await _fileIndex.CheckAsync(item, progress, ct));
                continue;
            }
            var needed = item.Needed || !Directory.Exists(item.TargetDir) || !SameHash(state.Get(item.TargetDir), item.Sha256)
                || item.ExePath is not null && !File.Exists(item.ExePath);
            results.Add(new CheckResult(item, !needed, needed ? 1 : 0, 0, 0));
        }
        return results;
    }

    public async Task<SyncSummary> SyncAsync(SyncPlan plan, IProgress<SyncProgress>? progress = null, CancellationToken ct = default)
    {
        var summary = new SyncSummary(0, 0, 0);
        // 消されたフォルダ（以前のダウンロード先など）の記録や、古い一時ファイルを片付ける
        CleanUp();
        // 本体を先に入れる。本体を入れ直すと中のpaksetも消えるので、そのあとpaksetを判定し直す
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
                    // zip slip対策: 一時フォルダの外に書き出すエントリは拒否する
                    EnsureInside(staging, dest, $"{item.Label}のzipのエントリ '{entry.FullName}'");
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
    /// 展開先を置き換える。ランチャーが入れたフォルダなら消し、そうでないもの（ユーザーが自分で入れたpaksetなど）は
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

    /// <summary>zipの最上位がフォルダ1つだけなら、そのフォルダを返す（公式配布のzipはpak128.japan/などで包まれているため）。</summary>
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
        // zipによっては実行権限が残らないので、本体フォルダ直下の拡張子なしファイルなどに付けておく
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
            throw new SyncException($"{what}が展開先の外を指しています");
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
