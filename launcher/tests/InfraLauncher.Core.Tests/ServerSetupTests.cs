using System.Security.Cryptography;
using System.Text;
using InfraLauncher.Core.Admin;

namespace InfraLauncher.Core.Tests;

public sealed class ServerSetupTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("infralauncher-admin-").FullName;
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public void Dispose()
    {
        _key.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private string Setup => Path.Combine(_dir, "server-setup");
    private string KeyFile => Path.Combine(_dir, "signing-key.dat");
    private string ManifestPath => Path.Combine(_dir, "dist", "list-abc.json");

    private void Create(bool sign = true, bool writeKey = true)
    {
        Directory.CreateDirectory(Setup);
        Directory.CreateDirectory(Path.GetDirectoryName(ManifestPath)!);
        File.WriteAllText(Path.Combine(Setup, "Common.ps1"), "");
        File.WriteAllText(Path.Combine(Setup, "publish-settings.json"),
            $$"""{ "manifest": {{System.Text.Json.JsonSerializer.Serialize(ManifestPath)}}, "share_url": "https://h:8443/list-abc.json", "paksets": [] }""");
        var manifest = Encoding.UTF8.GetBytes("""
            { "schema_version": 1, "servers": [
              { "id": "a", "name": "鯖A", "address": "h:13353", "status": "maintenance", "message": "20時から",
                "pakset": { "name": "p", "folder": "pak", "index_url": "p/index.json", "index_sha256": "x" }, "engine": { "revision": "r1" } },
              { "id": "b", "name": "鯖B", "address": "h:13354", "pakset": { "name": "p", "folder": "pak" } } ] }
            """);
        File.WriteAllBytes(ManifestPath, manifest);
        if (sign)
        {
            File.WriteAllText(Path.Combine(_dir, "dist", "list-abc.sig.json"), ManifestSignature.Sign(manifest, _key));
        }
        if (writeKey)
        {
            File.WriteAllText(KeyFile, $$"""{ "format": "infra-launcher-signing-key-1", "public_key": "{{Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo())}}", "protected": "dpapi:xx" }""");
        }
    }

    [Fact]
    public void ReadsServersKeyAndSignature()
    {
        Create();
        var s = ServerSetup.Load(Setup, KeyFile);
        Assert.Null(s.Error);
        Assert.Equal(ManifestPath, s.ManifestPath);
        Assert.Equal("https://h:8443/list-abc.json", s.ShareUrl);
        Assert.Equal(2, s.Servers.Count);
        Assert.True(s.Servers[0].Maintenance);
        Assert.Equal("20時から", s.Servers[0].Message);
        Assert.Equal("r1", s.Servers[0].EngineRevision);
        Assert.Null(s.Servers[1].Message);
        Assert.Equal(ManifestSignature.CodeFor(Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo())), s.KeyCode);
        Assert.Equal(SignatureState.Ok, s.Signature);
        Assert.False(s.HasGuessableName);
    }

    [Fact]
    public void DetectsMissingTamperedOrOtherKeySignatures()
    {
        Create(sign: false);
        Assert.Equal(SignatureState.Missing, ServerSetup.Load(Setup, KeyFile).Signature);

        Create();
        File.AppendAllText(ManifestPath, " ");
        Assert.Equal(SignatureState.Invalid, ServerSetup.Load(Setup, KeyFile).Signature);

        Create(writeKey: false);
        File.Delete(KeyFile);
        var noKey = ServerSetup.Load(Setup, KeyFile);
        Assert.Null(noKey.KeyCode);
        Assert.Equal(SignatureState.OtherKey, noKey.Signature);
    }

    [Fact]
    public void FindsSetupFolderNextToExe()
    {
        Create();
        Assert.Equal(Setup, ServerSetup.FindFolder(Setup));
        Assert.Equal(Setup, ServerSetup.FindFolder(_dir));
        Assert.Null(ServerSetup.FindFolder(Path.Combine(_dir, "dist")));
    }

    [Fact]
    public void QuotesPowerShellArguments()
    {
        var cmd = PowerShellCommand.Build(@"C:\a b\Edit-ServerList.ps1", [
            new("ServerId", "a"), new("Message", "it's ‘20時’; rm -r C:\\"), new("Maintenance", true), new("ClearMessage", null)]);
        Assert.Contains(@"& 'C:\a b\Edit-ServerList.ps1'", cmd);
        Assert.Contains("-Message 'it''s ‘‘20時’’; rm -r C:\\'", cmd);
        Assert.Contains("-Maintenance:$true", cmd);
        Assert.Contains("-ClearMessage }", cmd);
        Assert.Contains("exit 1", cmd);
        Assert.Equal(cmd, Encoding.Unicode.GetString(Convert.FromBase64String(PowerShellCommand.Encode(cmd))));
    }

    [Fact]
    public void InteractiveCommandAlwaysShowsFinishMessage()
    {
        var cmd = PowerShellCommand.BuildInteractive(@"C:\it's\Add-Server.ps1", "終わりました");
        Assert.StartsWith(@"try { & 'C:\it''s\Add-Server.ps1' }", cmd);
        Assert.Contains("finally { Write-Host ''; Write-Host '終わりました'", cmd);
    }

    [Fact]
    public void InteractiveCommandPassesArguments()
    {
        var cmd = PowerShellCommand.BuildInteractive(@"C:\s\Publish-Pakset.ps1", "終わりました",
            [new("ServerId", new[] { "test", "it's" }), new("Target", "Engine")]);
        Assert.StartsWith(@"try { & 'C:\s\Publish-Pakset.ps1' -ServerId 'test','it''s' -Target 'Engine' }", cmd);
    }

    [Fact]
    public void ParsesEngineExtrasListingPerSource()
    {
        var sources = EngineExtras.Parse(
            "SOURCE\tC:\\NANS_CC_test\tテスト鯖（test）\r\nREQUIRED\tC:\\NANS_CC_test\t0\r\n" +
            "ITEM\tC:\\NANS_CC_test\tfolder\t1\tinterlockingpanel\r\nITEM\tC:\\NANS_CC_test\tfile\t0\todd[1].txt\r\n" +
            "なにか別の出力\r\nITEM\tC:\\NANS_CC_test\tbad\t1\tx\r\nITEM\tC:\\unknown\tfile\t1\ty\r\n" +
            "SOURCE\tC:\\NANSSC3FP2\t本番（main）\r\nREQUIRED\tC:\\NANSSC3FP2\t1\r\nITEM\tC:\\NANSSC3FP2\tfile\t1\tstation_data.json\r\n");
        Assert.Equal(2, sources.Count);
        var test = sources[0];
        Assert.Equal("C:\\NANS_CC_test", test.Folder);
        Assert.Equal("テスト鯖（test）　C:\\NANS_CC_test", test.Label);
        Assert.False(test.Required);
        Assert.Equal(["interlockingpanel", "odd[1].txt"], test.Items.Select(i => i.Name));
        Assert.True(test.Items[0].IsFolder);
        Assert.Equal("interlockingpanel", test.SelectedItemsArgument());
        test.Items[1].Selected = true;
        Assert.Equal("interlockingpanel|odd[1].txt", test.SelectedItemsArgument());
        Assert.True(sources[1].Required);
        Assert.Equal("station_data.json", sources[1].SelectedItemsArgument());
    }
}
