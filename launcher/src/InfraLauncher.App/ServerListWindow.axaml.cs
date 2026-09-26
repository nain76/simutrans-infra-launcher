using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using InfraLauncher.Core;

namespace InfraLauncher.App;

/// <summary>共有されたサーバーリストの追加・編集。保存の前に実際に読み込んで確かめる。</summary>
public partial class ServerListWindow : Window
{
    private readonly ManifestClient? _client;

    public ServerListWindow() => InitializeComponent();

    public ServerListWindow(ManifestClient client, ServerListSource? existing, string? note = null) : this()
    {
        _client = client;
        Title = existing is null ? "共有されたサーバーリストを追加" : "サーバーリストを編集";
        NameBox.Text = existing?.Name ?? "";
        UrlBox.Text = existing?.Url ?? "";
        if (note is not null)
        {
            Note.Text = note;
            Note.IsVisible = true;
        }
    }

    public ServerListSource? Result { get; private set; }

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        if (await Dialogs.PickFileAsync(this, "サーバーリストのファイルを選択", Dialogs.JsonFile) is { } path)
        {
            UrlBox.Text = path;
        }
    }

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        var url = UrlBox.Text?.Trim() ?? "";
        if (url.Length == 0)
        {
            Show("配信アドレスを入れてください", error: true);
            return;
        }

        SaveButton.IsEnabled = false;
        Show("読み込んで確認しています…", error: false);
        try
        {
            var manifest = await _client!.LoadAsync(LauncherService.ToUri(url));
            var name = string.IsNullOrWhiteSpace(NameBox.Text)
                ? manifest.Servers.FirstOrDefault()?.Name ?? "サーバーリスト"
                : NameBox.Text.Trim();
            Result = new ServerListSource { Name = name, Url = url };
            Close(true);
        }
        catch (Exception ex) when (ex is ManifestException or UriFormatException or ArgumentException)
        {
            Show($"読み込めませんでした: {ex.Message}", error: true);
        }
        finally
        {
            SaveButton.IsEnabled = true;
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    private void Show(string text, bool error)
    {
        ResultText.Text = text;
        ResultText.Foreground = error ? new SolidColorBrush(Color.Parse("#d13438")) : null;
        ResultText.IsVisible = true;
    }
}
