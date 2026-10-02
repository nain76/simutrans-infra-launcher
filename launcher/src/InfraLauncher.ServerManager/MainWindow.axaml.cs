using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using InfraLauncher.Core;
using InfraLauncher.Core.Admin;

namespace InfraLauncher.ServerManager;

/// <summary>
/// サーバー管理ツールの画面。今の状態（公開アドレス、確認コード、署名）を見せ、お知らせなどの書き換えと、
/// 各スクリプトの呼び出しをまとめる。ファイルの書き換えはすべて server-setup のスクリプトが行う。
/// </summary>
public partial class MainWindow : Window
{
    private static readonly IBrush Green = new SolidColorBrush(Color.Parse("#1f7a3a"));
    private static readonly IBrush Orange = new SolidColorBrush(Color.Parse("#b35c00"));
    private static readonly IBrush Red = new SolidColorBrush(Color.Parse("#b3261e"));
    private static readonly IBrush Gray = new SolidColorBrush(Color.Parse("#5f6368"));

    private string? _folder;
    private ServerSetup? _setup;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public MainWindow()
    {
        InitializeComponent();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Simutrans_ServerManager/0.1");
        _folder = RememberedFolder() ?? ServerSetup.FindFolder(AppContext.BaseDirectory);
        Reload();
    }

    private AdminServer? SelectedServer => ServerList.SelectedItem as AdminServer;

    private void Reload(string? selectId = null, bool keepSelection = true)
    {
        if (_folder is null)
        {
            FolderText.Text = "server-setupフォルダが見つかりません。このツールをserver-setupフォルダに置くか、「フォルダを選ぶ」で選んでください";
            Editor.IsEnabled = false;
            return;
        }
        if (keepSelection)
        {
            selectId ??= SelectedServer?.Id;
        }
        _setup = ServerSetup.Load(_folder);
        FolderText.Text = $"server-setupフォルダ: {_folder}";

        ShareUrlText.Text = _setup.ShareUrl ?? "まだ記録がありません（Setup-Server.batかEnable-Https.batを実行すると記録されます）";
        CodeText.Text = _setup.KeyCode ?? "署名の鍵がありません（「署名の鍵の管理」で作れます）";
        ManifestText.Text = _setup.ManifestPath ?? "まだありません（「新規構築を始める」で作れます）";
        FirstSetupPanel.IsVisible = _setup.ManifestPath is null;
        GuessableWarning.IsVisible = _setup.HasGuessableName;
        (SignatureText.Text, SignatureBadge.Background) = _setup.Signature switch
        {
            SignatureState.Ok => ("✔ この管理者の鍵で正しく署名されています", Green),
            SignatureState.Missing => ("署名がありません。「署名の鍵の管理」の3で署名してください", Red),
            SignatureState.Invalid => ("署名が中身と合いません（サーバーリストを手で書き換えた場合など）。「署名の鍵の管理」の3で署名し直してください", Red),
            SignatureState.OtherKey when _setup.KeyCode is null => ("このWindowsユーザーには署名の鍵がありません。鍵を作ったユーザーで開いてください", Orange),
            SignatureState.OtherKey => ("このWindowsユーザーの鍵とは別の鍵で署名されています", Orange),
            _ => ("サーバーリストがまだありません", Gray),
        };
        if (_setup.Error is { } error)
        {
            Log($"読み込めないファイルがありました: {error}");
        }

        ServerList.ItemsSource = _setup.Servers;
        ServerList.SelectedItem = _setup.Servers.FirstOrDefault(s => s.Id == selectId);
        ShowSelected();
    }

    private void ShowSelected()
    {
        var s = SelectedServer;
        Editor.IsEnabled = s is not null && _setup?.KeyCode is not null;
        NameBox.Text = s?.Name ?? "";
        MessageBox.Text = s?.Message ?? "";
        MaintenanceBox.IsChecked = s?.Maintenance == true;
        ServerInfoText.Text = s is null ? (_setup?.Servers.Count > 0 ? "左の一覧から、書き換えるサーバーを選んでください" : "")
            : $"id: {s.Id}　｜　接続先: {s.Address}　｜　pakset: {s.PaksetFolder ?? "-"}　｜　本体: {s.EngineRevision ?? "配っていない"}";
        if (s is not null && _setup?.KeyCode is null)
        {
            ServerInfoText.Text += "\n署名の鍵がないため、保存できません。先に「署名の鍵の管理」で鍵を作ってください";
        }
    }

    private void OnServerSelected(object? sender, SelectionChangedEventArgs e) => ShowSelected();

    private void OnRevert(object? sender, RoutedEventArgs e) => ShowSelected();

    private void OnReload(object? sender, RoutedEventArgs e)
    {
        Reload();
        Log("読み込み直しました");
    }

    /// <summary>表示名・お知らせ・メンテナンス中を、Edit-ServerList.ps1 で書き換えて署名する。</summary>
    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        if (_folder is null || SelectedServer is not { } s)
        {
            return;
        }
        var name = NameBox.Text?.Trim() ?? "";
        if (name.Length == 0)
        {
            Log("表示名を空にはできません");
            return;
        }
        var message = MessageBox.Text?.Trim() ?? "";
        var args = new List<KeyValuePair<string, object?>>
        {
            new("ServerId", s.Id),
            new("Name", name),
            new("Maintenance", MaintenanceBox.IsChecked == true),
        };
        args.Add(message.Length == 0 ? new("ClearMessage", null) : new("Message", message));

