using Avalonia.Controls;
using Avalonia.Interactivity;
using InfraLauncher.Core;

namespace InfraLauncher.App;

public partial class MainWindow : Window
{
    private readonly HttpClient _http = new();
    private readonly LauncherService _service;
    private LauncherSettings _settings;
    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("InfraLauncher/0.1");
        _service = new LauncherService(InstallLayout.Default(), _http);
        _settings = _service.LoadSettings();
        Opened += async (_, _) =>
        {
            if (_settings.ManifestUrls.Count == 0 && _settings.Favorites.Count == 0)
            {
                StatusText.Text = "まず「設定」でサーバー一覧（マニフェスト）の URL を登録してください";
                return;
            }
            await RefreshAsync();
        };
    }

    private async void OnRefresh(object? sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        SetBusy(true, "サーバー一覧を取得しています…");
        try
        {
            var sources = await _service.LoadAllAsync(_settings.ManifestUrls);
            var rows = new List<ServerRow>();
            foreach (var src in sources)
            {
                foreach (var server in src.Manifest?.Servers ?? [])
                {
                    SyncPlan? plan = null;
                    string? error = null;
                    try { plan = _service.Sync.Plan(server, _settings); }
                    catch (SyncException ex) { error = ex.Message; }
                    rows.Add(new ServerRow(server, plan, error));
                }
            }
            rows.AddRange(_settings.Favorites.Select(f => new ServerRow(f)));
            ServerList.ItemsSource = rows;

            var errors = sources.Where(s => s.Error is not null).Select(s => s.Error!).ToList();
            StatusText.Text = errors.Count > 0
                ? "取得できなかった一覧があります: " + string.Join(" / ", errors)
                : $"{rows.Count} 件のサーバー（{DateTime.Now:HH:mm} 更新）";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        ConnectButton.IsEnabled = !_busy && ServerList.SelectedItem is ServerRow;

    private async void OnConnect(object? sender, RoutedEventArgs e)
    {
        if (_busy || ServerList.SelectedItem is not ServerRow row)
        {
            return;
        }
        SetBusy(true, $"{row.Name} に接続する準備をしています…");
        try
        {
            string command;
            if (row.Server is { } server)
            {
                var progress = new Progress<SyncProgress>(p =>
                {
                    StatusText.Text = $"{p.Item.Label}: {p.Stage}";
                    Progress.IsIndeterminate = p.BytesTotal is not > 0;
                    if (p.BytesTotal is > 0)
                    {
                        Progress.Value = p.BytesDone * 100.0 / p.BytesTotal.Value;
                    }
                });
                (_, command) = await _service.SyncAndLaunchAsync(server, _settings, progress: progress);
                row.SetPlan(_service.Sync.Plan(server, _settings), null);
            }
            else
            {
                (_, command) = LauncherService.LaunchFavorite(row.Favorite!, _settings);
            }
            StatusText.Text = $"起動しました: {command}";
        }
        catch (Exception ex) when (ex is SyncException or FormatException or FileNotFoundException or System.ComponentModel.Win32Exception)
        {
            StatusText.Text = $"エラー: {ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnSettings(object? sender, RoutedEventArgs e)
    {
        var window = new SettingsWindow(_settings);
        if (await window.ShowDialog<bool>(this))
        {
            _settings = window.Result;
            _settings.Save(_service.Layout);
            await RefreshAsync();
        }
    }

    private void SetBusy(bool busy, string? message = null)
    {
        _busy = busy;
        RefreshButton.IsEnabled = !busy;
        ConnectButton.IsEnabled = !busy && ServerList.SelectedItem is ServerRow;
        Progress.IsVisible = busy;
        Progress.IsIndeterminate = true;
        if (message is not null)
        {
            StatusText.Text = message;
        }
    }
}
