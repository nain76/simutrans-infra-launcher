using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;

namespace InfraLauncher.App;

internal static class Dialogs
{
    /// <summary>はい／いいえの確認。</summary>
    public static Task<bool> ConfirmAsync(Window owner, string title, string message, string yes)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 440,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var yesButton = new Button { Content = yes, Classes = { "accent" } };
        var noButton = new Button { Content = "キャンセル" };
        yesButton.Click += (_, _) => dialog.Close(true);
        noButton.Click += (_, _) => dialog.Close(false);
        dialog.Content = new StackPanel
        {
            Margin = new(16),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { noButton, yesButton },
                },
            },
        };
        return dialog.ShowDialog<bool>(owner);
    }

    public static async Task<string?> PickFileAsync(Window owner, string title, params FilePickerFileType[] types)
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = types.Length > 0 ? types : null,
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public static async Task<string?> PickFolderAsync(Window owner, string title, string? startIn)
    {
        var start = string.IsNullOrEmpty(startIn) || !Directory.Exists(startIn)
            ? null
            : await owner.StorageProvider.TryGetFolderFromPathAsync(startIn);
        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = start,
        });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public static readonly FilePickerFileType SimutransExe = new("simutransの実行ファイル")
    {
        Patterns = OperatingSystem.IsWindows() ? ["*.exe"] : ["*"],
    };

    public static readonly FilePickerFileType JsonFile = new("サーバーリスト（JSON）") { Patterns = ["*.json"] };
}
