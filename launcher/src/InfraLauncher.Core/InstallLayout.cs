namespace InfraLauncher.Core;

/// <summary>ランチャーが管理するフォルダの配置。</summary>
public sealed class InstallLayout(string root)
{
    public string Root { get; } = Path.GetFullPath(root);

    /// <summary>Windows: %LOCALAPPDATA%\InfraLauncher、Linux: ~/.local/share/InfraLauncher、macOS: ~/Library/Application Support/InfraLauncher</summary>
    public static InstallLayout Default() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
        "InfraLauncher"));

    public string SettingsPath => Path.Combine(Root, "settings.json");

    /// <summary>展開したファイルの記録（どの sha256 の zip をどこに展開したか）。</summary>
    public string InstalledStatePath => Path.Combine(Root, "installed.json");

    public string DownloadDir => Path.Combine(Root, "downloads");

    /// <summary>本体はリビジョンごとに別フォルダに置く。pakset はこの中（実行ファイルの横）に展開される。</summary>
    public string EngineDir(string revision) => Path.Combine(Root, "simutrans", revision);
}
