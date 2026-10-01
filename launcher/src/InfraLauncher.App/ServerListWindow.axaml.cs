using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using InfraLauncher.Core;
using InfraLauncher.Core.Models;

namespace InfraLauncher.App;

/// <summary>
/// 共有されたサーバーリストの追加・編集。保存の前に実際に読み込んで確かめる。
/// 署名があれば、サーバー管理者から聞いた確認コードを入力してもらい、合っていればその鍵を登録する。
/// 画面には確認コードを出さない（出すと、見比べずに「同じ」を押したり、表示を写したりできてしまう）。
/// </summary>
public partial class ServerListWindow : Window
{
    private readonly ManifestClient? _client;
    private readonly ServerListSource? _existing;
    private string _url = "";
    private Manifest? _loaded;

    public ServerListWindow() => InitializeComponent();

    public ServerListWindow(ManifestClient client, ServerListSource? existing, string? note = null) : this()
    {
        _client = client;
        _existing = existing;
        Title = existing is null ? "共有されたサーバーリストを追加" : "サーバーリストを編集";
        NameBox.Text = existing?.Name ?? "";
        UrlBox.Text = existing?.Url ?? "";
        if (note is not null)
        {
            Note.Text = note;
            Note.IsVisible = true;
        }
    }

    public ServerListSource? Result { get; private set; }

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        if (await Dialogs.PickFileAsync(this, "サーバーリストのファイルを選択", Dialogs.JsonFile) is { } path)
        {
            UrlBox.Text = path;
            Show("手元のファイルを登録すると、paksetなども同じフォルダから探します（動作確認用）。" +
                 "友人と遊ぶときは、サーバー管理者から教えてもらったhttps://…/manifest.jsonを入れてください", error: false);
        }
    }

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        var url = UrlBox.Text?.Trim() ?? "";
        if (url.Length == 0)
        {
            Show("配信アドレスを入れてください", error: true);
            return;
        }

        SaveButton.IsEnabled = false;
        Show("読み込んで確認しています…", error: false);
        try
        {
            // 鍵を指定せずに読む（署名があれば、中身と合うかはここで確かめられる）
            var manifest = await _client!.LoadAsync(LauncherService.ToUri(url));
            _url = url;
            _loaded = manifest;
            if (manifest.Signature is null && _existing?.PublicKey is not null)
            {
                // 確認コードを登録済みのリストから署名が消えた。書き換えのおそれがあるので、登録を外して保存することはしない
                Show("このサーバーリストには署名がありません。確認コードを登録済みのリストから署名が消えるのは、配信しているファイルが書き換えられたおそれがあります。" +
                     "サーバー管理者に確かめてください（どうしても使う場合は、いったん削除してから追加し直してください）", error: true);
                return;
            }
            if (manifest.Signature is not { } sig)
            {
                // 署名のないリスト。paksetは同期できるが、本体は自動で入れない
                Finish(publicKey: null);
                return;
            }
            if (_existing?.PublicKey is { } known && ManifestSignature.SameCode(ManifestSignature.CodeFor(known), sig.Code))
            {
                // 登録済みの鍵と同じなら、入力し直す必要はない
                Finish(known);
                return;
            }
            AskCode();
        }
        catch (Exception ex) when (ex is ManifestException or UriFormatException or ArgumentException)
        {
            Show($"読み込めませんでした: {ex.Message}", error: true);
        }
        finally
        {
            SaveButton.IsEnabled = true;
        }
    }

    private int _failures;

    private void AskCode()
    {
        ResultText.IsVisible = false;
        CodeIntro.Text = _existing?.PublicKey is not null
            ? "このサーバーリストの確認コードが、以前登録したものから変わっています（サーバー管理者が鍵を作り直すと変わります）。" +
              "新しい確認コードをサーバー管理者に聞いて入力してください。"
            : "サーバー管理者から聞いた確認コード（Discordなどで教えてもらったもの）を入力してください。";
        // 登録済みの鍵が変わった場合は「あとで」を出さない（書き換えられたリストを、登録を外して使い続けることにならないように）
        LaterButton.IsVisible = _existing?.PublicKey is null;
        CodePanel.IsVisible = true;
        SaveButtons.IsVisible = false;
        UrlBox.IsEnabled = false;
        CodeBox.Focus();
    }

    private void OnCodeKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key == Avalonia.Input.Key.Enter)
        {
            OnCodeSubmit(sender, e);
        }
    }

    private void OnCodeSubmit(object? sender, RoutedEventArgs e)
    {
        var input = CodeBox.Text?.Trim() ?? "";
        var sig = _loaded!.Signature!;
        if (input.Length > 0 && ManifestSignature.SameCode(input, sig.Code))
        {
            Finish(sig.PublicKey);
            return;
        }
        _failures++;
        // 正しいコードは出さない。何度も違うなら、アドレス違いか書き換えのおそれを伝える
        CodeError.Text = input.Length == 0 ? "確認コードを入力してください"
            : _failures < 3 ? "確認コードが一致しません。入力を見直してください（20文字の英数字です）"
            : "確認コードが一致しません。アドレスが違うか、配信しているファイルが書き換えられているおそれがあります。" +
              "サーバー管理者にアドレスと確認コードを確かめてください";
        CodeError.IsVisible = true;
    }

    /// <summary>まだ聞いていないなら登録せずに保存する（paksetだけ同期できる。あとで「編集」から登録できる）。</summary>
    private void OnCodeLater(object? sender, RoutedEventArgs e) => Finish(publicKey: null);

    private void Finish(string? publicKey)
    {
        var name = string.IsNullOrWhiteSpace(NameBox.Text)
            ? _loaded!.Servers.FirstOrDefault()?.Name ?? "サーバーリスト"
            : NameBox.Text.Trim();
        Result = new ServerListSource { Name = name, Url = _url, PublicKey = publicKey };
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    private void Show(string text, bool error)
    {
        ResultText.Text = text;
        ResultText.Foreground = error ? new SolidColorBrush(Color.Parse("#d13438")) : null;
        ResultText.IsVisible = true;
    }
}
