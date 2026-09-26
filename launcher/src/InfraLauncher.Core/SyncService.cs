using System.IO.Compression;
using System.Security.Cryptography;
using InfraLauncher.Core.Models;

namespace InfraLauncher.Core;

public sealed class SyncException(string message, Exception? inner = null) : Exception(message, inner);

public enum SyncItemKind { Engine, Pakset }

/// <summary>1つのダウンロード対象（本体または pakset）と、その展開先。</summary>
public sealed record SyncItem(SyncItemKind Kind, string Label, string Url, string Sha256, string TargetDir, bool Needed);

/// <summary>サーバー1つ分の同期計画。</summary>
public sealed record SyncPlan(ServerEntry Server, string ExePath, string PaksetFolder, IReadOnlyList<SyncItem> Items)
{
    public bool UpToDate => Items.All(i => !i.Needed);
}

public sealed record SyncProgress(SyncItem Item, string Stage, long BytesDone, long? BytesTotal);

/// <summary>
/// マニフェストと installed.json を比べ、必要なものだけをダウンロード・展開する。
/// 手順: ダウンロード → sha256 確認 → 展開先の横の一時フォルダに展開 → 置き換え。
/// </summary>
public sealed class SyncService(InstallLayout layout, HttpClient http)
{
    public SyncPlan Plan(ServerEntry server, LauncherSettings settings)
    {
        var state = InstalledState.Load(layout);
        var items = new List<SyncItem>();
        string exe;

        var build = server.Engine?.Builds?.GetValueOrDefault(PlatformInfo.CurrentKey);
        if (build is not null)
        {
            var engineDir = layout.EngineDir(server.Engine!.Revision);
            exe = Path.GetFullPath(Path.Combine(engineDir, build.Exe));
            EnsureInside(engineDir, exe, "engine の exe");
            var needed = !File.Exists(exe) || !SameHash(state.Get(engineDir), build.Sha256);
            items.Add(new SyncItem(SyncItemKind.Engine, $"simutrans {server.Engine.Revision}", build.Url, build.Sha256, engineDir, needed));
        }
        else if (!string.IsNullOrWhiteSpace(settings.SimutransExe))
        {
            exe = Path.GetFullPath(settings.SimutransExe);
        }
        else
        {
            throw new SyncException(
                $"サーバー '{server.Name}' のサーバーリストには、この PC（{PlatformInfo.CurrentKey}）用の simutrans 本体が含まれていません。" +
                "「設定」で手元の simutrans 本体を指定してください");
        }

        var p = server.Pakset;
        var paksetDir = Path.Combine(SimutransPaths.DataDirFor(exe), p.Folder);
        var paksetNeeded = !Directory.Exists(paksetDir) || !SameHash(state.Get(paksetDir), p.Sha256);
        var label = string.IsNullOrEmpty(p.Version) ? p.Name : $"{p.Name} {p.Version}";
        items.Add(new SyncItem(SyncItemKind.Pakset, label, p.Url, p.Sha256, paksetDir, paksetNeeded));

        return new SyncPlan(server, exe, p.Folder, items);
    }

    public async Task SyncAsync(SyncPlan plan, IProgress<SyncProgress>? progress = null, CancellationToken ct = default)
    {
        // 本体を先に入れる。本体を入れ直すと中の pakset も消えるので、そのあと pakset を判定し直す
        foreach (var item in plan.Items.OrderBy(i => i.Kind))
        {
            var state = InstalledState.Load(layout);
            var needed = item.Needed || !Directory.Exists(item.TargetDir) || !SameHash(state.Get(item.TargetDir), item.Sha256);
            if (!needed)
            {
                continue;
            }
            await InstallAsync(item, progress, ct);
        }
    }

    private async Task InstallAsync(SyncItem item, IProgress<SyncProgress>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(layout.DownloadDir);
        var zipPath = Path.Combine(layout.DownloadDir, $"{item.Sha256.ToLowerInvariant()}.zip");
        try
        {
            await DownloadAsync(item, zipPath, progress, ct);
            progress?.Report(new SyncProgress(item, "展開中", 0, null));
            Extract(item, zipPath);
        }
        finally
        {
            TryDelete(zipPath);
        }
        progress?.Report(new SyncProgress(item, "完了", 0, null));
    }

    private async Task DownloadAsync(SyncItem item, string zipPath, IProgress<SyncProgress>? progress, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            var uri = new Uri(item.Url);
            HttpResponseMessage? response = null;
            Stream source;
            long? total;
            if (uri.IsFile)
            {
                source = File.OpenRead(uri.LocalPath);
                total = source.Length;
            }
            else
            {
                response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();
                source = await response.Content.ReadAsStreamAsync(ct);
                total = response.Content.Headers.ContentLength;
            }

            using (response)
            await using (source)
            await using (var dest = File.Create(zipPath))
            {
                var buffer = new byte[81920];
                long done = 0;
                int n;
                progress?.Report(new SyncProgress(item, "ダウンロード中", 0, total));
                while ((n = await source.ReadAsync(buffer, ct)) > 0)
                {
                    hash.AppendData(buffer, 0, n);
                    await dest.WriteAsync(buffer.AsMemory(0, n), ct);
                    done += n;
                    progress?.Report(new SyncProgress(item, "ダウンロード中", done, total));
                }
            }
        }
        catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new SyncException($"{item.Label} をダウンロードできませんでした: {item.Url} ({e.Message})", e);
        }

        var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
        if (!actual.Equals(item.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new SyncException(
                $"{item.Label} のハッシュがサーバーリストの記載と一致しません。ダウンロードが壊れているか、サーバーリストが古い可能性があります。サーバー管理者に確認してください。" +
                $"（期待: {item.Sha256.ToLowerInvariant()}、実際: {actual}）");
        }
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
            state.Set(target, new InstalledRecord { Sha256 = item.Sha256.ToLowerInvariant(), Url = item.Url, InstalledAt = DateTimeOffset.Now });
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

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
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
