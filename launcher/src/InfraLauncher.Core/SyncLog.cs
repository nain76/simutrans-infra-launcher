namespace InfraLauncher.Core;

/// <summary>
/// 同期の記録（&lt;データフォルダ&gt;/logs/sync.log）。止まったり失敗したりしたときに、どこで何が起きたかを調べるために残す。
/// 2 MB を超えたら sync.old.log に移して書き直す。記録に失敗しても同期は続ける。
/// </summary>
public static class SyncLog
{
    private static readonly object Gate = new();
    private const long MaxBytes = 2 * 1024 * 1024;

    /// <summary>記録するファイル。null なら記録しない。</summary>
    public static string? FilePath { get; set; }

    public static void Write(string message)
    {
        var path = FilePath;
        if (path is null)
        {
            return;
        }
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                {
                    File.Move(path, Path.ChangeExtension(path, ".old.log"), overwrite: true);
                }
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
