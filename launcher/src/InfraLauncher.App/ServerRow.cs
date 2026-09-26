using System.ComponentModel;
using System.Runtime.CompilerServices;
using InfraLauncher.Core;
using InfraLauncher.Core.Models;

namespace InfraLauncher.App;

/// <summary>サーバー一覧の1行。マニフェストのサーバーか、手入力のお気に入りのどちらか。</summary>
public sealed class ServerRow : INotifyPropertyChanged
{
    private string _syncText = "";

    public ServerRow(ServerEntry server, SyncPlan? plan, string? planError)
    {
        Server = server;
        Name = server.Name;
        AddressText = server.Address;
        StatusText = server.Status switch
        {
            "online" => "● 稼働中",
            "offline" => "○ 停止中",
            "maintenance" => "▲ メンテナンス中",
            _ => "? 不明",
        };
        PlayersText = server.Players is { } n ? $"{n}人" : "-";
        Message = server.Message ?? "";
        PaksetText = server.Pakset.Version is { Length: > 0 } v ? $"{server.Pakset.Name} {v}" : server.Pakset.Name;
        SetPlan(plan, planError);
    }

    public ServerRow(FavoriteServer favorite)
    {
        Favorite = favorite;
        Name = $"★ {favorite.Name}";
        AddressText = favorite.Address;
        StatusText = "お気に入り";
        PlayersText = "-";
        Message = "";
        PaksetText = favorite.PaksetFolder;
        _syncText = "同期なし（手元の simutrans で接続）";
    }

    public ServerEntry? Server { get; }
    public FavoriteServer? Favorite { get; }

    public string Name { get; }
    public string AddressText { get; }
    public string StatusText { get; }
    public string PlayersText { get; }
    public string Message { get; }
    public bool HasMessage => Message.Length > 0;
    public string PaksetText { get; }

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
