using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace InfraLauncher.Core.Admin;

/// <summary>サーバーリストにあるサーバー1台分（サーバー管理ツールの表示用）。</summary>
public sealed record AdminServer(string Id, string Name, string Address, string? Status, string? Message, string? PaksetFolder, string? EngineRevision)
{
    public bool Maintenance => Status == "maintenance";
}

/// <summary>署名の状態。</summary>
public enum SignatureState
{
    /// <summary>サーバーリストがない。</summary>
    NoList,
    /// <summary>署名がない。</summary>
    Missing,
    /// <summary>署名が中身と合わない。</summary>
    Invalid,
    /// <summary>この PC の署名の鍵とは別の鍵で署名されている。</summary>
    OtherKey,
    /// <summary>この PC の署名の鍵で正しく署名されている。</summary>
    Ok,
}

/// <summary>
/// server-setup フォルダの中身を読む（サーバー管理ツール用）。書き換えは PowerShell のスクリプトに任せ、ここでは読むだけにする。
/// 読むもの: publish-settings.json（サーバーリストの場所、公開アドレス）、サーバーリスト、その署名、署名の鍵の公開鍵。
/// </summary>
public sealed class ServerSetup
{
    private ServerSetup(string folder) => Folder = folder;

    public string Folder { get; }
    public string? ManifestPath { get; private set; }
    public string? ShareUrl { get; private set; }
    public IReadOnlyList<AdminServer> Servers { get; private set; } = [];
    /// <summary>この PC の署名の鍵の確認コード（鍵がなければ null）。</summary>
    public string? KeyCode { get; private set; }
    public string? KeyPublicKey { get; private set; }
    public SignatureState Signature { get; private set; }
    /// <summary>読み込めなかった理由（読めたら null）。</summary>
    public string? Error { get; private set; }

    /// <summary>サーバーリストのファイル名が manifest.json のような推測されやすい名前か。</summary>
    public bool HasGuessableName => ManifestPath is not null
        && Path.GetFileName(ManifestPath).Equals("manifest.json", StringComparison.OrdinalIgnoreCase);

    /// <summary>server-setup フォルダか（Common.ps1 があるか）。</summary>
    public static bool IsSetupFolder(string folder) => File.Exists(Path.Combine(folder, "Common.ps1"));

