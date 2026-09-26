using System.ComponentModel;
using System.Runtime.CompilerServices;
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

/// <summary>サーバー一覧の1行。</summary>
public sealed class ServerRow : INotifyPropertyChanged
{
    private string _syncText = "";
    private bool _isFavorite;

    private ServerRow(ServerRowKind kind, string favoriteKey)
    {
        Kind = kind;
        FavoriteKey = favoriteKey;
    }

    public static ServerRow Listed(ServerListSource list, ServerEntry server, SyncPlan? plan, string? planError)
    {
        var row = new ServerRow(ServerRowKind.Listed, FavoriteKeys.ForListed(list, server.Id))
        {
            List = list,
            Server = server,
            Name = server.Name,
            AddressText = server.Address,
            StatusText = server.Status switch
            {
                "online" => "● 稼働中",
                "offline" => "○ 停止中",
                "maintenance" => "▲ メンテナンス中",
                _ => "? 状態不明",
            },
            PlayersText = server.Players is { } n ? $"{n}人" : "",
            Message = server.Message ?? "",
            PaksetText = server.Pakset.Version is { Length: > 0 } v ? $"{server.Pakset.Name} {v}" : server.Pakset.Name,
            SourceText = $"リスト: {list.Name}",
        };
        row.SetPlan(plan, planError);
        return row;
    }

    public static ServerRow Manual(ManualProfile profile) => new(ServerRowKind.Manual, FavoriteKeys.ForManual(profile))
    {
        Profile = profile,
        Name = profile.Name,
        AddressText = profile.Address,
        StatusText = "",
        PlayersText = "",
        Message = "",
        PaksetText = profile.PaksetFolder,
        SourceText = "手動プロファイル",
        _syncText = "自動同期なし",
    };

    public static ServerRow Error(ServerListSource list, string error) => new(ServerRowKind.ListError, "")
    {
        List = list,
        Name = list.Name,
        AddressText = "",
        StatusText = "⚠ 読み込めませんでした",
        PlayersText = "",
        Message = error,
        PaksetText = "",
        SourceText = "サーバーリスト",
    };

    public ServerRowKind Kind { get; }
    public string FavoriteKey { get; }
    public ServerListSource? List { get; private init; }
    public ServerEntry? Server { get; private init; }
    public ManualProfile? Profile { get; private init; }

    public string Name { get; private init; } = "";
    public string AddressText { get; private init; } = "";
    public string StatusText { get; private init; } = "";
    public string PlayersText { get; private init; } = "";
    public string Message { get; private init; } = "";
    public bool HasMessage => Message.Length > 0;
    public string PaksetText { get; private init; } = "";
    public string SourceText { get; private init; } = "";
    public bool CanFavorite => Kind != ServerRowKind.ListError;
    /// <summary>読み込めなかった行は☆を見えなくするが、場所は残して左を揃える。</summary>
    public double FavoriteOpacity => CanFavorite ? 1 : 0;
    public bool CanConnect => Kind != ServerRowKind.ListError;

    public bool IsFavorite
    {
        get => _isFavorite;
        set { _isFavorite = value; OnPropertyChanged(); OnPropertyChanged(nameof(FavoriteGlyph)); }
    }

    public string FavoriteGlyph => _isFavorite ? "★" : "☆";

    public string SyncText
    {
        get => _syncText;
        private set { _syncText = value; OnPropertyChanged(); }
    }

    public void SetPlan(SyncPlan? plan, string? error)
    {
        SyncText = error is not null ? $"同期できません: {error}"
            : plan is null ? ""
            : plan.UpToDate ? "✔ 最新"
            : "↓ 要同期: " + string.Join("、", plan.Items.Where(i => i.Needed).Select(i => i.Label));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
