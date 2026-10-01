using System.Text.Json;
using System.Text.RegularExpressions;
using InfraLauncher.Core.Models;

namespace InfraLauncher.Core;

public class ManifestException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>マニフェスト（サーバーリスト）を取得して検証する。http(s):// と file:// に対応。</summary>
public sealed partial class ManifestClient(HttpClient http)
{
    public const int SupportedSchemaVersion = 1;

    /// <summary>公開し直している最中に取得して署名が合わなかったとき、取り直すまで待つ時間。</summary>
    internal static TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// サーバーリストと、隣にある署名（manifest.sig.json）を取得して検証する。
    /// <paramref name="pinnedKey"/>（ユーザーが確認コードを登録した鍵）を渡すと、その鍵の正しい署名がなければ例外にする。
    /// 渡さなければ署名は任意（あれば中身と合うかだけ確かめ、確認コードを <see cref="Manifest.Signature"/> に入れる）。
    /// 本体の自動インストールは、登録した鍵で確かめられたリストだけに許す。
    /// </summary>
    public async Task<Manifest> LoadAsync(Uri uri, string? pinnedKey = null, CancellationToken ct = default)
    {
        var (bytes, signature) = await FetchAsync(uri, ct);
        SignatureInfo? info;
        try
        {
            info = Check(bytes, signature, pinnedKey);
        }
        catch (ManifestSignatureException e) when (e.Problem == SignatureProblem.Invalid && !uri.IsFile)
        {
            // 管理者がちょうど公開し直している最中だと、新しいリストと古い署名を取ってしまうことがある。少し待って1回だけ取り直す
            await Task.Delay(RetryDelay, ct);
            (bytes, signature) = await FetchAsync(uri, ct);
            info = Check(bytes, signature, pinnedKey);
        }
        var manifest = Parse(Decode(bytes), uri, trusted: pinnedKey is not null);
        manifest.Signature = info;
        manifest.Trusted = pinnedKey is not null;
        return manifest;
    }

