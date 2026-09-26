using Avalonia.Controls;
using Avalonia.Interactivity;

namespace InfraLauncher.App;

public enum AddKind { None, List, Manual }

public partial class AddKindWindow : Window
{
    public AddKindWindow() => InitializeComponent();

    private void OnList(object? sender, RoutedEventArgs e) => Close(AddKind.List);
    private void OnManual(object? sender, RoutedEventArgs e) => Close(AddKind.Manual);
    private void OnCancel(object? sender, RoutedEventArgs e) => Close(AddKind.None);
}
