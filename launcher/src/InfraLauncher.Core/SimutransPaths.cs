namespace InfraLauncher.Core;

public static class SimutransPaths
{
    /// <summary>
    ///本体がデータフォルダとして使う場所。-objectsのフォルダはここから見た位置になる。
    /// simmain.ccと同じく、実行ファイルのあるフォルダ。macOSの.appの中なら.appのあるフォルダ。
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