    private async Task<(byte[] Manifest, string? Signature)> FetchAsync(Uri uri, CancellationToken ct)
    {
        var sigUri = ManifestSignature.SignatureUriFor(uri);
        try
        {
            if (uri.IsFile)
            {
                var bytes = await File.ReadAllBytesAsync(uri.LocalPath, ct);
                var sig = File.Exists(sigUri.LocalPath) ? await File.ReadAllTextAsync(sigUri.LocalPath, ct) : null;
                return (bytes, sig);
            }
            var manifest = await http.GetByteArrayAsync(uri, ct);
            using var response = await http.GetAsync(sigUri, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return (manifest, null);
            }
            response.EnsureSuccessStatusCode();
            return (manifest, await response.Content.ReadAsStringAsync(ct));
        }
        catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new ManifestException($"サーバーリストを取得できませんでした。アドレスが正しいか確認してください: {uri} ({e.Message})", e);
        }
    }

    private static SignatureInfo? Check(byte[] manifest, string? signature, string? pinnedKey)
    {
        var info = signature is null ? null : ManifestSignature.Verify(manifest, signature);
        if (pinnedKey is null)
        {
            return info;
        }
        if (info is null)
        {
            throw new ManifestSignatureException(SignatureProblem.Missing,
                "確認コードを登録したサーバーリストなのに、署名が見つかりません。配信しているファイルが書き換えられたおそれがあるため、読み込みを中止しました。サーバー管理者に連絡してください");
        }
        if (!Convert.FromBase64String(info.PublicKey).AsSpan().SequenceEqual(Convert.FromBase64String(pinnedKey)))
        {
            throw new ManifestSignatureException(SignatureProblem.KeyChanged,
                // 新しい確認コードは画面に出さない（出すと、ユーザーがそれを写して入力できてしまう）
                "サーバーリストの確認コードが変わりました。" +
                "管理者が鍵を作り直したのなら、新しい確認コードを管理者に聞いて「編集」で入力し直してください。" +
                "心当たりがなければ、配信しているファイルが書き換えられたおそれがあります", info.Code);
        }
        return info;
    }

    /// <summary>UTF-8 として読む（先頭に BOM があれば除く）。</summary>
    private static string Decode(byte[] bytes) =>
        bytes is [0xEF, 0xBB, 0xBF, ..] ? System.Text.Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3) : System.Text.Encoding.UTF8.GetString(bytes);

    /// <summary>
    /// 解析して検証する。<paramref name="baseUri"/> を渡すと、リスト内の相対アドレス（"pak128.japan/index.json" など）を
    /// サーバーリストの場所から見た絶対アドレスに置き換える。
    /// <paramref name="trusted"/> は署名を確かめたリストか（本体の自動インストールを許すか）。
    /// </summary>
    public static Manifest Parse(string json, Uri? baseUri = null, bool trusted = false)
    {
        Manifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize(json, Json.Context.Manifest);
        }
        catch (JsonException e)
        {
            throw new ManifestException($"サーバーリストの形式が正しくありません: {e.Message}", e);
        }
        if (manifest is null)
        {
            throw new ManifestException("サーバーリストが空です");
        }
        Validate(manifest, baseUri, trusted);
        return manifest;
    }

    private static void Validate(Manifest m, Uri? baseUri, bool trusted)
    {
        if (m.SchemaVersion != SupportedSchemaVersion)
        {
            throw new ManifestException($"対応していない schema_version です: {m.SchemaVersion}（対応: {SupportedSchemaVersion}）。ランチャーを更新してください");
        }
        var ids = new HashSet<string>();
        foreach (var s in m.Servers)
        {
            var where = string.IsNullOrEmpty(s.Name) ? s.Id : s.Name;
            Require(SafeName().IsMatch(s.Id), $"サーバー '{where}' の id が不正です");
            Require(ids.Add(s.Id), $"サーバーの id が重複しています: {s.Id}");
            Require(!string.IsNullOrWhiteSpace(s.Name), $"サーバー '{s.Id}' の name がありません");
            Require(ServerAddress.TryParse(s.Address, out _), $"サーバー '{where}' の address が不正です: {s.Address}");

            var p = s.Pakset;
            Require(p is not null, $"サーバー '{where}' に pakset がありません");
            // folder と revision はそのままパスになるので、.. や区切り文字を含む値を拒否する
            Require(SafeName().IsMatch(p!.Folder) && p.Folder.Trim('.').Length > 0, $"サーバー '{where}' の pakset.folder が不正です: {p.Folder}");
            var hasZip = p.Url is not null || p.Sha256 is not null;
            var hasIndex = p.IndexUrl is not null || p.IndexSha256 is not null;
            Require(hasZip != hasIndex,
                $"サーバー '{where}' の pakset には、url と sha256（zip 方式）か、index_url と index_sha256（ファイル一覧方式）のどちらか一方を書いてください");
            if (hasIndex)
            {
                p.IndexUrl = ResolveUrl(p.IndexUrl, baseUri, $"サーバー '{where}' の pakset.index_url");
                RequireSha(p.IndexSha256, $"サーバー '{where}' の pakset.index_sha256");
            }
            else
            {
                p.Url = ResolveUrl(p.Url, baseUri, $"サーバー '{where}' の pakset.url");
                RequireSha(p.Sha256, $"サーバー '{where}' の pakset.sha256");
            }

            s.EngineDownloadAllowed = false;
            if (s.Engine is { } e)
            {
                Require(SafeName().IsMatch(e.Revision) && e.Revision.Trim('.').Length > 0, $"サーバー '{where}' の engine.revision が不正です: {e.Revision}");
                foreach (var (key, b) in e.Builds ?? new())
                {
                    var what = $"サーバー '{where}' の engine.builds.{key}";
                    var engineZip = b.Url is not null || b.Sha256 is not null;
                    var engineIndex = b.IndexUrl is not null || b.IndexSha256 is not null;
                    Require(engineZip != engineIndex, $"{what} には、url と sha256（zip 方式）か、index_url と index_sha256（ファイル一覧方式）のどちらか一方を書いてください");
                    if (engineIndex)
                    {
                        b.IndexUrl = ResolveUrl(b.IndexUrl, baseUri, $"{what}.index_url");
                        RequireSha(b.IndexSha256, $"{what}.index_sha256");
                    }
                    else
                    {
                        b.Url = ResolveUrl(b.Url, baseUri, $"{what}.url");
                        RequireSha(b.Sha256, $"{what}.sha256");
                    }
                    Require(IsSafeRelativePath(b.Exe), $"{what}.exe が不正です");
                }
                // 本体のファイルは SHA256 でサーバーリストに結び付いているので、リストの署名を確かめていれば取得経路は問わない
                s.EngineDownloadAllowed = trusted;
            }
        }
    }

    /// <summary>
    /// アドレスを絶対アドレスにする。相対アドレスは baseUri から見た位置になる。
    /// Web 上のリストが手元のファイル（file://）を指すことは許さない。
    /// </summary>
    internal static string ResolveUrl(string? url, Uri? baseUri, string what)
    {
        Require(!string.IsNullOrWhiteSpace(url), $"{what} がありません");
        Uri? resolved = null;
        if (Uri.TryCreate(url, UriKind.Absolute, out var abs) && abs.Scheme is "http" or "https" or "file")
        {
            resolved = abs;
        }
        else if (baseUri is not null && !url!.Contains(':') && Uri.TryCreate(baseUri, url, out var rel))
        {
            resolved = rel;
        }
        Require(resolved is not null, $"{what} が不正です: {url}");
        Require(!(resolved!.IsFile && baseUri is { IsFile: false }), $"{what} が手元のファイルを指しています: {url}");
        return resolved.AbsoluteUri;
    }

    /// <summary>
    /// ファイル一覧（index.json）を解析して検証する。
    /// <paramref name="engineExe"/> を渡すと本体のファイル一覧として扱い、その実行ファイルと、直下の .dll だけは許す。
    /// </summary>
    public static PaksetIndex ParseIndex(string json, string? engineExe = null)
    {
        var kind = engineExe is null ? "pakset" : "simutrans 本体";
        PaksetIndex? index;
        try
        {
            index = JsonSerializer.Deserialize(json, Json.Context.PaksetIndex);
        }
        catch (JsonException e)
        {
            throw new ManifestException($"{kind}のファイル一覧の形式が正しくありません: {e.Message}", e);
        }
        Require(index is not null, $"{kind}のファイル一覧が空です");
        Require(index!.SchemaVersion == SupportedSchemaVersion, $"対応していないファイル一覧の schema_version です: {index.SchemaVersion}");

        var componentIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in index.Components ?? new())
        {
            Require(SafeName().IsMatch(c.Id) && componentIds.Add(c.Id), $"{kind}のファイル一覧の部品の id が不正か重複しています: {c.Id}");
        }

        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in index.Files)
        {
            Require(IsSafeRelativePath(f.Path), $"{kind}のファイル一覧に使えないパスがあります: {f.Path}");
            var ext = Path.GetExtension(f.Path);
            var allowedExecutable = engineExe is not null
                && (string.Equals(f.Path, engineExe, StringComparison.OrdinalIgnoreCase)
                    || ext.Equals(".dll", StringComparison.OrdinalIgnoreCase) && !f.Path.Contains('/'));
            Require(allowedExecutable || !BlockedExtensions.Contains(ext), $"{kind}のファイル一覧に実行ファイルなどが含まれています: {f.Path}");
            Require(files.Add(f.Path), $"{kind}のファイル一覧でパスが重複しています: {f.Path}");
            Require(f.Size >= 0, $"{kind}のファイル一覧のサイズが不正です: {f.Path}");
            RequireSha(f.Sha256, $"{kind}のファイル一覧の {f.Path} の sha256");
            Require(f.Component is null || componentIds.Contains(f.Component), $"{kind}のファイル一覧の {f.Path} の部品がありません: {f.Component}");
            var parts = f.Path.Split('/');
            for (var i = 1; i < parts.Length; i++)
            {
                dirs.Add(string.Join('/', parts[..i]));
            }
        }
        Require(!files.Overlaps(dirs), $"{kind}のファイル一覧に、ファイルとフォルダで同じ名前のものがあります");
        Require(engineExe is null || files.Contains(engineExe), $"{kind}のファイル一覧に実行ファイル {engineExe} がありません");
        return index;
    }

    /// <summary>
    /// pakset に入っていてはいけない、OS がそのまま実行できる種類のファイル。
    /// Squirrel スクリプト（.nut）は pakset の正式な中身なので止めない（simutrans の中で制限付きで動く）。
    /// </summary>
    public static readonly HashSet<string> BlockedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".com", ".scr", ".msi", ".msp", ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".vbe",
        ".js", ".jse", ".wsf", ".wsh", ".hta", ".lnk", ".url", ".reg", ".cpl", ".jar", ".sh", ".app", ".so", ".dylib",
    };

    private static readonly HashSet<string> WindowsReserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>"/" 区切りの相対パスで、どの OS でもフォルダの外を指さず、Windows で作れる名前か。</summary>
    internal static bool IsSafeRelativePath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 400 || path.StartsWith('/'))
        {
            return false;
        }
        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or ".." || segment.EndsWith('.') || segment.EndsWith(' ')
                || segment.Any(c => c < 32 || "\\:*?\"<>|".Contains(c))
                || WindowsReserved.Contains(segment.Split('.')[0]))
            {
                return false;
            }
        }
        return true;
    }

    private static void RequireSha(string? sha256, string what) =>
        Require(Sha256Hex().IsMatch(sha256 ?? ""), $"{what} が不正です（16進数64文字）");

    private static void Require(bool ok, string message)
    {
        if (!ok)
        {
            throw new ManifestException(message);
        }
    }

    [GeneratedRegex("^[A-Za-z0-9_.+-]+$")]
    private static partial Regex SafeName();

    [GeneratedRegex("^[0-9a-fA-F]{64}$")]
    private static partial Regex Sha256Hex();
}
