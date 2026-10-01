namespace InfraLauncher.Core;

/// <summary>ユーザーごとの設定。&lt;データフォルダ&gt;/settings.jsonに保存する。</summary>
public sealed class LauncherSettings
{
    /// <summary>サーバー管理者から共有されたサーバーリスト（中身はマニフェスト）。</summary>
    public List<ServerListSource> ServerLists { get; set; } = new();

    /// <summary>手動で設定したサーバー。同期はせず、指定したsimutransでそのまま接続する。</summary>
    public List<ManualProfile> ManualProfiles { get; set; } = new();

    /// <summary>お気に入りの印を付けたサーバー。キーは<see cref="FavoriteKeys"/>で作る。</summary>
    public List<string> FavoriteKeys { get; set; } = new();

    /// <summary>サーバーリストに今のOS用の本体が含まれていないときに使う、手元のsimutransの実行ファイル。</summary>
    public string? SimutransExe { get; set; }

    /// <summary>ネットワークゲームで表示されるプレイヤー名。空ならsimutransの設定のまま。</summary>
    public string? Nickname { get; set; }

    /// <summary>本体とpaksetをダウンロードする既定のフォルダ。空なら &lt;データフォルダ&gt;/simutrans。</summary>
    public string? InstallRoot { get; set; }

    /// <summary>サーバーごとのインストール設定（ダウンロード先と部品の選び方）。キーは<see cref="FavoriteKeys.ForListed"/>。</summary>
    public Dictionary<string, InstallOptions> ServerInstall { get; set; } = new();

    /// <summary>
    /// ユーザーが実行を承認した本体（ランチャーが配布元から入れたもの）のSHA256。
    /// 本体が更新されたり書き換えられたりしてSHA256が変わると、もう一度確認する。
    /// </summary>
    public List<string> ApprovedExecutables { get; set; } = new();

    /// <summary>古い形式の設定（manifest_urls）。読み込み時に<see cref="ServerLists"/>へ移す。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? ManifestUrls { get; set; }

    public static LauncherSettings Load(InstallLayout layout)
    {
        var s = Json.Load(layout.SettingsPath, Json.Context.LauncherSettings);
        if (s.ManifestUrls is { Count: > 0 } old)
        {
            s.ServerLists.AddRange(old.Select((url, i) => new ServerListSource { Name = $"サーバーリスト{i + 1}", Url = url }));
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

/// <summary>
/// サーバーごとのインストール設定。
/// InstallRoot: ダウンロード先（空なら既定のフォルダ）。本体はこの下の &lt;リビジョン&gt; フォルダに入り、paksetはその中に入る
/// Components: 落とす本体の部品。nullなら「推奨」（サーバーが推奨する部品。サーバー側で推奨が変われば追従する）、
///             リストなら「カスタム」（必須の部品は書かなくても落とす）
/// </summary>
public sealed class InstallOptions
{
    public string? InstallRoot { get; set; }
    public List<string>? Components { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsCustom => Components is not null;
}

/// <summary>
/// 共有されたサーバーリスト。表示名と配信アドレス（URLかファイルのパス）。
/// PublicKeyは、ユーザーが管理者から聞いた確認コードと一致した署名の鍵。以後はこの鍵の署名がないと読み込まない。
/// </summary>
public sealed class ServerListSource
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string? PublicKey { get; set; }

    /// <summary>登録した鍵の確認コード。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? Code => PublicKey is null ? null : ManifestSignature.CodeFor(PublicKey);
}

/// <summary>手動で設定したサーバー（プロファイル）。</summary>
public sealed class ManualProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
    public string PaksetFolder { get; set; } = "";
    /// <summary>空なら<see cref="LauncherSettings.SimutransExe"/>を使う。</summary>
    public string? SimutransExe { get; set; }
}

/// <summary>お気に入りの印を保存するときのキー。</summary>
public static class FavoriteKeys
{
    public static string ForListed(ServerListSource list, string serverId) => $"list:{list.Url}#{serverId}";

    public static string ForManual(ManualProfile profile) => $"manual:{profile.Id}";
}
