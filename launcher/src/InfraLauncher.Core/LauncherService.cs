using InfraLauncher.Core.Models;

namespace InfraLauncher.Core;

/// <summary>1つのサーバーリストを読んだ結果。取得に失敗した場合はErrorに理由が入る。</summary>
public sealed record ManifestSource(ServerListSource List, Manifest? Manifest, string? Error);

/// <summary>
/// 起動に必要な情報。ManagedEngineはランチャーが配布元から入れた本体か（そうなら初回に承認が要る）。
/// </summary>
public sealed record LaunchInfo(string ExePath, IReadOnlyList<string> Args, string Command, string ExeSha256,
    bool ManagedEngine, bool NeedsApproval, string? EngineLabel, string? SourceUrl);

/// <summary>CLIと画面の両方から使う、ひととおりの操作をまとめたもの。</summary>
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
                return new ManifestSource(list, await Manifests.LoadAsync(ToUri(list.Url), list.PublicKey, ct), null);
            }
            catch (Exception e) when (e is ManifestException or UriFormatException or ArgumentException)
            {
                return new ManifestSource(list, null, e.Message);
            }
        });
        return await Task.WhenAll(tasks);
    }

    /// <summary>同期する（本体とpaksetのインストール・更新）。起動はしない。</summary>
    public async Task<SyncSummary> SyncServerAsync(ServerEntry server, LauncherSettings settings,
        IProgress<SyncProgress>? progress = null, CancellationToken ct = default, InstallOptions? options = null) =>
        await Sync.SyncAsync(Sync.Plan(server, settings, options), progress, ct);

    /// <summary>
    /// 起動の準備。同期が済んでいることを確かめ、実行ファイルのSHA256を求め、ユーザーの承認が要るかを判断する。
    /// ランチャーが配布元から入れた本体は、同じSHA256のものを一度承認するまで起動しない。
    /// </summary>
    public LaunchInfo PrepareLaunch(ServerEntry server, LauncherSettings settings, InstallOptions? options = null)
    {
        var plan = Sync.Plan(server, settings, options);
        if (!plan.UpToDate)
        {
            throw new SyncException("まだ同期が済んでいません。先に「同期」を押してください");
        }
        var engine = plan.Items.FirstOrDefault(i => i.Kind == SyncItemKind.Engine);
        var args = LaunchCommandBuilder.Build(plan.PaksetFolder, ServerAddress.Parse(server.Address));
        var sha = HashExe(plan.ExePath);
        return new LaunchInfo(plan.ExePath, args, LaunchCommandBuilder.ToDisplayString(plan.ExePath, args), sha,
            ManagedEngine: engine is not null,
            NeedsApproval: engine is not null && !settings.IsApproved(sha),
            EngineLabel: engine?.Label, SourceUrl: engine?.Url);
    }

    /// <summary>手動プロファイルの起動の準備。本人が指定したsimutransなので承認は要らない。</summary>
    public static LaunchInfo PrepareManual(ManualProfile profile, LauncherSettings settings)
    {
        var exe = string.IsNullOrWhiteSpace(profile.SimutransExe) ? settings.SimutransExe : profile.SimutransExe;
        if (string.IsNullOrWhiteSpace(exe))
        {
            throw new SyncException($"プロファイル '{profile.Name}' にsimutransの実行ファイルが指定されていません");
        }
        var args = LaunchCommandBuilder.Build(profile.PaksetFolder, ServerAddress.Parse(profile.Address));
        return new LaunchInfo(Path.GetFullPath(exe), args, LaunchCommandBuilder.ToDisplayString(exe, args), HashExe(exe),
            ManagedEngine: false, NeedsApproval: false, EngineLabel: null, SourceUrl: null);
    }

    /// <summary>
    /// 起動する。承認が要る本体が未承認なら起動しない。準備のあとで実行ファイルが書き換えられていないかも確かめる。
    /// </summary>
    public static System.Diagnostics.Process Launch(LaunchInfo info, LauncherSettings settings)
    {
        if (info.ManagedEngine && !settings.IsApproved(info.ExeSha256))
        {
            throw new SyncException("このsimutrans本体はまだ実行を承認していません");
        }
        if (!string.Equals(HashExe(info.ExePath), info.ExeSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new SyncException("確認したあとでsimutrans本体が書き換えられました。もう一度「起動」を押してください");
        }
        // ランチャーが入れた本体なら、プレイヤー名を設定ファイルに書いてから起動する
        if (info.ManagedEngine)
        {
            NicknameConfig.Apply(Path.GetDirectoryName(info.ExePath)!, settings.Nickname);
        }
        return SimutransRunner.Start(info.ExePath, info.Args);
    }

    private static string HashExe(string exe)
    {
        if (!File.Exists(exe))
        {
            throw new FileNotFoundException($"simutransの実行ファイルが見つかりません: {exe}", exe);
        }
        using var stream = File.OpenRead(exe);
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream));
    }

    /// <summary>URLでなければローカルのファイルパスとして扱う。</summary>
    public static Uri ToUri(string urlOrPath) =>
        Uri.TryCreate(urlOrPath, UriKind.Absolute, out var u) && (u.Scheme is "http" or "https" or "file")
            ? u
            : new Uri(Path.GetFullPath(urlOrPath));
}
