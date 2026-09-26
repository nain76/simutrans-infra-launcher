namespace InfraLauncher.Core.Models;

/// <summary>サーバー一覧マニフェスト。形式は manifest/manifest.schema.json を参照。</summary>
public sealed class Manifest
{
    public int SchemaVersion { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public List<ServerEntry> Servers { get; set; } = new();
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
}

public sealed class EngineInfo
{
    public string Revision { get; set; } = "";
    /// <summary>キーは <see cref="PlatformInfo.CurrentKey"/> と同じ形式（windows-x64 など）。</summary>
    public Dictionary<string, EngineBuild>? Builds { get; set; }
}

public sealed class EngineBuild
{
    public string Url { get; set; } = "";
    public string Sha256 { get; set; } = "";
    /// <summary>展開先から見た実行ファイルの相対パス。</summary>
    public string Exe { get; set; } = "";
}

public sealed class PaksetInfo
{
    public string Name { get; set; } = "";
    public string? Version { get; set; }
    /// <summary>-objects に渡すフォルダ名。</summary>
    public string Folder { get; set; } = "";
    public string Url { get; set; } = "";
    public string Sha256 { get; set; } = "";
}
