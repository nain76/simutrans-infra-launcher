using System.Diagnostics.CodeAnalysis;

namespace InfraLauncher.Core;

/// <summary>接続先。"host"、"host:port"、"[IPv6]:port" を受け付ける。</summary>
public readonly record struct ServerAddress(string Host, int Port)
{
    /// <summary>Simutransの標準ポート。</summary>
    public const int DefaultPort = 13353;

    public static ServerAddress Parse(string text) =>
        TryParse(text, out var a) ? a : throw new FormatException($"接続先の形式が正しくありません: {text}");

    public static bool TryParse(string? text, [NotNullWhen(true)] out ServerAddress result)
    {
        result = default;
        text = text?.Trim();
        if (string.IsNullOrEmpty(text) || text.Any(char.IsWhiteSpace))
        {
            return false;
        }

        string host;
        string? port = null;
        if (text.StartsWith('['))
        {
            var close = text.IndexOf(']');
            if (close < 2)
            {
                return false;
            }
            host = text[..(close + 1)];
            var rest = text[(close + 1)..];
            if (rest.Length > 0)
            {
                if (rest[0] != ':')
                {
                    return false;
                }
                port = rest[1..];
            }
        }
        else
        {
            var colon = text.LastIndexOf(':');
            if (colon >= 0 && text.IndexOf(':') != colon)
            {
                // 角かっこなしのIPv6はポートと区別できないので受け付けない
                return false;
            }
            host = colon >= 0 ? text[..colon] : text;
            port = colon >= 0 ? text[(colon + 1)..] : null;
        }

        if (host.Length == 0)
        {
            return false;
        }
        var p = DefaultPort;
        if (port is not null && (!int.TryParse(port, out p) || p is < 1 or > 65535))
        {
            return false;
        }
        result = new ServerAddress(host, p);
        return true;
    }

    public override string ToString() => $"{Host}:{Port}";
}
