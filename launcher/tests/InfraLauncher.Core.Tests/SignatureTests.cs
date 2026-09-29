using System.Net;
using System.Security.Cryptography;
using System.Text;
using InfraLauncher.Core;

namespace InfraLauncher.Core.Tests;

public sealed class SignatureTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "infra-sig-" + Guid.NewGuid().ToString("N"));
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    private static readonly byte[] ManifestBytes = Encoding.UTF8.GetBytes("""
        { "schema_version": 1, "servers": [ { "id": "s", "name": "鯖", "address": "h",
          "engine": { "revision": "r1", "builds": { "windows-x64": { "url": "engine/a.zip", "sha256": "{{A}}", "exe": "simutrans.exe" } } },
          "pakset": { "name": "p", "folder": "pak", "index_url": "pak/index.json", "index_sha256": "{{B}}" } } ] }
        """.Replace("{{A}}", new string('a', 64)).Replace("{{B}}", new string('b', 64)));

    public SignatureTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _key.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private string PublicKey => Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo());

    private Uri Write(byte[] manifest, string? signature)
    {
        var path = Path.Combine(_dir, "manifest.json");
        File.WriteAllBytes(path, manifest);
        var sig = Path.Combine(_dir, "manifest.sig.json");
        if (signature is null)
        {
            File.Delete(sig);
        }
        else
        {
            File.WriteAllText(sig, signature);
        }
        return new Uri(path);
    }

    private static ManifestClient Client(HttpMessageHandler? handler = null) => new(new HttpClient(handler ?? new HttpClientHandler()));

    [Fact]
    public void VerifiesAndDetectsTampering()
    {
        var sig = ManifestSignature.Sign(ManifestBytes, _key);
        var info = ManifestSignature.Verify(ManifestBytes, sig);
        Assert.Equal(PublicKey, info.PublicKey);
        Assert.Matches("^[0-9A-F]{4}(-[0-9A-F]{4}){4}$", info.Code);

        var tampered = (byte[])ManifestBytes.Clone();
        tampered[^3] ^= 1;
        var e = Assert.Throws<ManifestSignatureException>(() => ManifestSignature.Verify(tampered, sig));
        Assert.Equal(SignatureProblem.Invalid, e.Problem);
        Assert.Throws<ManifestSignatureException>(() => ManifestSignature.Verify(ManifestBytes, "{ not json"));
        Assert.Throws<ManifestSignatureException>(() => ManifestSignature.Verify(ManifestBytes, sig.Replace(ManifestSignature.Format, "other")));
    }

    [Fact]
    public void RejectsOtherCurves()
    {
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        Assert.Throws<ManifestSignatureException>(() => ManifestSignature.Verify(ManifestBytes, ManifestSignature.Sign(ManifestBytes, p384)));
    }

    [Fact]
    public void ComparesCodesLoosely()
    {
        Assert.True(ManifestSignature.SameCode("A1B2-C3D4-E5F6-0718-293A", "a1b2 c3d4 e5f6 0718 293a"));
        Assert.False(ManifestSignature.SameCode("A1B2-C3D4-E5F6-0718-293A", "A1B2-C3D4-E5F6-0718-293B"));
    }

    [Theory]
    [InlineData("https://h:8443/manifest.json", "https://h:8443/manifest.sig.json")]
    [InlineData("https://h/list?x=1", "https://h/list.sig.json")]
    [InlineData("file:///C:/lists/servers.JSON", "file:///C:/lists/servers.sig.json")]
    public void FindsSignatureNextToList(string list, string expected) =>
        Assert.Equal(expected, ManifestSignature.SignatureUriFor(new Uri(list)).AbsoluteUri);

    [Fact]
    public async Task UnpinnedListsNeverInstallEngines()
    {
        var unsigned = await Client().LoadAsync(Write(ManifestBytes, null));
        Assert.Null(unsigned.Signature);
        Assert.False(unsigned.Trusted);
        Assert.False(unsigned.Servers[0].EngineDownloadAllowed);

        var signed = await Client().LoadAsync(Write(ManifestBytes, ManifestSignature.Sign(ManifestBytes, _key)));
        Assert.Equal(ManifestSignature.CodeFor(PublicKey), signed.Signature!.Code);
        Assert.False(signed.Servers[0].EngineDownloadAllowed);
    }

    [Fact]
    public async Task PinnedKeyAllowsEngines()
    {
        var m = await Client().LoadAsync(Write(ManifestBytes, ManifestSignature.Sign(ManifestBytes, _key)), PublicKey);
        Assert.True(m.Trusted);
        Assert.True(m.Servers[0].EngineDownloadAllowed);
    }

    [Fact]
    public async Task AcceptsBomBeforeJson()
    {
        var withBom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(ManifestBytes).ToArray();
        var m = await Client().LoadAsync(Write(withBom, ManifestSignature.Sign(withBom, _key)), PublicKey);
        Assert.Single(m.Servers);
    }

    [Fact]
    public async Task RefusesWhenPinnedSignatureIsMissingOrChanged()
    {
        var missing = await Assert.ThrowsAsync<ManifestSignatureException>(() => Client().LoadAsync(Write(ManifestBytes, null), PublicKey));
        Assert.Equal(SignatureProblem.Missing, missing.Problem);

        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var changed = await Assert.ThrowsAsync<ManifestSignatureException>(() =>
            Client().LoadAsync(Write(ManifestBytes, ManifestSignature.Sign(ManifestBytes, other)), PublicKey));
        Assert.Equal(SignatureProblem.KeyChanged, changed.Problem);
        Assert.Equal(ManifestSignature.CodeFor(Convert.ToBase64String(other.ExportSubjectPublicKeyInfo())), changed.NewCode);
    }

    [Fact]
    public async Task RefusesTamperedListEvenWithoutPin()
    {
        var sig = ManifestSignature.Sign(ManifestBytes, _key);
        var tampered = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(ManifestBytes).Replace("鯖", "偽"));
        var e = await Assert.ThrowsAsync<ManifestSignatureException>(() => Client().LoadAsync(Write(tampered, sig)));
        Assert.Equal(SignatureProblem.Invalid, e.Problem);
    }

    [Fact]
    public async Task RetriesOnceWhileListIsBeingRepublished()
    {
        ManifestClient.RetryDelay = TimeSpan.Zero;
        var oldSig = ManifestSignature.Sign(Encoding.UTF8.GetBytes("old"), _key);
        var handler = new SequenceHandler(new()
        {
            ["https://h/manifest.json"] = [ManifestBytes, ManifestBytes],
            ["https://h/manifest.sig.json"] = [Encoding.UTF8.GetBytes(oldSig), Encoding.UTF8.GetBytes(ManifestSignature.Sign(ManifestBytes, _key))],
        });
        var m = await Client(handler).LoadAsync(new Uri("https://h/manifest.json"), PublicKey);
        Assert.True(m.Trusted);
        Assert.Equal(4, handler.Requests);
    }

    [Fact]
    public async Task MissingSignatureOverHttpIsUnsigned()
    {
        var handler = new SequenceHandler(new() { ["https://h/manifest.json"] = [ManifestBytes] });
        var m = await Client(handler).LoadAsync(new Uri("https://h/manifest.json"));
        Assert.Null(m.Signature);
    }

    /// <summary>同じ URL に何度目の要求かで違う中身を返す。なければ 404。</summary>
    private sealed class SequenceHandler(Dictionary<string, byte[][]> responses) : HttpMessageHandler
    {
        private readonly Dictionary<string, int> _count = new();
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            var url = request.RequestUri!.ToString();
            if (!responses.TryGetValue(url, out var list))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }
            var i = _count.GetValueOrDefault(url);
            _count[url] = i + 1;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(list[Math.Min(i, list.Length - 1)]) });
        }
    }
}
