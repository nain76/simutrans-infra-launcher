namespace InfraLauncher.Core;

public static class LaunchCommandBuilder
{
    /// <summary>
    /// サーバーへ接続する起動引数。
    /// -objects: 使う pakset（simmain.cc の -objects 処理）
    /// -noaddons: 個人のアドオンが混ざってチェックサムがずれるのを防ぐ
    /// -load net:host:port: 指定サーバーへ接続（simmain.cc の -load 処理）
    /// </summary>
    public static IReadOnlyList<string> Build(string paksetFolder, ServerAddress address) =>
    [
        "-objects", paksetFolder.TrimEnd('/', '\\') + "/",
        "-noaddons",
        "-load", $"net:{address}",
    ];

    /// <summary>表示・ログ用のコマンド文字列。</summary>
    public static string ToDisplayString(string exe, IEnumerable<string> args) =>
        string.Join(' ', new[] { exe }.Concat(args).Select(Quote));

    private static string Quote(string s) =>
        s.Length > 0 && !s.Any(c => char.IsWhiteSpace(c) || c == '"') ? s : $"\"{s.Replace("\"", "\\\"")}\"";
}
