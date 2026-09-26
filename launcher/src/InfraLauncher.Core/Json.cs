using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace InfraLauncher.Core;

internal static class Json
{
    /// <summary>マニフェストや保存ファイルは snake_case。日本語はエスケープせずに書く。</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    public static T Load<T>(string path) where T : new()
    {
        if (!File.Exists(path))
        {
            return new T();
        }
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<T>(stream, Options) ?? new T();
    }

    /// <summary>書き込み途中で落ちても壊れないよう、一時ファイルに書いてから置き換える。</summary>
    public static void Save<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
        File.Move(tmp, path, overwrite: true);
    }
}
