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
/// paksetはサーバーと同じでないと接続できないので、選択肢は出さずにすべて落とす。
/// </summary>
public partial class InstallOptionsWindow : Window
{
    private readonly List<ComponentChoice> _choices = new();
    private readonly PaksetIndex? _engineIndex;
    private readonly long? _paksetBytes;
    private readonly string _paksetName = "";
    private readonly string _defaultRoot = "";

    public InstallOptionsWindow() => InitializeComponent();

    /// <param name="paksetBytes">pakset全体のサイズ（ファイル一覧方式のとき。zip方式ならnull）。</param>
    public InstallOptionsWindow(string serverName, string defaultRoot, InstallOptions? current, PaksetIndex engineIndex, string paksetName, long? paksetBytes) : this()
    {
        _engineIndex = engineIndex;
        _paksetBytes = paksetBytes;
        _defaultRoot = defaultRoot;
        Heading.Text = $"「{serverName}」のインストール設定";
        RootBox.Watermark = $"既定: {defaultRoot}";
        AdviceText.Text = SyncFolders.Advice;
        RootBox.Text = current?.InstallRoot ?? "";
        _paksetName = paksetName;
        PaksetNote.Text = "paksetはサーバーと完全に同じでないと接続できないため、すべてダウンロードします。手元にすでにあるファイルは落としません。";

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
        TotalText.Text = _paksetBytes is { } pakset ? ComponentChoice.Format(total + pakset) : $"{ComponentChoice.Format(total)} ＋pakset";
        PaksetSizeText.Text = _paksetBytes is { } p ? $"{ComponentChoice.Format(p)}（{_paksetName}）" : $"サイズ不明（{_paksetName}・zipでまとめて配布）";
        EngineSizeText.Text = $"{ComponentChoice.Format(total)}（上で選んだ部品）";
    }

    private void OnRootChanged(object? sender, TextChangedEventArgs e)
    {
        var root = RootBox.Text?.Trim();
        var effective = string.IsNullOrEmpty(root) ? _defaultRoot : root;
        if (OneDriveWarning is null) return;
        var service = SyncFolders.Detect(effective);
        OneDriveWarning.IsVisible = service is not null;
        OneDriveWarningText.Text = service is null ? "" : SyncFolders.Warning(service);
    }

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        if (await Dialogs.PickFolderAsync(this, "ダウンロード先のフォルダを選択", RootBox.Text) is { } path)
        {
            RootBox.Text = path;
        }
    }

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        var root = RootBox.Text?.Trim().Trim('"');
        if (!string.IsNullOrEmpty(root) && !Path.IsPathRooted(root))
        {
            ErrorText.Text = "ダウンロード先はフルパスで入れてください（例: C:\\Games\\simutrans）";
            ErrorText.IsVisible = true;
            return;
        }
        if (SyncFolders.Detect(string.IsNullOrEmpty(root) ? _defaultRoot : root) is { } service
            && !await Dialogs.ConfirmAsync(this, "ダウンロード先の確認", SyncFolders.Warning(service) + "\n\nこのまま保存しますか？", "このまま保存"))
        {
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
