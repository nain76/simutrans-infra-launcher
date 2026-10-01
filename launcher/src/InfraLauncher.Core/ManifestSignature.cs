using System.Security.Cryptography;
using System.Text.Json;
using InfraLauncher.Core.Models;

namespace InfraLauncher.Core;

/// <summary>サーバーリストの署名を確かめた結果。PublicKeyは公開鍵（SubjectPublicKeyInfoのbase64）、Codeは人が入力して確かめる確認コード。</summary>
public sealed record SignatureInfo(string PublicKey, string Code);

/// <summary>どう信用できなかったか。</summary>
public enum SignatureProblem
{
    /// <summary>署名がサーバーリストの中身と合わない（書き換えられた）。</summary>
    Invalid,
    /// <summary>確認コードを登録したのに署名がない。</summary>
    Missing,
    /// <summary>登録した確認コードと違う鍵で署名されている。</summary>
    KeyChanged,
}

/// <summary>署名の確認で信用できなかったときの例外。KeyChangedならNewCodeに新しい確認コードが入る。</summary>
public sealed class ManifestSignatureException(SignatureProblem problem, string message, string? newCode = null)
    : ManifestException(message)
{
    public SignatureProblem Problem { get; } = problem;
    public string? NewCode { get; } = newCode;
}

/// <summary>
/// サーバーリストの署名。サーバー管理者はVPSにある秘密鍵でmanifest.jsonに署名し、manifest.sig.jsonに置く。
/// ランチャーは、ユーザーが管理者から聞いた「確認コード」（公開鍵のSHA256の先頭）と一致した公開鍵を覚えておき、
/// 毎回その鍵で署名を確かめる。paksetや本体のファイル一覧はmanifest.jsonにSHA256が書かれているので、
/// manifest.jsonの署名を確かめれば、配っているファイルすべてが管理者の公開したものだと分かる。
/// 署名はECDSA P-256 / SHA-256（rとsを並べた64バイト）。
/// </summary>
public static class ManifestSignature
{
    public const string Format = "infra-launcher-signature-1";
    private const string P256Oid = "1.2.840.10045.3.1.7";

    /// <summary>署名ファイルの場所。manifest.jsonならmanifest.sig.json（IISの設定を足さずに配れる拡張子にする）。</summary>
    public static Uri SignatureUriFor(Uri manifest)
    {
        var b = new UriBuilder(manifest) { Query = "", Fragment = "" };
        b.Path = b.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? b.Path[..^".json".Length] + ".sig.json"
            : b.Path + ".sig.json";
        return b.Uri;
    }

    /// <summary>署名ファイルを読み、manifestの中身（バイト列そのもの）に対する署名として正しいか確かめる。</summary>
    public static SignatureInfo Verify(byte[] manifest, string signatureJson)
    {
        SignatureFile? file;
        try
        {
            file = JsonSerializer.Deserialize(signatureJson, Json.Context.SignatureFile);
        }
        catch (JsonException)
        {
            file = null;
        }
        if (file is null || file.Format != Format || file.PublicKey is null || file.Signature is null)
        {
            throw Invalid();
        }
        try
        {
            using var key = ImportKey(file.PublicKey);
            if (!key.VerifyData(manifest, Convert.FromBase64String(file.Signature), HashAlgorithmName.SHA256))
            {
                throw Invalid();
            }
        }
        catch (Exception e) when (e is FormatException or CryptographicException)
        {
            throw Invalid();
        }
        return new SignatureInfo(file.PublicKey, CodeFor(file.PublicKey));
    }

    /// <summary>公開鍵から確認コードを作る。SHA256の先頭10バイトを16進数で4文字ずつ区切ったもの（例: A1B2-C3D4-E5F6-0718-293A）。</summary>
    public static string CodeFor(string publicKey)
    {
        var hash = SHA256.HashData(Convert.FromBase64String(publicKey));
        var hex = Convert.ToHexString(hash, 0, 10);
        return string.Join('-', Enumerable.Range(0, 5).Select(i => hex.Substring(i * 4, 4)));
    }

    /// <summary>人が入力した確認コードを比べる（大文字小文字、区切りの-や空白は問わない）。</summary>
    public static bool SameCode(string a, string b) => Normalize(a) == Normalize(b);

    /// <summary>署名ファイルを作る（テストと動作確認用。実際の署名はサーバー側のPowerShellが作る）。</summary>
    public static string Sign(byte[] manifest, ECDsa key)
    {
        var file = new SignatureFile
        {
            Format = Format,
            PublicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
            Signature = Convert.ToBase64String(key.SignData(manifest, HashAlgorithmName.SHA256)),
        };
        return JsonSerializer.Serialize(file, Json.Context.SignatureFile);
    }

    private static ECDsa ImportKey(string publicKey)
    {
        var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
        if (key.ExportParameters(false).Curve.Oid?.Value != P256Oid)
        {
            key.Dispose();
            throw new CryptographicException("P-256の鍵ではありません");
        }
        return key;
    }

    private static string Normalize(string code) =>
        new(code.Where(char.IsAsciiLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static ManifestSignatureException Invalid() => new(SignatureProblem.Invalid,
        "サーバーリストの署名が中身と合いません。配信しているファイルが書き換えられたおそれがあるため、読み込みを中止しました。サーバー管理者に連絡してください");
}
