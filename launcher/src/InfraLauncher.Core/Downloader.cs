using System.Security.Cryptography;

namespace InfraLauncher.Core;

/// <summary>ダウンロードとハッシュ計算。http(s):// と file:// に対応。</summary>
internal static class Downloader
{
    /// <summary>
    /// <paramref name="url"/> を <paramref name="dest"/> に保存し、SHA256 が <paramref name="expectedSha256"/> と一致するか確かめる。
    /// 一致しなければファイルを消して <see cref="SyncException"/> を投げる。
    /// </summary>
    public static async Task DownloadAsync(HttpClient http, string url, string dest, string expectedSha256, string what,
        Action<long>? onBytes, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            var uri = new Uri(url);
            HttpResponseMessage? response = null;
            Stream source;
            if (uri.IsFile)
            {
                source = File.OpenRead(uri.LocalPath);
            }
            else
            {
                response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();
                source = await response.Content.ReadAsStreamAsync(ct);
            }

            using (response)
            await using (source)
            await using (var file = File.Create(dest))
            {
                var buffer = new byte[81920];
                int n;
                while ((n = await source.ReadAsync(buffer, ct)) > 0)
                {
                    hash.AppendData(buffer, 0, n);
                    await file.WriteAsync(buffer.AsMemory(0, n), ct);
                    onBytes?.Invoke(n);
                }
            }
        }
        catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            TryDelete(dest);
            throw new SyncException($"{what} をダウンロードできませんでした: {url} ({e.Message})", e);
        }
        catch
        {
            TryDelete(dest);
            throw;
        }

        var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
        if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(dest);
            throw new SyncException(
                $"{what} のハッシュがサーバーリストの記載と一致しません。ダウンロードが壊れているか、サーバーリストが古い可能性があります。サーバー管理者に確認してください。" +
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
