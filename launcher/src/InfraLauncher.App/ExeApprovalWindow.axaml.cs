using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using InfraLauncher.Core;

namespace InfraLauncher.App;

/// <summary>ランチャーが配布元から入れた本体を、初めて（または中身が変わって）実行するときの確認。</summary>
public partial class ExeApprovalWindow : Window
{
    private readonly string _folder = "";

    public ExeApprovalWindow() => InitializeComponent();

    public ExeApprovalWindow(LaunchInfo info, string serverName, string? listName) : this()
    {
        Heading.Text = $"サーバー「{serverName}」のsimutrans本体を実行しますか？";
        EngineText.Text = info.EngineLabel ?? "";
        SourceText.Text = listName is null ? info.SourceUrl ?? "" : $"{listName}（{info.SourceUrl}）";
        PathText.Text = info.ExePath;
        ShaText.Text = info.ExeSha256;
        _folder = Path.GetDirectoryName(info.ExePath) ?? "";
    }

    private void OnOpenFolder(object? sender, RoutedEventArgs e)
    {
        if (Directory.Exists(_folder))
        {
            Process.Start(new ProcessStartInfo(_folder) { UseShellExecute = true });
        }
    }

    private void OnApprove(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
