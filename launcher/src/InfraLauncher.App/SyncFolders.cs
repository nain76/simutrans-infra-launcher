namespace InfraLauncher.App;

/// <summary>OneDriveなどのクラウド同期フォルダかどうか。数百MBのゲームのファイルを置くと、同期されたり「オンライン専用」になったりして困る。</summary>
public static class SyncFolders
{
    private static readonly string[] Names = ["OneDrive", "Dropbox", "Google Drive", "GoogleDrive", "iCloudDrive", "iCloud Drive", "Box"];

    /// <summary>同期フォルダの中なら、そのサービスの名前を返す。</summary>
    public static string? Detect(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
        var full = path.Trim().Trim('"');
        var segments = full.Split('\\', '/');
        // OneDriveは「OneDrive -会社名」のようなフォルダ名にもなる
        return Names.FirstOrDefault(n => segments.Any(s => s.Equals(n, StringComparison.OrdinalIgnoreCase)
            || s.StartsWith(n + " - ", StringComparison.OrdinalIgnoreCase)));
    }

    public const string Advice =
        "おすすめはC:\\Games\\simutransのように、Cドライブの直下に作ったフォルダです。" +
        "「ドキュメント」や「デスクトップ」はOneDriveに同期されていることがあるので避けてください。";

    public static string Warning(string service) =>
        $"{service}のフォルダの中です。数百MBのファイルがクラウドに同期されたり、「オンライン専用」になってsimutransが読み込めなくなったり、" +
        "ダウンロードが止まったりします。C:\\Games\\simutransのようなCドライブ直下のフォルダに変えてください。";
}
