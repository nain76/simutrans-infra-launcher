using Avalonia.Controls;
using Avalonia.Interactivity;

namespace InfraLauncher.App;

public partial class SettingsWindow : Window
{
    public SettingsWindow() => InitializeComponent();

    public SettingsWindow(string? simutransExe, string? installRoot, string defaultRoot) : this()
    {
        SimutransExe.Text = simutransExe ?? "";
        InstallRoot.Text = installRoot ?? "";
        InstallRoot.Watermark = $"既定: {defaultRoot}";
    }

    public string? Result { get; private set; }
    public string? InstallRootResult { get; private set; }

    private async void OnBrowseRoot(object? sender, RoutedEventArgs e)
    {
        if (await Dialogs.PickFolderAsync(this, "ダウンロード先の既定フォルダを選択", InstallRoot.Text) is { } path)
        {
            InstallRoot.Text = path;
        }
    }

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        if (await Dialogs.PickFileAsync(this, "simutrans の実行ファイルを選択", Dialogs.SimutransExe) is { } path)
        {
            SimutransExe.Text = path;
        }
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        Result = string.IsNullOrWhiteSpace(SimutransExe.Text) ? null : SimutransExe.Text.Trim();
        InstallRootResult = string.IsNullOrWhiteSpace(InstallRoot.Text) ? null : InstallRoot.Text.Trim().Trim('"');
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