    /// <summary>
    /// server-setup フォルダを探す。ツールの exe と同じフォルダか、その中の server-setup フォルダ。
    /// </summary>
    public static string? FindFolder(string exeDir)
    {
        foreach (var candidate in new[] { exeDir, Path.Combine(exeDir, "server-setup"), Path.GetDirectoryName(exeDir) ?? exeDir })
        {
            if (IsSetupFolder(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }
        return null;
    }

    /// <summary>
    /// 読み込む。<paramref name="keyFile"/> は署名の鍵のファイル（既定は %LOCALAPPDATA%\InfraLauncherServer\signing-key.dat）。
    /// </summary>
    public static ServerSetup Load(string folder, string? keyFile = null)
    {
        var setup = new ServerSetup(Path.GetFullPath(folder));
        try
        {
            setup.LoadKey(keyFile ?? DefaultKeyFile);
            setup.LoadSettings();
            setup.LoadManifest();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException or FormatException)
        {
            setup.Error = e.Message;
        }
        return setup;
    }

    public static string DefaultKeyFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InfraLauncherServer", "signing-key.dat");

    private void LoadKey(string keyFile)
    {
        if (!File.Exists(keyFile))
        {
            return;
        }
        var key = JsonNode.Parse(ReadText(keyFile));
        if (key?["public_key"]?.GetValue<string>() is { } publicKey)
        {
            KeyPublicKey = publicKey;
            KeyCode = ManifestSignature.CodeFor(publicKey);
        }
    }

    private void LoadSettings()
    {
        var path = Path.Combine(Folder, "publish-settings.json");
        if (!File.Exists(path))
        {
            return;
        }
        var settings = JsonNode.Parse(ReadText(path));
        ManifestPath = settings?["manifest"]?.GetValue<string>();
        ShareUrl = settings?["share_url"]?.GetValue<string>();
    }

    private void LoadManifest()
    {
        if (ManifestPath is null || !File.Exists(ManifestPath))
        {
            Signature = SignatureState.NoList;
            return;
        }
        var bytes = File.ReadAllBytes(ManifestPath);
        var manifest = JsonNode.Parse(Decode(bytes));
        Servers = (manifest?["servers"]?.AsArray() ?? [])
            .Where(s => s is not null)
            .Select(s => new AdminServer(
                s!["id"]?.GetValue<string>() ?? "",
                s["name"]?.GetValue<string>() ?? "",
                s["address"]?.GetValue<string>() ?? "",
                s["status"]?.GetValue<string>(),
                s["message"]?.GetValue<string>(),
                s["pakset"]?["folder"]?.GetValue<string>(),
                s["engine"]?["revision"]?.GetValue<string>()))
            .ToList();

        var sigPath = ManifestSignature.SignatureUriFor(new Uri(ManifestPath)).LocalPath;
        if (!File.Exists(sigPath))
        {
            Signature = SignatureState.Missing;
            return;
        }
        try
        {
            var info = ManifestSignature.Verify(bytes, ReadText(sigPath));
            Signature = KeyPublicKey is not null && info.PublicKey == KeyPublicKey ? SignatureState.Ok : SignatureState.OtherKey;
        }
        catch (ManifestSignatureException)
        {
            Signature = SignatureState.Invalid;
        }
    }

    private static string ReadText(string path) => Decode(File.ReadAllBytes(path));

    private static string Decode(byte[] bytes) =>
        bytes is [0xEF, 0xBB, 0xBF, ..] ? Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3) : Encoding.UTF8.GetString(bytes);
}

/// <summary>
/// PowerShell のスクリプトを呼ぶためのコマンド。引数は PowerShell の文字列としてエスケープし、-EncodedCommand で渡す
/// （お知らせに引用符や記号が入っていても、そのまま渡せるように）。出力は UTF-8 にする。
/// </summary>
public static class PowerShellCommand
{
    /// <summary>引数の値。文字列か真偽値かスイッチ（値なし）。</summary>
    public static string Build(string scriptPath, IEnumerable<KeyValuePair<string, object?>> arguments)
    {
        var sb = new StringBuilder();
        // エラーは「エラー: 理由」の1行にして、終了コード 1 で終わる（呼んだ側が成功か失敗かを見分けられるように）
        sb.Append("[Console]::OutputEncoding = [System.Text.Encoding]::UTF8; $ErrorActionPreference = 'Stop'; try { & ");
        sb.Append(Quote(scriptPath));
        foreach (var (name, value) in arguments)
        {
            sb.Append(" -").Append(name);
            switch (value)
            {
                case null:
                    break;
                case bool b:
                    sb.Append(':').Append(b ? "$true" : "$false");
                    break;
                default:
                    sb.Append(' ').Append(Quote(value.ToString() ?? ""));
                    break;
            }
        }
        sb.Append(" } catch { Write-Output ('エラー: ' + $_.Exception.Message); exit 1 }");
        return sb.ToString();
    }

    /// <summary>-EncodedCommand に渡す形（UTF-16LE を base64 にしたもの）。</summary>
    public static string Encode(string command) => Convert.ToBase64String(Encoding.Unicode.GetBytes(command));

    /// <summary>PowerShell の単一引用符の文字列にする（中の ' は '' にする）。</summary>
    public static string Quote(string value)
    {
        // PowerShell は ' のほかに ‘ ’ ‚ ‛ も単一引用符として扱うので、どれも2つ重ねる
        var sb = new StringBuilder("'");
        foreach (var c in value)
        {
            sb.Append(c);
            if (c is '\'' or '\u2018' or '\u2019' or '\u201A' or '\u201B')
            {
                sb.Append(c);
            }
        }
        return sb.Append('\'').ToString();
    }
}
