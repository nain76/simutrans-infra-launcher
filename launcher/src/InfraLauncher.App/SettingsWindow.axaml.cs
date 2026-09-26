using Avalonia.Controls;
using Avalonia.Interactivity;

namespace InfraLauncher.App;

public partial class SettingsWindow : Window
{
    public SettingsWindow() => InitializeComponent();

    public SettingsWindow(string? simutransExe) : this()
    {
        SimutransExe.Text = simutransExe ?? "";
    }

    public string? Result { get; private set; }

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
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
