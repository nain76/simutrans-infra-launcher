using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using InfraLauncher.Core;
using InfraLauncher.Core.Models;

namespace InfraLauncher.App;

public enum ServerRowKind
{
    /// <summary>共有されたサーバーリストにあるサーバー。</summary>
    Listed,
    /// <summary>手動で設定したプロファイル。</summary>
    Manual,
    /// <summary>読み込めなかったサーバーリスト（編集・削除できるように行として出す）。</summary>
    ListError,
}

/// <summary>
/// サーバー一覧の1行。ひと目で分かるよう、サーバーの稼働状況と同期の状態は色付きの札（バッジ）で出し、
/// 細かい情報は下の行に小さく出す。
/// </summary>
public sealed class ServerRow : INotifyPropertyChanged
{
    // 札の色。明るい画面でも暗い画面でも白い文字が読める濃さにする
    private static readonly IBrush Green = new SolidColorBrush(Color.Parse("#1f7a3a"));
    private static readonly IBrush Orange = new SolidColorBrush(Color.Parse("#b35c00"));
    private static readonly IBrush Red = new SolidColorBrush(Color.Parse("#b3261e"));
    private static readonly IBrush Gray = new SolidColorBrush(Color.Parse("#5f6368"));
    private static readonly IBrush Blue = new SolidColorBrush(Color.Parse("#1a5fb4"));

    private bool _isFavorite;
    private string _syncLabel = "";
    private IBrush _syncBrush = Gray;
    private string _syncDetail = "";

    private ServerRow(ServerRowKind kind, string favoriteKey)
    {
        Kind = kind;
        FavoriteKey = favoriteKey;
    }

    public static ServerRow Listed(ServerListSource list, ServerEntry server, SyncPlan? plan, string? planError)
    {
        var (label, brush) = server.Status switch
        {
            "online" => ("● 稼働中", Green),
            "offline" => ("停止中", Gray),
            "maintenance" => ("メンテナンス中", Orange),
            _ => ("状態不明", Gray),
        };
        var engine = server.Engine is { } e ? $"本体: {e.Revision}" : "本体: 手元のものを使う";
        var row = new ServerRow(ServerRowKind.Listed, FavoriteKeys.ForListed(list, server.Id))
        {
            List = list,
            Server = server,
            Name = server.Name,
            AddressText = server.Address,
            StatusText = label,
            StatusBrush = brush,
            PlayersText = server.Players is { } n ? $"{n}人" : "",
            Message = server.Message ?? "",
            DetailText = string.Join("　｜　", $"pakset: {server.Pakset.DisplayName}", engine,
                $"リスト: {list.Name}" + (list.PublicKey is null ? "（確認コード未入力）" : "（確認コード入力済み）")),
        };
        row.SetPlan(plan, planError);
        return row;
    }

    public static ServerRow Manual(ManualProfile profile) => new(ServerRowKind.Manual, FavoriteKeys.ForManual(profile))
    {
        Profile = profile,
        Name = profile.Name,
        AddressText = profile.Address,
        DetailText = $"pakset: {profile.PaksetFolder}　｜　手動プロファイル",
        _syncLabel = "手動",
        _syncBrush = Blue,
        _syncDetail = "自動同期はしません。そのまま起動できます",
    };

    public static ServerRow Error(ServerListSource list, string error) => new(ServerRowKind.ListError, "")
    {
        List = list,
        Name = list.Name,
        StatusText = "読み込めませんでした",
        StatusBrush = Red,
        DetailText = "サーバーリスト（「編集」でアドレスを確かめるか、「削除」で消せます）",
        _syncLabel = "エラー",
        _syncBrush = Red,
        _syncDetail = error,
    };

    public ServerRowKind Kind { get; }
    public string FavoriteKey { get; }
    public ServerListSource? List { get; private init; }
    public ServerEntry? Server { get; private init; }
    public ManualProfile? Profile { get; private init; }

    public string Name { get; private init; } = "";
    public string AddressText { get; private init; } = "";
    public string StatusText { get; private init; } = "";
    public IBrush StatusBrush { get; private init; } = Gray;
    public bool HasStatus => StatusText.Length > 0;
    public string PlayersText { get; private init; } = "";
    public bool HasPlayers => PlayersText.Length > 0;
    public string Message { get; private init; } = "";
    public bool HasMessage => Message.Length > 0;
    /// <summary>pakset・本体・リストなどの細かい情報（1行にまとめて小さく出す）。</summary>
    public string DetailText { get; private init; } = "";
    public bool CanFavorite => Kind != ServerRowKind.ListError;
    /// <summary>「同期」できるか（共有リストのサーバーだけ）。</summary>
    public bool CanSync => Kind == ServerRowKind.Listed && _planError is null;
    /// <summary>「起動」できるか。共有リストのサーバーは同期が済んでいること。</summary>
    public bool IsReady => Kind == ServerRowKind.Manual || Kind == ServerRowKind.Listed && Plan is { UpToDate: true };
    public SyncPlan? Plan { get; private set; }
    private string? _planError;
    /// <summary>読み込めなかった行は☆を見えなくするが、場所は残して左を揃える。</summary>
    public double FavoriteOpacity => CanFavorite ? 1 : 0;

    public bool IsFavorite
    {
        get => _isFavorite;
        set { _isFavorite = value; OnPropertyChanged(); OnPropertyChanged(nameof(FavoriteGlyph)); }
    }

    public string FavoriteGlyph => _isFavorite ? "★" : "☆";

    /// <summary>同期の状態の札（「起動できます」「要同期」など）。</summary>
    public string SyncLabel
    {
        get => _syncLabel;
        private set { _syncLabel = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasSyncLabel)); }
    }

    public bool HasSyncLabel => _syncLabel.Length > 0;

    public IBrush SyncBrush
    {
        get => _syncBrush;
        private set { _syncBrush = value; OnPropertyChanged(); }
    }

    /// <summary>札の横に出す説明（何を同期するか、なぜ同期できないか）。</summary>
    public string SyncDetail
    {
        get => _syncDetail;
        private set { _syncDetail = value; OnPropertyChanged(); }
    }

    public void SetPlan(SyncPlan? plan, string? error)
    {
        Plan = plan;
        _planError = error;
        if (error is not null)
        {
            (SyncLabel, SyncBrush, SyncDetail) = ("同期できません", Red, error);
        }
        else if (plan is null)
        {
            (SyncLabel, SyncBrush, SyncDetail) = ("", Gray, "");
        }
        else if (plan.UpToDate)
        {
            (SyncLabel, SyncBrush, SyncDetail) = ("✔ 起動できます", Green, "本体と pakset はサーバーと同じです");
        }
        else
        {
            var needed = string.Join("、", plan.Items.Where(i => i.Needed).Select(i => i.Kind == SyncItemKind.Engine ? "simutrans 本体" : "pakset"));
            (SyncLabel, SyncBrush, SyncDetail) = ("要同期", Orange, $"{needed} を更新します。「同期」を押してください");
        }
        OnPropertyChanged(nameof(IsReady));
        OnPropertyChanged(nameof(CanSync));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
