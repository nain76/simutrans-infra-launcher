namespace InfraLauncher.Core;

/// <summary>ランチャーが管理するフォルダの配置。</summary>
public sealed class InstallLayout(string root, string? defaultInstallRoot = null)
{
    public string Root { get; } = Path.GetFullPath(root);

    /// <summary>同期の記録。</summary>
    public string SyncLogPath => Path.Combine(Root, "logs", "sync.log");

    /// <summary>Windows: %LOCALAPPDATA%\InfraLauncher、Linux: ~/.local/share/InfraLauncher、macOS: ~/Library/Application Support/InfraLauncher</summary>
    public static InstallLayout Default() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
        "InfraLauncher"), VisibleInstallRoot());

    /// <summary>
    /// 本体とpaksetの既定のダウンロード先。隠しフォルダ（AppData）の中だと、セーブデータや設定を探しにくいので、見える場所にする。
    /// Windows: C:\Games\simutrans（ドキュメントやデスクトップはOneDriveに同期されることが多く、ダウンロードが止まる原因になるため避ける）、
    /// そのほか: ~/Simutrans
    /// </summary>
    public static string VisibleInstallRoot() => OperatingSystem.IsWindows()
        ? Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\", "Games", "simutrans")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Simutrans");

    public string SettingsPath => Path.Combine(Root, "settings.json");

    /// <summary>展開したファイルの記録（どのsha256のzipをどこに展開したか）。</summary>
    public string InstalledStatePath => Path.Combine(Root, "installed.json");

    public string DownloadDir => Path.Combine(Root, "downloads");

    /// <summary>本体とpaksetの既定のダウンロード先。設定で変えられる。以前の版は Root の下の simutrans だった。</summary>
    public string DefaultInstallRoot { get; } = Path.GetFullPath(defaultInstallRoot ?? Path.Combine(root, "simutrans"));

    /// <summary>既定のダウンロード先での本体のフォルダ。本体はサーバーごとに1つのフォルダに置き、paksetはこの中（実行ファイルの横）に入る。</summary>
    public string EngineDir(string folderName) => Path.Combine(DefaultInstallRoot, folderName);
}
