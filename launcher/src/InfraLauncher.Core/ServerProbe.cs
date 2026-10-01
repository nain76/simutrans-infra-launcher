using System.Net.Sockets;

namespace InfraLauncher.Core;

/// <summary>
/// simutransサーバーが動いているかを、そのポートにつながるかで確かめる。
///サーバーリストのstatusは管理者が書いた値なので、実際に動いているかはこちらで確かめる。
///つないですぐ切るだけで、ゲームのデータは送らない。
/// </summary>
public static class ServerProbe
{
    public static async Task<bool> IsReachableAsync(ServerAddress address, TimeSpan timeout, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(address.Host.Trim('[', ']'), address.Port, cts.Token);
            return true;
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return false;
        }
    }
}
