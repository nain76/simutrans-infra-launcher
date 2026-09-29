using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using InfraLauncher.Core;
using InfraLauncher.Core.Models;

namespace InfraLauncher.App;

/// <summary>部品の選択肢の1行。</summary>
public sealed class ComponentChoice(IndexComponent component, long size, Action changed) : INotifyPropertyChanged
{
    private bool _isChecked;
    private bool _custom;

    public IndexComponent Component { get; } = component;
    public long Size { get; } = size;

    public string Label =>
        $"{Component.Name}（{Format(Size)}）" + (Component.Required ? "　必須" : Component.Recommended ? "　推奨" : "");

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return;
            _isChecked = value;
            OnPropertyChanged();
            changed();
        }
    }

    public bool IsEditable => _custom && !Component.Required;

    /// <summary>推奨なら推奨どおりに固定し、カスタムなら必須以外を選べるようにする。</summary>
    public void SetMode(bool custom)
    {
        _custom = custom;
        if (!custom || Component.Required)
        {
            IsChecked = Component.Required || Component.Recommended;
        }
        OnPropertyChanged(nameof(IsEditable));
    }

    public static string Format(long bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / 1024.0 / 1024.0:0.#} MB" : $"{Math.Max(1, bytes / 1024)} KB";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// サーバーごとのインストール設定。ダウンロード先と、本体の部品を「推奨」で落とすか「カスタム」で選ぶかを決める。
/// pakset はサーバーと同じでないと接続できないので、選択肢は出さずにすべて落とす。
/// </summary>
public partial class InstallOptionsWindow : Window
{
    private readonly List<ComponentChoice> _choices = new();
    private readonly PaksetIndex? _engineIndex;

    public InstallOptionsWindow() => InitializeComponent();

    public InstallOptionsWindow(string serverName, string defaultRoot, InstallOptions? current, PaksetIndex engineIndex, string paksetName) : this()
    {
        _engineIndex = engineIndex;
        Heading.Text = $"「{serverName}」のインストール設定";
        RootBox.Watermark = $"既定: {defaultRoot}";
        RootBox.Text = current?.InstallRoot ?? "";
        PaksetNote.Text = $"pakset（{paksetName}）は、サーバーと完全に同じでないと接続できないため、すべてダウンロードします。";

        var sizes = ComponentSelection.SizeByComponent(engineIndex);
        foreach (var c in engineIndex.Components ?? new())
        {
            var choice = new ComponentChoice(c, sizes.GetValueOrDefault(c.Id), UpdateTotal);
            _choices.Add(choice);
        }
        ComponentList.ItemsSource = _choices;

        var custom = current?.IsCustom == true;
        foreach (var choice in _choices)
        {
            choice.SetMode(custom);
            if (custom && !choice.Component.Required)
            {
                choice.IsChecked = current!.Components!.Contains(choice.Component.Id);
            }
        }
        (custom ? CustomRadio : RecommendedRadio).IsChecked = true;
        if (_choices.Count == 0)
        {
            CustomRadio.IsEnabled = false;
            RecommendedRadio.Content = "すべて（このサーバーは部品を分けていません）";
        }
        UpdateTotal();
    }

    public InstallOptions? Result { get; private set; }

    private void OnModeChanged(object? sender, RoutedEventArgs e)
    {
        var custom = CustomRadio.IsChecked == true;
        foreach (var choice in _choices)
        {
            choice.SetMode(custom);
        }
        UpdateTotal();
    }

    private void UpdateTotal()
    {
        if (_engineIndex is null || TotalText is null)
        {
            return;
        }
        var selected = CustomRadio.IsChecked == true ? _choices.Where(c => c.IsChecked).Select(c => c.Component.Id).ToList() : null;
        var total = ComponentSelection.SelectFiles(_engineIndex, selected).Sum(f => f.Size);
        TotalText.Text = $"本体のダウンロード量: {ComponentChoice.Format(total)}（手元にあるファイルは落としません）";
    }

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        if (await Dialogs.PickFolderAsync(this, "ダウンロード先のフォルダを選択", RootBox.Text) is { } path)
        {
            RootBox.Text = path;
        }
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        var root = RootBox.Text?.Trim().Trim('"');
        if (!string.IsNullOrEmpty(root) && !Path.IsPathRooted(root))
        {
            ErrorText.Text = "ダウンロード先はフルパスで入れてください（例: D:\\Games\\simutrans）";
            ErrorText.IsVisible = true;
            return;
        }
        Result = new InstallOptions
        {
            InstallRoot = string.IsNullOrEmpty(root) ? null : root,
            Components = CustomRadio.IsChecked == true
                ? _choices.Where(c => c.IsChecked && !c.Component.Required).Select(c => c.Component.Id).ToList()
                : null,
        };
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
