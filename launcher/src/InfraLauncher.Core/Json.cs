using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.Unicode;
using InfraLauncher.Core.Models;

namespace InfraLauncher.Core;

/// <summary>
/// JSONの読み書き。exeを小さくするために未使用コードを削っても動くよう、
///リフレクションではなくソース生成（<see cref="JsonContext"/>）を使う。
/// </summary>
internal static class Json
{
    /// <summary>マニフェストや保存ファイルはsnake_case。日本語はエスケープせずに書く。</summary>
    public static readonly JsonContext Context = new(new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    });

    public static T Load<T>(string path, JsonTypeInfo<T> type) where T : new()
    {
        if (!File.Exists(path))
        {
            return new T();
        }
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize(stream, type) ?? new T();
    }

    /// <summary>書き込み途中で落ちても壊れないよう、一時ファイルに書いてから置き換える。</summary>
    public static void Save<T>(string path, T value, JsonTypeInfo<T> type)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, type));
        File.Move(tmp, path, overwrite: true);
    }
}

[JsonSerializable(typeof(Manifest))]
[JsonSerializable(typeof(PaksetIndex))]
[JsonSerializable(typeof(LauncherSettings))]
[JsonSerializable(typeof(InstalledState))]
[JsonSerializable(typeof(SignatureFile))]
internal sealed partial class JsonContext : JsonSerializerContext;
