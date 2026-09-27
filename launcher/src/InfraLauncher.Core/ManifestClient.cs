using System.Text.Json;
using System.Text.RegularExpressions;
using InfraLauncher.Core.Models;

namespace InfraLauncher.Core;

public sealed class ManifestException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>マニフェストを取得して検証する。http(s):// と file:// に対応。</summary>
public sealed partial class ManifestClient(HttpClient http)
{
    public const int SupportedSchemaVersion = 1;

    public async Task<Manifest> LoadAsync(Uri uri, CancellationToken ct = default)
    {
        string text;
        try
        {
            text = uri.IsFile
                ? await File.ReadAllTextAsync(uri.LocalPath, ct)
                : await http.GetStringAsync(uri, ct);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new ManifestException($"サーバーリストを取得できませんでした。アドレスが正しいか確認してください: {uri} ({e.Message})", e);
        }
        return Parse(text, uri);
    }

    /// <summary>
    /// 解析して検証する。<paramref name="baseUri"/> を渡すと、リスト内の相対アドレス（"pak128.japan/index.json" など）を
    /// サーバーリストの場所から見た絶対アドレスに置き換える。
    /// </summary>
    public static Manifest Parse(string json, Uri? baseUri = null)
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
        Validate(manifest, baseUri);
        return manifest;
    }

    private static void Validate(Manifest m, Uri? baseUri)
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
                var secure = true;
                foreach (var (key, b) in e.Builds ?? new())
                {
                    b.Url = ResolveUrl(b.Url, baseUri, $"サーバー '{where}' の engine.builds.{key}.url");
                    RequireSha(b.Sha256, $"サーバー '{where}' の engine.builds.{key}.sha256");
                    Require(!string.IsNullOrWhiteSpace(b.Exe) && !Path.IsPathRooted(b.Exe), $"サーバー '{where}' の engine.builds.{key}.exe が不正です");
                    secure &= new Uri(b.Url).Scheme is "https" || new Uri(b.Url).IsFile && baseUri is null or { IsFile: true };
                }
                s.EngineDownloadAllowed = secure && (baseUri is null || baseUri.Scheme == "https" || baseUri.IsFile);
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

    /// <summary>ファイル一覧（index.json）を解析して検証する。</summary>
    public static PaksetIndex ParseIndex(string json)
    {
        PaksetIndex? index;
        try
        {
            index = JsonSerializer.Deserialize(json, Json.Context.PaksetIndex);
        }
        catch (JsonException e)
        {
            throw new ManifestException($"pakset のファイル一覧の形式が正しくありません: {e.Message}", e);
        }
        Require(index is not null, "pakset のファイル一覧が空です");
        Require(index!.SchemaVersion == SupportedSchemaVersion, $"対応していないファイル一覧の schema_version です: {index.SchemaVersion}");

        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in index.Files)
        {
            Require(IsSafeRelativePath(f.Path), $"pakset のファイル一覧に使えないパスがあります: {f.Path}");
            Require(!BlockedExtensions.Contains(Path.GetExtension(f.Path)), $"pakset のファイル一覧に実行ファイルなどが含まれています: {f.Path}");
            Require(files.Add(f.Path), $"pakset のファイル一覧でパスが重複しています: {f.Path}");
            Require(f.Size >= 0, $"pakset のファイル一覧のサイズが不正です: {f.Path}");
            RequireSha(f.Sha256, $"pakset のファイル一覧の {f.Path} の sha256");
            var parts = f.Path.Split('/');
            for (var i = 1; i < parts.Length; i++)
            {
                dirs.Add(string.Join('/', parts[..i]));
            }
        }
        Require(!files.Overlaps(dirs), "pakset のファイル一覧に、ファイルとフォルダで同じ名前のものがあります");
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
