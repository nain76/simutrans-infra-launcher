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
        return Parse(text);
    }

    public static Manifest Parse(string json)
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
        Validate(manifest);
        return manifest;
    }

    private static void Validate(Manifest m)
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
            RequireDownload(p.Url, p.Sha256, $"サーバー '{where}' の pakset");

            if (s.Engine is { } e)
            {
                Require(SafeName().IsMatch(e.Revision) && e.Revision.Trim('.').Length > 0, $"サーバー '{where}' の engine.revision が不正です: {e.Revision}");
                foreach (var (key, b) in e.Builds ?? new())
                {
                    RequireDownload(b.Url, b.Sha256, $"サーバー '{where}' の engine.builds.{key}");
                    Require(!string.IsNullOrWhiteSpace(b.Exe) && !Path.IsPathRooted(b.Exe), $"サーバー '{where}' の engine.builds.{key}.exe が不正です");
                }
            }
        }
    }

    private static void RequireDownload(string url, string sha256, string what)
    {
        Require(Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme is "http" or "https" or "file"), $"{what} の url が不正です: {url}");
        Require(Sha256Hex().IsMatch(sha256 ?? ""), $"{what} の sha256 が不正です（16進数64文字）");
    }

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
