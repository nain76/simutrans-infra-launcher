using System.Text;

namespace InfraLauncher.Core;

/// <summary>
/// ネットワークゲームで表示されるプレイヤー名（simutrans の nickname）を、本体の config/simuconf.tab に書き込む。
/// simutrans には名前を指定する起動オプションがないため、設定ファイルに書く。
/// simutrans は settings.xml を読んだあとに config/simuconf.tab を読むので、ここに書いた名前が優先される。
/// また、同じ項目が2回あると最初の行が使われるので、ファイルの先頭に書く。
/// ランチャーが書いた行は目印で囲み、書き直すときはその部分だけを入れ替える。
/// </summary>
public static class NicknameConfig
{
    public const int MaxLength = 32;
    private const string Begin = "# >>> InfraLauncher: player name (managed by the launcher)";
    private const string End = "# <<< InfraLauncher";

    /// <summary>名前として使えるか。使えなければ理由を返す。</summary>
    public static string? Validate(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }
        name = name.Trim();
        if (name.Length > MaxLength)
        {
            return $"プレイヤー名は {MaxLength} 文字以内にしてください";
        }
        if (name.Any(char.IsControl) || name.StartsWith('#'))
        {
            return "プレイヤー名に改行などの記号は使えません。また、# で始めることはできません";
        }
        return null;
    }

    /// <summary>
    /// 本体のフォルダの config/simuconf.tab に名前を書く。名前が空なら、ランチャーが書いた行を消す。
    /// ファイルがなければ何もしない（同期で入る）。書き換えたら true。
    /// </summary>
    public static bool Apply(string engineDir, string? name)
    {
        var path = Path.Combine(engineDir, "config", "simuconf.tab");
        if (!File.Exists(path) || Validate(name) is not null)
        {
            return false;
        }
        var text = File.ReadAllText(path);
        var newline = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Split(["\r\n", "\n"], StringSplitOptions.None).ToList();
        var begin = lines.IndexOf(Begin);
        var end = begin < 0 ? -1 : lines.IndexOf(End, begin);
        if (begin >= 0 && end > begin)
        {
            lines.RemoveRange(begin, end - begin + 1);
        }
        if (!string.IsNullOrWhiteSpace(name))
        {
            lines.InsertRange(0, [Begin, $"nickname = {name.Trim()}", End]);
        }
        var updated = string.Join(newline, lines);
        if (updated == text)
        {
            return false;
        }
        File.WriteAllText(path, updated, new UTF8Encoding(false));
        return true;
    }
}
