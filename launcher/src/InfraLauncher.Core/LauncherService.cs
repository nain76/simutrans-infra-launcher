using InfraLauncher.Core.Models;

namespace InfraLauncher.Core;

/// <summary>1つのサーバーリストを読んだ結果。取得に失敗した場合は Error に理由が入る。</summary>
public sealed record ManifestSource(ServerListSource List, Manifest? Manifest, string? Error);

/// <summary>CLI と画面の両方から使う、ひととおりの操作をまとめたもの。</summary>
public sealed class LauncherService(InstallLayout layout, HttpClient http)
{
    public InstallLayout Layout => layout;
    public ManifestClient Manifests { get; } = new(http);
    public SyncService Sync { get; } = new(layout, http);

    public LauncherSettings LoadSettings() => LauncherSettings.Load(layout);

    /// <summary>設定にあるサーバーリストを全部読む。1つが失敗しても他は読む。</summary>
    public async Task<IReadOnlyList<ManifestSource>> LoadAllAsync(IEnumerable<ServerListSource> lists, CancellationToken ct = default)
    {
        var tasks = lists.Select(async list =>
        {
            try
            {
                return new ManifestSource(list, await Manifests.LoadAsync(ToUri(list.Url), ct), null);
            }
            catch (Exception e) when (e is ManifestException or UriFormatException or ArgumentException)
            {
                return new ManifestSource(list, null, e.Message);
            }
        });
        return await Task.WhenAll(tasks);
    }

    /// <summary>同期して起動する。起動したプロセスと、実際に使ったコマンドを返す。</summary>
    public async Task<(System.Diagnostics.Process? Process, string Command)> SyncAndLaunchAsync(
        ServerEntry server, LauncherSettings settings, bool printOnly = false,
        IProgress<SyncProgress>? progress = null, CancellationToken ct = default)
    {
        var plan = Sync.Plan(server, settings);
        await Sync.SyncAsync(plan, progress, ct);
        var args = LaunchCommandBuilder.Build(plan.PaksetFolder, ServerAddress.Parse(server.Address));
        var command = LaunchCommandBuilder.ToDisplayString(plan.ExePath, args);
        return (printOnly ? null : SimutransRunner.Start(plan.ExePath, args), command);
    }

    /// <summary>手動プロファイルは同期せず、指定した simutrans でそのまま接続する。</summary>
    public static (System.Diagnostics.Process? Process, string Command) LaunchManual(
        ManualProfile profile, LauncherSettings settings, bool printOnly = false)
    {
        var exe = string.IsNullOrWhiteSpace(profile.SimutransExe) ? settings.SimutransExe : profile.SimutransExe;
        if (string.IsNullOrWhiteSpace(exe))
        {
            throw new SyncException($"プロファイル '{profile.Name}' に simutrans の実行ファイルが指定されていません");
        }
        var args = LaunchCommandBuilder.Build(profile.PaksetFolder, ServerAddress.Parse(profile.Address));
        var command = LaunchCommandBuilder.ToDisplayString(exe, args);
        return (printOnly ? null : SimutransRunner.Start(exe, args), command);
    }

    /// <summary>URL でなければローカルのファイルパスとして扱う。</summary>
    public static Uri ToUri(string urlOrPath) =>
        Uri.TryCreate(urlOrPath, UriKind.Absolute, out var u) && (u.Scheme is "http" or "https" or "file")
            ? u
            : new Uri(Path.GetFullPath(urlOrPath));
}
