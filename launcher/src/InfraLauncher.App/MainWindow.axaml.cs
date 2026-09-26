using Avalonia.Controls;
using Avalonia.Interactivity;
using InfraLauncher.Core;

namespace InfraLauncher.App;

public partial class MainWindow : Window
{
    private readonly HttpClient _http = new();
    private readonly LauncherService _service;
    private readonly LauncherSettings _settings;
    private List<ServerRow> _rows = new();
    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("InfraLauncher/0.1");
        _service = new LauncherService(InstallLayout.Default(), _http);
        _settings = _service.LoadSettings();
        Opened += async (_, _) => await RefreshAsync();
    }

    private ServerRow? Selected => ServerList.SelectedItem as ServerRow;

    private async void OnRefresh(object? sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync(string? selectKey = null)
    {
        SetBusy(true, _settings.ServerLists.Count > 0 ? "サーバー一覧を取得しています…" : null);
        try
        {
            var sources = await _service.LoadAllAsync(_settings.ServerLists);
            var rows = new List<ServerRow>();
            foreach (var src in sources)
            {
                if (src.Manifest is null)
                {
                    rows.Add(ServerRow.Error(src.List, src.Error ?? "不明なエラー"));
                    continue;
                }
                foreach (var server in src.Manifest.Servers)
                {
                    SyncPlan? plan = null;
                    string? error = null;
                    try { plan = _service.Sync.Plan(server, _settings); }
                    catch (SyncException ex) { error = ex.Message; }
                    rows.Add(ServerRow.Listed(src.List, server, plan, error));
                }
            }
            rows.AddRange(_settings.ManualProfiles.Select(ServerRow.Manual));
            foreach (var r in rows)
            {
                r.IsFavorite = r.CanFavorite && _settings.IsFavorite(r.FavoriteKey);
            }
            _rows = rows;
            ShowRows(selectKey);

            var failed = sources.Count(s => s.Error is not null);
            StatusText.Text = failed > 0
                ? $"読み込めなかったサーバーリストが {failed} 件あります。選んで「編集」でアドレスを確認してください"
                : rows.Count > 0 ? $"{rows.Count} 件のサーバー（{DateTime.Now:HH:mm} 更新）" : "";
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>お気に入りを上に並べ、絞り込みを反映して表示する。</summary>
    private void ShowRows(string? selectKey = null)
    {
        selectKey ??= Selected is { } s ? RowKey(s) : null;
        var visible = _rows
            .Where(r => FavoritesOnly.IsChecked != true || r.IsFavorite)
            .OrderByDescending(r => r.IsFavorite)
            .ToList();
        ServerList.ItemsSource = visible;
        ServerList.SelectedItem = visible.FirstOrDefault(r => RowKey(r) == selectKey);

        EmptyText.IsVisible = visible.Count == 0;
        EmptyText.Text = _rows.Count == 0
            ? "サーバーがまだありません。\n「追加」から、サーバー管理者に共有されたリストを追加するか、プロファイルを手動で設定してください。"
            : "お気に入りの印を付けたサーバーはまだありません。\n一覧の ☆ を押すとお気に入りになります。";
        UpdateButtons();
    }

    private static string RowKey(ServerRow r) => r.Kind switch
    {
        ServerRowKind.ListError => $"error:{r.List!.Url}",
        _ => r.FavoriteKey,
    };

    private void OnFilterChanged(object? sender, RoutedEventArgs e) => ShowRows();

    private void OnToggleFavorite(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not ServerRow row || !row.CanFavorite)
        {
            return;
        }
        row.IsFavorite = !row.IsFavorite;
        _settings.SetFavorite(row.FavoriteKey, row.IsFavorite);
        _settings.Save(_service.Layout);
        ShowRows(RowKey(row));
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e) => UpdateButtons();

    private void UpdateButtons()
    {
        var row = Selected;
        ConnectButton.IsEnabled = !_busy && row is { CanConnect: true };
        ConnectButton.Content = row?.Kind == ServerRowKind.Listed ? "同期して接続" : "接続";
        EditButton.IsEnabled = !_busy && row is not null;
        DeleteButton.IsEnabled = !_busy && row is not null;
        AddButton.IsEnabled = !_busy;
        RefreshButton.IsEnabled = !_busy;
    }

    private async void OnAdd(object? sender, RoutedEventArgs e)
    {
        switch (await new AddKindWindow().ShowDialog<AddKind>(this))
        {
            case AddKind.List:
            {
                var w = new ServerListWindow(_service.Manifests, null);
                if (await w.ShowDialog<bool>(this) && w.Result is { } list)
                {
                    if (_settings.ServerLists.Any(l => l.Url == list.Url))
                    {
                        StatusText.Text = "そのサーバーリストはすでに追加されています";
                        return;
                    }
                    _settings.ServerLists.Add(list);
                    _settings.Save(_service.Layout);
                    await RefreshAsync();
                }
                break;
            }
            case AddKind.Manual:
            {
                var w = new ProfileWindow(null, _settings.SimutransExe);
                if (await w.ShowDialog<bool>(this) && w.Result is { } profile)
                {
                    _settings.ManualProfiles.Add(profile);
                    _settings.Save(_service.Layout);
                    await RefreshAsync(FavoriteKeys.ForManual(profile));
                }
                break;
            }
        }
    }

    private async void OnEdit(object? sender, RoutedEventArgs e)
    {
        var row = Selected;
        if (row is null)
        {
            return;
        }
        if (row.Kind == ServerRowKind.Manual)
        {
            var w = new ProfileWindow(row.Profile, _settings.SimutransExe);
            if (await w.ShowDialog<bool>(this) && w.Result is { } updated)
            {
                var i = _settings.ManualProfiles.FindIndex(p => p.Id == updated.Id);
                _settings.ManualProfiles[i] = updated;
                _settings.Save(_service.Layout);
                await RefreshAsync(FavoriteKeys.ForManual(updated));
            }
            return;
        }

        // 共有リストのサーバーは中身を管理者が管理しているので、リストの表示名と配信アドレスを編集する
        var list = row.List!;
        var note = row.Kind == ServerRowKind.Listed
            ? $"「{row.Name}」はサーバーリスト「{list.Name}」から配信されています。サーバーの内容はサーバー管理者が管理しているため、ここではリストの表示名と配信アドレスを変更できます。"
            : null;
        var lw = new ServerListWindow(_service.Manifests, list, note);
        if (await lw.ShowDialog<bool>(this) && lw.Result is { } edited)
        {
            // 配信アドレスが変わったら、お気に入りの印も引き継ぐ
            var oldPrefix = FavoriteKeys.ForListed(list, "");
            var newPrefix = FavoriteKeys.ForListed(edited, "");
            _settings.FavoriteKeys = _settings.FavoriteKeys
                .Select(k => k.StartsWith(oldPrefix, StringComparison.Ordinal) ? newPrefix + k[oldPrefix.Length..] : k)
                .ToList();
            list.Name = edited.Name;
            list.Url = edited.Url;
            _settings.Save(_service.Layout);
            await RefreshAsync();
        }
    }

    private async void OnDelete(object? sender, RoutedEventArgs e)
    {
        var row = Selected;
        if (row is null)
        {
            return;
        }
        if (row.Kind == ServerRowKind.Manual)
        {
            if (await Dialogs.ConfirmAsync(this, "削除", $"プロファイル「{row.Name}」を削除しますか？", "削除"))
            {
                _settings.ManualProfiles.RemoveAll(p => p.Id == row.Profile!.Id);
                _settings.SetFavorite(row.FavoriteKey, false);
                _settings.Save(_service.Layout);
                await RefreshAsync();
            }
            return;
        }

        // 共有リストのサーバーは1件だけ消すことはできないので、リストごと削除する
        var list = row.List!;
        var count = _rows.Count(r => r.Kind == ServerRowKind.Listed && r.List == list);
        var message = row.Kind == ServerRowKind.Listed
            ? $"「{row.Name}」はサーバーリスト「{list.Name}」から配信されています。\nリストごと削除すると、このリストの {count} 件のサーバーが一覧から消えます。削除しますか？"
            : $"サーバーリスト「{list.Name}」を削除しますか？";
        if (await Dialogs.ConfirmAsync(this, "削除", message, "リストを削除"))
        {
            var prefix = FavoriteKeys.ForListed(list, "");
            _settings.ServerLists.Remove(list);
            _settings.FavoriteKeys.RemoveAll(k => k.StartsWith(prefix, StringComparison.Ordinal));
            _settings.Save(_service.Layout);
            await RefreshAsync();
        }
    }

    private async void OnConnect(object? sender, RoutedEventArgs e)
    {
        if (_busy || Selected is not { CanConnect: true } row)
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
                (_, command) = LauncherService.LaunchManual(row.Profile!, _settings);
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
        var window = new SettingsWindow(_settings.SimutransExe);
        if (await window.ShowDialog<bool>(this))
        {
            _settings.SimutransExe = window.Result;
            _settings.Save(_service.Layout);
            await RefreshAsync();
        }
    }

    private void SetBusy(bool busy, string? message = null)
    {
        _busy = busy;
        Progress.IsVisible = busy;
        Progress.IsIndeterminate = true;
        if (message is not null)
        {
            StatusText.Text = message;
        }
        UpdateButtons();
    }
}
