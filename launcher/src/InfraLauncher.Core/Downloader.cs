using System.Security.Cryptography;

namespace InfraLauncher.Core;

/// <summary>通信が途切れた・止まったなど、やり直せば成功するかもしれない失敗。</summary>
internal sealed class DownloadInterruptedException(string message, Exception inner) : SyncException(message, inner);

/// <summary>ダウンロードとハッシュ計算。http(s)://とfile://に対応。</summary>
internal static class Downloader
{
    /// <summary>
    ///この時間データが1バイトも届かなければ、通信が止まったとみなして打ち切る。
    ///（HttpClientのTimeoutは応答の頭までしか見ないので、途中で通信が途切れたまま待ち続けることがあるため）
    /// </summary>
    internal static TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// <paramref name="url"/>を<paramref name="dest"/>に保存し、SHA256が<paramref name="expectedSha256"/>と一致するか確かめる。
    ///一致しなければファイルを消して<see cref="SyncException"/>を投げる。
    /// </summary>
    public static async Task DownloadAsync(HttpClient http, string url, string dest, string expectedSha256, string what,
        Action<long>? onBytes, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idle.CancelAfter(IdleTimeout);
        try
        {
            var uri = new Uri(url);
            HttpResponseMessage? response = null;
            Stream source;
            if (uri.IsFile)
            {
                if (!File.Exists(uri.LocalPath))
                {
                    throw new SyncException(
                        $"{what}が手元に見つかりません: {uri.LocalPath}。サーバーリストを手元のファイルとして登録していると、" +
                        "paksetなども同じフォルダから探します。「編集」で配信アドレスを、サーバー管理者から教えてもらったhttp(s)://…/manifest.jsonに変えてください");
                }
                source = File.OpenRead(uri.LocalPath);
            }
            else
            {
                SyncLog.Write($"要求: {url}");
                response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, idle.Token);
                SyncLog.Write($"応答: {url} {(int)response.StatusCode}長さ{response.Content.Headers.ContentLength?.ToString() ?? "不明"}");
                response.EnsureSuccessStatusCode();
                source = await response.Content.ReadAsStreamAsync(idle.Token);
            }

            var received = 0L;
            var nextLog = 64L * 1024 * 1024;
            var started = DateTime.Now;
            using (response)
            await using (source)
            await using (var file = File.Create(dest))
            {
                var buffer = new byte[81920];
                int n;
                while ((n = await source.ReadAsync(buffer, idle.Token)) > 0)
                {
                    idle.CancelAfter(IdleTimeout);
                    received += n;
                    if (received >= nextLog)
                    {
                        SyncLog.Write($"受信中: {url} {received:N0}バイト");
                        nextLog += 64L * 1024 * 1024;
                    }
                    hash.AppendData(buffer, 0, n);
                    await file.WriteAsync(buffer.AsMemory(0, n), ct);
                    onBytes?.Invoke(n);
                }
            }
            if (response is not null)
            {
                SyncLog.Write($"受信完了: {url} {received:N0}バイト（{(DateTime.Now - started).TotalSeconds:0.0}秒）");
            }
        }
        catch (OperationCanceledException e) when (!ct.IsCancellationRequested)
        {
            TryDelete(dest);
            SyncLog.Write($"止まったので打ち切り: {url}");
            throw new DownloadInterruptedException($"{what}のダウンロードが{IdleTimeout.TotalSeconds:0}秒間止まったので打ち切りました: {url}", e);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException && !ct.IsCancellationRequested)
        {
            TryDelete(dest);
            var message = $"{what}をダウンロードできませんでした: {url} ({e.Message})";
            SyncLog.Write($"失敗: {message}");
            // 404などサーバーがはっきり断った場合は、やり直しても同じなのでやり直さない
            var interrupted = e is IOException || e is HttpRequestException { StatusCode: null or >= System.Net.HttpStatusCode.InternalServerError };
            throw interrupted ? new DownloadInterruptedException(message, e) : new SyncException(message, e);
        }
        catch
        {
            TryDelete(dest);
            throw;
        }

        var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
        if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            SyncLog.Write($"ハッシュ不一致: {url}期待{expectedSha256}実際{actual}");
            TryDelete(dest);
            throw new SyncException(
                $"{what}のハッシュがサーバーリストの記載と一致しません。ダウンロードが壊れているか、サーバーリストが古い可能性があります。サーバー管理者に確認してください。" +
                $"（期待: {expectedSha256.ToLowerInvariant()}、実際: {actual}）");
        }
    }

    public static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
    }

    public static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
