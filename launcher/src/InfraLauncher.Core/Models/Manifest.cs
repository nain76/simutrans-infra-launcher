namespace InfraLauncher.Core.Models;

/// <summary>サーバー一覧マニフェスト。形式は manifest/manifest.schema.json を参照。</summary>
public sealed class Manifest
{
    public int SchemaVersion { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public List<ServerEntry> Servers { get; set; } = new();

    /// <summary>署名があれば、その鍵と確認コード。<see cref="ManifestClient"/> が設定する。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public SignatureInfo? Signature { get; set; }

    /// <summary>ユーザーが確認コードを登録した鍵で署名されていたか。<see cref="ManifestClient"/> が設定する。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Trusted { get; set; }
}

/// <summary>サーバーリストの署名ファイル（manifest.sig.json）。</summary>
public sealed class SignatureFile
{
    public string? Format { get; set; }
    /// <summary>公開鍵（SubjectPublicKeyInfo を base64 にしたもの）。</summary>
    public string? PublicKey { get; set; }
    /// <summary>manifest.json のバイト列に対する ECDSA P-256 / SHA-256 の署名（r と s を並べた 64 バイトを base64 にしたもの）。</summary>
    public string? Signature { get; set; }
    public string? SignedAt { get; set; }
}

public sealed class ServerEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
    public string? Status { get; set; }
    public int? Players { get; set; }
    public string? Message { get; set; }
    public EngineInfo? Engine { get; set; }
    public PaksetInfo Pakset { get; set; } = new();

    /// <summary>
    /// このサーバーの本体（実行ファイル）を自動で入れてよいか。
    /// 本体をすり替えられないよう、ユーザーが確認コードを登録した鍵でサーバーリストが署名されているときだけ許す。
    /// <see cref="ManifestClient"/> が設定する。
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool EngineDownloadAllowed { get; set; }
}

public sealed class EngineInfo
{
    public string Revision { get; set; } = "";
    /// <summary>キーは <see cref="PlatformInfo.CurrentKey"/> と同じ形式（windows-x64 など）。</summary>
    public Dictionary<string, EngineBuild>? Builds { get; set; }
}

/// <summary>
/// OS ごとの本体。配り方は2通り。
/// zip 方式: url と sha256
/// ファイル一覧方式: index_url と index_sha256（部品ごとに選んで落とせる。変わったファイルだけを落とす）
/// </summary>
public sealed class EngineBuild
{
    public string? Url { get; set; }
    public string? Sha256 { get; set; }
    public string? IndexUrl { get; set; }
    public string? IndexSha256 { get; set; }
    /// <summary>展開先から見た実行ファイルの相対パス。</summary>
    public string Exe { get; set; } = "";

    [System.Text.Json.Serialization.JsonIgnore]
    public bool UsesFileIndex => IndexUrl is not null;
}

/// <summary>
/// pakset の配り方は2通り。
/// zip 方式: url と sha256（zip 1つを丸ごと入れ替える）
/// ファイル一覧方式: index_url と index_sha256（変わったファイルだけを落とす）
/// </summary>
public sealed class PaksetInfo
{
    public string Name { get; set; } = "";
    public string? Version { get; set; }
    /// <summary>-objects に渡すフォルダ名。</summary>
    public string Folder { get; set; } = "";
    public string? Url { get; set; }
    public string? Sha256 { get; set; }
    public string? IndexUrl { get; set; }
    public string? IndexSha256 { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool UsesFileIndex => IndexUrl is not null;

    /// <summary>画面に出す名前。フォルダ名が名前と違うときは添える（同じ名前の pakset を見分けるため）。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayName
    {
        get
        {
            var name = Folder.Length > 0 && !string.Equals(Folder, Name, StringComparison.OrdinalIgnoreCase) ? $"{Name}（{Folder}）" : Name;
            return string.IsNullOrEmpty(Version) ? name : $"{name} {Version}";
        }
    }
}

/// <summary>ファイル一覧方式の一覧ファイル（index.json）。各ファイルは一覧ファイルと同じ場所からの相対パスで置く。</summary>
public sealed class PaksetIndex
{
    public int SchemaVersion { get; set; }
    /// <summary>部品の一覧（本体のファイル一覧だけ）。なければ全ファイルを落とす。</summary>
    public List<IndexComponent>? Components { get; set; }
    public List<PaksetFile> Files { get; set; } = new();
}

/// <summary>本体の部品（音楽、テーマなど）。必須の部品は外せない。推奨の部品は「推奨」を選んだときに落とす。</summary>
public sealed class IndexComponent
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Required { get; set; }
    public bool Recommended { get; set; }
}

public sealed class PaksetFile
{
    /// <summary>pakset フォルダから見た相対パス。区切りは "/"。</summary>
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
    /// <summary>属する部品の id（本体のファイル一覧だけ）。</summary>
    public string? Component { get; set; }
}
