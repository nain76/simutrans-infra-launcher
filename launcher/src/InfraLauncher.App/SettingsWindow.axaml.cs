using Avalonia.Controls;
using Avalonia.Interactivity;
using InfraLauncher.Core;

namespace InfraLauncher.App;

public partial class SettingsWindow : Window
{
    public SettingsWindow() => InitializeComponent();

    public SettingsWindow(string? simutransExe, string? installRoot, string defaultRoot, string? nickname) : this()
    {
        NicknameBox.Text = nickname ?? "";
        SimutransExe.Text = simutransExe ?? "";
        InstallRoot.Text = installRoot ?? "";
        InstallRoot.Watermark = $"既定: {defaultRoot}";
        AdviceText.Text = SyncFolders.Advice;
    }

    private void OnRootChanged(object? sender, TextChangedEventArgs e)
    {
        if (SyncWarning is null) return;
        var service = SyncFolders.Detect(InstallRoot.Text);
        SyncWarning.IsVisible = service is not null;
        SyncWarningText.Text = service is null ? "" : SyncFolders.Warning(service);
    }

    public string? Result { get; private set; }
    public string? InstallRootResult { get; private set; }
    public string? NicknameResult { get; private set; }

    private async void OnBrowseRoot(object? sender, RoutedEventArgs e)
    {
        if (await Dialogs.PickFolderAsync(this, "ダウンロード先の既定フォルダを選択", InstallRoot.Text) is { } path)
        {
            InstallRoot.Text = path;
        }
    }

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        if (await Dialogs.PickFileAsync(this, "simutransの実行ファイルを選択", Dialogs.SimutransExe) is { } path)
        {
            SimutransExe.Text = path;
        }
    }

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        if (NicknameConfig.Validate(NicknameBox.Text) is { } problem)
        {
            NicknameError.Text = problem;
            NicknameError.IsVisible = true;
            return;
        }
        NicknameResult = string.IsNullOrWhiteSpace(NicknameBox.Text) ? null : NicknameBox.Text.Trim();
        if (SyncFolders.Detect(InstallRoot.Text) is { } service
            && !await Dialogs.ConfirmAsync(this, "ダウンロード先の確認", SyncFolders.Warning(service) + "\n\nこのまま保存しますか？", "このまま保存"))
        {
            return;
        }
        Result = string.IsNullOrWhiteSpace(SimutransExe.Text) ? null : SimutransExe.Text.Trim();
        InstallRootResult = string.IsNullOrWhiteSpace(InstallRoot.Text) ? null : InstallRoot.Text.Trim().Trim('"');
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
