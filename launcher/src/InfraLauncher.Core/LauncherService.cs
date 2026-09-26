using InfraLauncher.Core.Models;

namespace InfraLauncher.Core;

/// <summary>1つのマニフェストから読んだサーバー。取得に失敗した場合は Error に理由が入る。</summary>
public sealed record ManifestSource(string Url, Manifest? Manifest, string? Error);

/// <summary>CLI と画面の両方から使う、ひととおりの操作をまとめたもの。</summary>
public sealed class LauncherService(InstallLayout layout, HttpClient http)
{
    public InstallLayout Layout => layout;
    public ManifestClient Manifests { get; } = new(http);
    public SyncService Sync { get; } = new(layout, http);

    public LauncherSettings LoadSettings() => LauncherSettings.Load(layout);

    /// <summary>設定にあるマニフェストを全部読む。1つが失敗しても他は読む。</summary>
    public async Task<IReadOnlyList<ManifestSource>> LoadAllAsync(IEnumerable<string> urls, CancellationToken ct = default)
    {
        var tasks = urls.Select(async url =>
        {
            try
            {
                return new ManifestSource(url, await Manifests.LoadAsync(ToUri(url), ct), null);
            }
            catch (ManifestException e)
            {
                return new ManifestSource(url, null, e.Message);
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

    /// <summary>お気に入り（マニフェストにないサーバー）は同期せず、手元の simutrans で接続する。</summary>
    public static (System.Diagnostics.Process? Process, string Command) LaunchFavorite(
        FavoriteServer favorite, LauncherSettings settings, bool printOnly = false)
    {
        if (string.IsNullOrWhiteSpace(settings.SimutransExe))
        {
            throw new SyncException("お気に入りのサーバーに接続するには、設定で手元の simutrans の実行ファイルを指定してください");
        }
        var args = LaunchCommandBuilder.Build(favorite.PaksetFolder, ServerAddress.Parse(favorite.Address));
        var command = LaunchCommandBuilder.ToDisplayString(settings.SimutransExe, args);
        return (printOnly ? null : SimutransRunner.Start(settings.SimutransExe, args), command);
    }

    /// <summary>URL でなければローカルのファイルパスとして扱う。</summary>
    public static Uri ToUri(string urlOrPath) =>
        Uri.TryCreate(urlOrPath, UriKind.Absolute, out var u) && (u.Scheme is "http" or "https" or "file")
            ? u
            : new Uri(Path.GetFullPath(urlOrPath));
}
