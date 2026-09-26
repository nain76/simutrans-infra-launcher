namespace InfraLauncher.Core;

public static class SimutransPaths
{
    /// <summary>
    /// 本体がデータフォルダとして使う場所。-objects のフォルダはここから見た位置になる。
    /// simmain.cc と同じく、実行ファイルのあるフォルダ。macOS の .app の中なら .app のあるフォルダ。
    /// </summary>
    public static string DataDirFor(string exePath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(exePath))!;
        const string bundleSuffix = ".app/Contents/MacOS";
        var normalized = dir.Replace('\\', '/');
        if (normalized.EndsWith(bundleSuffix, StringComparison.Ordinal))
        {
            var bundle = dir[..(dir.Length - "/Contents/MacOS".Length)];
            return Path.GetDirectoryName(bundle)!;
        }
        return dir;
    }
}
