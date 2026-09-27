namespace InfraLauncher.Core;

/// <summary>ユーザーごとの設定。&lt;データフォルダ&gt;/settings.json に保存する。</summary>
public sealed class LauncherSettings
{
    /// <summary>サーバー管理者から共有されたサーバーリスト（中身はマニフェスト）。</summary>
    public List<ServerListSource> ServerLists { get; set; } = new();

    /// <summary>手動で設定したサーバー。同期はせず、指定した simutrans でそのまま接続する。</summary>
    public List<ManualProfile> ManualProfiles { get; set; } = new();

    /// <summary>お気に入りの印を付けたサーバー。キーは <see cref="FavoriteKeys"/> で作る。</summary>
    public List<string> FavoriteKeys { get; set; } = new();

    /// <summary>サーバーリストに今の OS 用の本体が含まれていないときに使う、手元の simutrans の実行ファイル。</summary>
    public string? SimutransExe { get; set; }

    /// <summary>
    /// ユーザーが実行を承認した本体（ランチャーが配布元から入れたもの）の SHA256。
    /// 本体が更新されたり書き換えられたりして SHA256 が変わると、もう一度確認する。
    /// </summary>
    public List<string> ApprovedExecutables { get; set; } = new();

    /// <summary>古い形式の設定（manifest_urls）。読み込み時に <see cref="ServerLists"/> へ移す。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? ManifestUrls { get; set; }

    public static LauncherSettings Load(InstallLayout layout)
    {
        var s = Json.Load(layout.SettingsPath, Json.Context.LauncherSettings);
        if (s.ManifestUrls is { Count: > 0 } old)
        {
            s.ServerLists.AddRange(old.Select((url, i) => new ServerListSource { Name = $"サーバーリスト {i + 1}", Url = url }));
        }
        s.ManifestUrls = null;
        return s;
    }

    public void Save(InstallLayout layout) => Json.Save(layout.SettingsPath, this, Json.Context.LauncherSettings);

    public bool IsApproved(string sha256) => ApprovedExecutables.Contains(sha256, StringComparer.OrdinalIgnoreCase);

    public void Approve(string sha256)
    {
        if (!IsApproved(sha256))
        {
            ApprovedExecutables.Add(sha256.ToLowerInvariant());
        }
    }

    public bool IsFavorite(string key) => FavoriteKeys.Contains(key);

    public void SetFavorite(string key, bool favorite)
    {
        FavoriteKeys.Remove(key);
        if (favorite)
        {
            FavoriteKeys.Add(key);
        }
    }
}

/// <summary>共有されたサーバーリスト。表示名と配信アドレス（URL かファイルのパス）。</summary>
public sealed class ServerListSource
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
}

/// <summary>手動で設定したサーバー（プロファイル）。</summary>
public sealed class ManualProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
    public string PaksetFolder { get; set; } = "";
    /// <summary>空なら <see cref="LauncherSettings.SimutransExe"/> を使う。</summary>
    public string? SimutransExe { get; set; }
}

/// <summary>お気に入りの印を保存するときのキー。</summary>
public static class FavoriteKeys
{
    public static string ForListed(ServerListSource list, string serverId) => $"list:{list.Url}#{serverId}";

    public static string ForManual(ManualProfile profile) => $"manual:{profile.Id}";
}
