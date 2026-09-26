using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using InfraLauncher.Core;

namespace InfraLauncher.App;

public partial class SettingsWindow : Window
{
    public SettingsWindow() : this(new LauncherSettings()) { }

    public SettingsWindow(LauncherSettings current)
    {
        InitializeComponent();
        ManifestUrls.Text = string.Join(Environment.NewLine, current.ManifestUrls);
        SimutransExe.Text = current.SimutransExe ?? "";
        Favorites.Text = string.Join(Environment.NewLine,
            current.Favorites.Select(f => $"{f.Name}, {f.Address}, {f.PaksetFolder}"));
    }

    public LauncherSettings Result { get; private set; } = new();

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "simutrans の実行ファイルを選択",
            AllowMultiple = false,
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
        {
            SimutransExe.Text = path;
        }
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        try
        {
            Result = new LauncherSettings
            {
                ManifestUrls = Lines(ManifestUrls.Text).ToList(),
                SimutransExe = string.IsNullOrWhiteSpace(SimutransExe.Text) ? null : SimutransExe.Text.Trim(),
                Favorites = Lines(Favorites.Text).Select(ParseFavorite).ToList(),
            };
        }
        catch (FormatException ex)
        {
            ErrorText.Text = ex.Message;
            ErrorText.IsVisible = true;
            return;
        }
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    private static IEnumerable<string> Lines(string? text) =>
        (text ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);

    private static FavoriteServer ParseFavorite(string line)
    {
        var parts = line.Split(',').Select(p => p.Trim()).ToArray();
        if (parts.Length != 3 || parts.Any(p => p.Length == 0) || !ServerAddress.TryParse(parts[1], out _))
        {
            throw new FormatException($"お気に入りの形式が正しくありません: {line}（名前, アドレス, pak フォルダ名）");
        }
        return new FavoriteServer { Name = parts[0], Address = parts[1], PaksetFolder = parts[2] };
    }
}