        SaveButton.IsEnabled = false;
        Log($"「{name}」を保存しています…");
        var (ok, output) = await Scripts.RunAsync(_folder, "Edit-ServerList.ps1", args);
        SaveButton.IsEnabled = true;
        Log(ok ? $"保存して署名しました。友人のランチャーには、次に一覧を更新したときに出ます。\n{output}" : $"保存できませんでした。\n{output}");
        // 保存できたら選択を外し、続けて書き換えてしまわないようにする。失敗したときは直せるように残す
        Reload(ok ? null : s.Id, keepSelection: !ok);
    }

    /// <summary>公開アドレスから取ったサーバーリストを、友人のランチャーと同じ方法で確かめる。</summary>
    private async void OnCheckDistribution(object? sender, RoutedEventArgs e)
    {
        if (_setup?.ShareUrl is not { } url)
        {
            Log("公開アドレスの記録がありません。Setup-Server.batかEnable-Https.batを実行すると記録されます");
            return;
        }
        Log($"{url} から取得しています…");
        try
        {
            var manifest = await new ManifestClient(_http).LoadAsync(new Uri(url), _setup.KeyPublicKey);
            Log(_setup.KeyPublicKey is null
                ? $"取得できました（サーバー {manifest.Servers.Count}台）。署名の鍵がこのユーザーにないため、署名は確かめていません"
                : $"取得でき、署名も正しいことを確かめました（サーバー {manifest.Servers.Count}台）。友人のランチャーからも同じように読めます");
        }
        catch (Exception ex) when (ex is ManifestException or UriFormatException)
        {
            Log($"確かめられませんでした: {ex.Message}");
        }
    }

    // バッチファイルと、それが動かすスクリプト。ツールからはスクリプトを直接開き、画面が閉じたら読み込み直す
    private void OnPublish(object? sender, RoutedEventArgs e) => OpenBatch("Publish-Pakset.bat", "Publish-Pakset.ps1");
    private void OnAddServer(object? sender, RoutedEventArgs e) => OpenBatch("Add-Server.bat", "Add-Server.ps1");
    private void OnManageKey(object? sender, RoutedEventArgs e) => OpenBatch("Manage-SigningKey.bat", "Manage-SigningKey.ps1");
    private void OnRename(object? sender, RoutedEventArgs e) => OpenBatch("Rename-ServerList.bat", "Rename-ServerList.ps1");
    private void OnHttps(object? sender, RoutedEventArgs e) => OpenBatch("Enable-Https.bat", "Enable-Https.ps1");
    private void OnSetup(object? sender, RoutedEventArgs e) => OpenBatch("Setup-Server.bat", "Install-DistServer.ps1");

    private void OnOpenFolder(object? sender, RoutedEventArgs e)
    {
        if (_folder is not null)
        {
            Scripts.OpenFolder(_folder);
        }
    }

    private void OpenBatch(string batch, string script)
    {
        if (_folder is null)
        {
            Log("server-setupフォルダを選んでください");
            return;
        }
        var error = Scripts.OpenInteractive(_folder, script, () => Dispatcher.UIThread.Post(() =>
        {
            Reload();
            Log($"{batch}（{script}）の画面が閉じられたので、読み込み直しました");
        }));
        Log(error ?? $"{batch}（{script}）をPowerShellの画面で開きました。質問に答えて、終わったらその画面を閉じてください。閉じると表示が新しくなります");
    }

    private async void OnCopyShareUrl(object? sender, RoutedEventArgs e)
    {
        if (_setup?.ShareUrl is { } url)
        {
            await CopyAsync(url, "公開アドレスをコピーしました");
        }
    }

    private async void OnCopyCode(object? sender, RoutedEventArgs e)
    {
        if (_setup?.KeyCode is { } code)
        {
            await CopyAsync(code, "確認コードをコピーしました。DiscordのDMなどで友人に伝えてください");
        }
    }

    private async Task CopyAsync(string text, string done)
    {
        if (GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
            Log(done);
        }
    }

    private async void OnPickFolder(object? sender, RoutedEventArgs e)
    {
        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "server-setupフォルダを選択", AllowMultiple = false });
        if (picked.FirstOrDefault()?.TryGetLocalPath() is not { } path)
        {
            return;
        }
        if (!ServerSetup.IsSetupFolder(path))
        {
            Log($"server-setupフォルダではありません（Common.ps1がありません）: {path}");
            return;
        }
        _folder = path;
        Remember(path);
        Reload();
    }

    private void Log(string text) => LogText.Text = $"{DateTime.Now:HH:mm:ss} {text}";

    // 選んだフォルダは %LOCALAPPDATA%\InfraLauncherServer\manager.json に覚えておく
    private static string SettingsPath =>
        Path.Combine(Path.GetDirectoryName(ServerSetup.DefaultKeyFile)!, "manager.json");

    private static string? RememberedFolder()
    {
        try
        {
            var folder = File.Exists(SettingsPath) ? JsonNode.Parse(File.ReadAllText(SettingsPath))?["setup_folder"]?.GetValue<string>() : null;
            return folder is not null && ServerSetup.IsSetupFolder(folder) ? folder : null;
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static void Remember(string folder)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, new JsonObject { ["setup_folder"] = folder }.ToJsonString());
        }
        catch (IOException)
        {
        }
    }
}
