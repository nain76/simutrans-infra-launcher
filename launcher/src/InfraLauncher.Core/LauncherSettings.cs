namespace InfraLauncher.Core;

/// <summary>ユーザーごとの設定。&lt;データフォルダ&gt;/settings.json に保存する。</summary>
public sealed class LauncherSettings
{
    public List<string> ManifestUrls { get; set; } = new();

    /// <summary>マニフェストに今の OS 用の本体がないときに使う、手元の simutrans の実行ファイル。</summary>
    public string? SimutransExe { get; set; }

    /// <summary>マニフェストにない、手入力のサーバー。同期はせず、そのまま接続する。</summary>
    public List<FavoriteServer> Favorites { get; set; } = new();

    public static LauncherSettings Load(InstallLayout layout) => Json.Load(layout.SettingsPath, Json.Context.LauncherSettings);

    public void Save(InstallLayout layout) => Json.Save(layout.SettingsPath, this, Json.Context.LauncherSettings);
}

public sealed class FavoriteServer
{
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
    public string PaksetFolder { get; set; } = "";
}
