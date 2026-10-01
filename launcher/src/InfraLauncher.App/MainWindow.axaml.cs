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
    /// <summary>同期中なら、中止に使う。</summary>
    private CancellationTokenSource? _syncCts;
    /// <summary>最後に進み具合が届いた時刻（止まっていないかを見るため）。</summary>
    private DateTime _lastProgress;
    private string _lastStage = "";

    public MainWindow()
    {
        InitializeComponent();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("InfraLauncher/0.1");
        _service = new LauncherService(InstallLayout.Default(), _http);
        SyncLog.FilePath = _service.Layout.SyncLogPath;
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
                    try { plan = _service.Sync.Plan(server, _settings, OptionsFor(src.List, server)); }
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
            _ = ProbeAsync(rows);

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

    /// <summary>各サーバーのポートにつながるかを確かめ、稼働状況の札に反映する（一覧の表示は待たない）。</summary>
    private static async Task ProbeAsync(IEnumerable<ServerRow> rows)
    {
        await Task.WhenAll(rows.Where(r => r is { Kind: ServerRowKind.Listed, Server: not null }).Select(async r =>
        {
            var ok = ServerAddress.TryParse(r.Server!.Address, out var address)
                && await ServerProbe.IsReachableAsync(address, TimeSpan.FromSeconds(4));
            r.SetReachable(ok);
        }));
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
        // 同期中は「同期」ボタンを「中止」にする
        SyncButton.Content = _syncCts is null ? "同期" : "中止";
        SyncButton.IsEnabled = _syncCts is not null || !_busy && row is { CanSync: true };
        InstallOptionsButton.IsEnabled = !_busy && row is { Kind: ServerRowKind.Listed, Server.EngineDownloadAllowed: true };
        LaunchButton.IsEnabled = !_busy && row is { IsReady: true };
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
            // インストール設定も引き継ぐ
            foreach (var key in _settings.ServerInstall.Keys.Where(k => k.StartsWith(oldPrefix, StringComparison.Ordinal)).ToList())
            {
                var value = _settings.ServerInstall[key];
                _settings.ServerInstall.Remove(key);
                _settings.ServerInstall[newPrefix + key[oldPrefix.Length..]] = value;
            }
            list.Name = edited.Name;
            list.Url = edited.Url;
            list.PublicKey = edited.PublicKey;
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
            foreach (var key in _settings.ServerInstall.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            {
                _settings.ServerInstall.Remove(key);
            }
            _settings.Save(_service.Layout);
            await RefreshAsync();
        }
    }

    private void OnDoubleTapped(object? sender, RoutedEventArgs e)
    {
        // ダブルクリックは、起動できるなら起動、まだなら同期（同期だけでは何も実行しない）
        if (Selected is { IsReady: true })
        {
            OnLaunch(sender, e);
        }
        else if (Selected is { CanSync: true })
        {
            OnSync(sender, e);
        }
    }

    /// <summary>本体と pakset をサーバーと同じ状態にする。起動はしない。</summary>
    private async void OnSync(object? sender, RoutedEventArgs e)
    {
        if (_syncCts is { } running)
        {
            running.Cancel();
            StatusText.Text = "中止しています…";
            return;
        }
        if (_busy || Selected is not { CanSync: true, Server: { } server } row)
        {
            return;
        }
        SetBusy(true, $"{row.Name} を同期しています…");
        using var cts = new CancellationTokenSource();
        // 進み具合が長いあいだ届かなければ、考えられる原因と対処を出す
        var watchdog = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        watchdog.Tick += (_, _) =>
        {
            if (DateTime.Now - _lastProgress > TimeSpan.FromSeconds(60))
            {
                StatusText.Text = $"{_lastStage}\n1分以上進んでいません。ダウンロード先が OneDrive などの同期フォルダの中だと止まることがあります。" +
                    "「中止」を押し、「インストール設定」でダウンロード先を OneDrive の外に変えてから、もう一度「同期」を押してください。" +
                    $"詳しい記録: {_service.Layout.SyncLogPath}";
            }
        };
        try
        {
            var progress = new Progress<SyncProgress>(p =>
            {
                // 進み具合は後から届くことがあるので、同期が終わったあとに届いたものは捨てる（結果の表示を上書きしないように）
                if (!ReferenceEquals(_syncCts, cts))
                {
                    return;
                }
                _lastProgress = DateTime.Now;
                var percent = p.BytesTotal is > 0 ? $"  {p.BytesDone * 100 / p.BytesTotal.Value}%" : "";
                StatusText.Text = _lastStage = $"{p.Item.Label}: {p.Stage}{percent}";
                Progress.IsIndeterminate = p.BytesTotal is not > 0;
                if (p.BytesTotal is > 0)
                {
                    Progress.Value = p.BytesDone * 100.0 / p.BytesTotal.Value;
                }
            });
            // 本体を配っているサーバーを初めて同期するときは、先にインストール設定を決めてもらう
            if (server.EngineDownloadAllowed && !_settings.ServerInstall.ContainsKey(row.FavoriteKey))
            {
                if (!await EditInstallOptionsAsync(row))
                {
                    StatusText.Text = "同期を取りやめました";
                    return;
                }
                SetBusy(true, $"{row.Name} を同期しています…");
            }
            var options = OptionsFor(row);
            _syncCts = cts;
            _lastProgress = DateTime.Now;
            _lastStage = StatusText.Text ?? "";
            watchdog.Start();
            UpdateButtons();
            var summary = await _service.SyncServerAsync(server, _settings, progress, cts.Token, options);
            _syncCts = null;
            row.SetPlan(_service.Sync.Plan(server, _settings, options), null);
            StatusText.Text = summary.Downloads == 0 && summary.Removed == 0
                ? $"{row.Name}: すでに最新です。「起動」で接続できます"
                : $"{row.Name}: 同期しました（ダウンロード {summary.Downloads} 件）。「起動」で接続できます";
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            _syncCts = null;
            StatusText.Text = "同期を中止しました。もう一度「同期」を押すと、ダウンロード済みのファイルは使い回して続きから始めます";
        }
        catch (Exception ex) when (ex is SyncException or FormatException or IOException or UnauthorizedAccessException)
        {
            _syncCts = null;
            SyncLog.Write($"エラー: {ex}");
            StatusText.Text = $"エラー: {ex.Message}\n詳しい記録: {_service.Layout.SyncLogPath}";
        }
        finally
        {
            watchdog.Stop();
            _syncCts = null;
            SetBusy(false);
        }
    }

    /// <summary>simutrans を起動して接続する。配布元から入れた本体は、初回（と中身が変わったとき）に確認する。</summary>
    private async void OnLaunch(object? sender, RoutedEventArgs e)
    {
        if (_busy || Selected is not { IsReady: true } row)
        {
            return;
        }
        try
        {
            var info = row.Server is { } server
                ? _service.PrepareLaunch(server, _settings, OptionsFor(row))
                : LauncherService.PrepareManual(row.Profile!, _settings);
            if (info.NeedsApproval)
            {
                var dialog = new ExeApprovalWindow(info, row.Name, row.List?.Name);
                if (!await dialog.ShowDialog<bool>(this))
                {
                    StatusText.Text = "起動を取りやめました";
                    return;
                }
                _settings.Approve(info.ExeSha256);
                _settings.Save(_service.Layout);
            }
            LauncherService.Launch(info, _settings);
            StatusText.Text = $"起動しました: {info.Command}";
        }
        catch (Exception ex) when (ex is SyncException or FormatException or FileNotFoundException or System.ComponentModel.Win32Exception)
        {
            StatusText.Text = $"エラー: {ex.Message}";
            if (row.Server is { } s)
            {
                try { row.SetPlan(_service.Sync.Plan(s, _settings, OptionsFor(row)), null); } catch (SyncException) { }
            }
            UpdateButtons();
        }
    }

    private InstallOptions? OptionsFor(ServerRow row) => _settings.ServerInstall.GetValueOrDefault(row.FavoriteKey);

    private InstallOptions? OptionsFor(ServerListSource list, InfraLauncher.Core.Models.ServerEntry server) =>
        _settings.ServerInstall.GetValueOrDefault(FavoriteKeys.ForListed(list, server.Id));

    private async void OnInstallOptions(object? sender, RoutedEventArgs e)
    {
        if (_busy || Selected is not { Kind: ServerRowKind.Listed, Server: { } server } row)
        {
            return;
        }
        SetBusy(true, "本体のファイル構成を取得しています…");
        try
        {
            if (await EditInstallOptionsAsync(row))
            {
                row.SetPlan(_service.Sync.Plan(server, _settings, OptionsFor(row)), null);
                StatusText.Text = "インストール設定を保存しました。「同期」で反映します";
            }
            else
            {
                StatusText.Text = "インストール設定は変えませんでした";
            }
        }
        catch (Exception ex) when (ex is SyncException or FormatException)
        {
            StatusText.Text = $"エラー: {ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>インストール設定の画面を出して保存する。保存したら true。</summary>
    private async Task<bool> EditInstallOptionsAsync(ServerRow row)
    {
        var server = row.Server!;
        var index = await _service.Sync.LoadEngineIndexAsync(server);
        if (index is null)
        {
            // zip 方式の本体など、部品を選べないサーバーはダウンロード先だけ選べるようにする
            index = new InfraLauncher.Core.Models.PaksetIndex { SchemaVersion = 1 };
        }
        var pakset = await _service.Sync.LoadPaksetIndexAsync(server);
        var dialog = new InstallOptionsWindow(row.Name, _service.Sync.InstallRoot(_settings, null), OptionsFor(row), index, server.Pakset.DisplayName,
            pakset?.Files.Sum(f => f.Size));
        if (!await dialog.ShowDialog<bool>(this) || dialog.Result is null)
        {
            return false;
        }
        _settings.ServerInstall[row.FavoriteKey] = dialog.Result;
        _settings.Save(_service.Layout);
        return true;
    }

    private async void OnSettings(object? sender, RoutedEventArgs e)
    {
        var window = new SettingsWindow(_settings.SimutransExe, _settings.InstallRoot, _service.Layout.DefaultInstallRoot);
        if (await window.ShowDialog<bool>(this))
        {
            _settings.SimutransExe = window.Result;
            _settings.InstallRoot = window.InstallRootResult;
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
