using Avalonia.Controls;
using Avalonia.Interactivity;
using InfraLauncher.Core;

namespace InfraLauncher.App;

/// <summary>手動プロファイルの追加・編集。</summary>
public partial class ProfileWindow : Window
{
    private readonly string _id = Guid.NewGuid().ToString("N");

    public ProfileWindow() => InitializeComponent();

    public ProfileWindow(ManualProfile? existing, string? defaultExe) : this()
    {
        Title = existing is null ? "プロファイルを手動で設定" : "プロファイルを編集";
        if (existing is not null)
        {
            _id = existing.Id;
        }
        NameBox.Text = existing?.Name ?? "";
        AddressBox.Text = existing?.Address ?? "";
        ExeBox.Text = existing?.SimutransExe ?? defaultExe ?? "";
        PakBox.Text = existing?.PaksetFolder ?? "";
    }

    public ManualProfile? Result { get; private set; }

    private async void OnBrowseExe(object? sender, RoutedEventArgs e)
    {
        if (await Dialogs.PickFileAsync(this, "simutrans の実行ファイルを選択", Dialogs.SimutransExe) is { } path)
        {
            ExeBox.Text = path;
        }
    }

    private async void OnBrowsePak(object? sender, RoutedEventArgs e)
    {
        var exe = ExeBox.Text?.Trim();
        var start = string.IsNullOrEmpty(exe) ? null : SimutransPaths.DataDirFor(exe);
        if (await Dialogs.PickFolderAsync(this, "pakset のフォルダを選択", start) is not { } path)
        {
            return;
        }
        path = Path.TrimEndingDirectorySeparator(path);
        if (start is not null && !string.Equals(Path.GetDirectoryName(path), Path.TrimEndingDirectorySeparator(start), StringComparison.OrdinalIgnoreCase))
        {
            ShowError("pakset のフォルダは simutrans 本体と同じフォルダの中にあるものを選んでください");
            return;
        }
        ErrorText.IsVisible = false;
        PakBox.Text = Path.GetFileName(path);
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        var name = NameBox.Text?.Trim() ?? "";
        var address = AddressBox.Text?.Trim() ?? "";
        var exe = ExeBox.Text?.Trim() ?? "";
        var pak = PakBox.Text?.Trim().TrimEnd('/', '\\') ?? "";

        string? error =
            name.Length == 0 ? "表示名を入れてください" :
            !ServerAddress.TryParse(address, out _) ? "接続先のアドレスの形式が正しくありません（例: example.ddns.net:13353）" :
            exe.Length == 0 ? "simutrans 本体を指定してください" :
            pak.Length == 0 ? "pakset のフォルダを指定してください" :
            null;
        if (error is not null)
        {
            ShowError(error);
            return;
        }

        Result = new ManualProfile { Id = _id, Name = name, Address = address, SimutransExe = exe, PaksetFolder = pak };
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    private void ShowError(string text)
    {
        ErrorText.Text = text;
        ErrorText.IsVisible = true;
    }
}
