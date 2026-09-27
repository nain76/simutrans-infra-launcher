using System.Text.Json.Nodes;
using InfraLauncher.Core;

namespace InfraLauncher.Core.Tests;

public class ManifestTests
{
    private static string Sample => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "manifest.sample.json"));
    private static readonly Uri SampleUri = new("https://example.com/simutrans/manifest.json");

    [Fact]
    public void ParsesSample()
    {
        var m = ManifestClient.Parse(Sample, SampleUri);
        Assert.Equal(2, m.Servers.Count);
        var s = m.Servers[0];
        Assert.Equal("friends-a", s.Id);
        Assert.Equal("友達内輪鯖A", s.Name);
        Assert.Equal(3, s.Players);
        Assert.Equal("online", s.Status);
        Assert.Equal("r1234-2026-09", s.Engine!.Revision);
        Assert.Equal("simutrans.exe", s.Engine.Builds!["windows-x64"].Exe);
        Assert.Equal("pak128.japan", s.Pakset.Folder);
        Assert.False(s.Pakset.UsesFileIndex);

        var b = m.Servers[1];
        Assert.True(b.Pakset.UsesFileIndex);
        Assert.Equal("https://example.com/simutrans/pak128.japan/index.json", b.Pakset.IndexUrl);
    }

    [Theory]
    [InlineData("schema_version", 2)]
    [InlineData("pakset.folder", "../evil")]
    [InlineData("pakset.folder", "..")]
    [InlineData("pakset.sha256", "abc")]
    [InlineData("pakset.url", "ftp://example.com/a.zip")]
    [InlineData("engine.revision", "../../x")]
    [InlineData("address", "host:99999")]
    [InlineData("id", "a/b")]
    public void RejectsInvalid(string path, object value)
    {
        var root = JsonNode.Parse(Sample)!;
        JsonNode target = path == "schema_version" ? root : root["servers"]![0]!;
        var parts = path.Split('.');
        for (var i = 0; i < parts.Length - 1; i++)
        {
            target = target[parts[i]]!;
        }
        target[parts[^1]] = JsonValue.Create(value);
        Assert.Throws<ManifestException>(() => ManifestClient.Parse(root.ToJsonString(), SampleUri));
    }

    [Fact]
    public void RejectsDuplicateIds()
    {
        var root = JsonNode.Parse(Sample)!;
        var servers = root["servers"]!.AsArray();
        servers.Add(servers[0]!.DeepClone());
        Assert.Throws<ManifestException>(() => ManifestClient.Parse(root.ToJsonString(), SampleUri));
    }

    [Fact]
    public void RejectsBrokenJson()
    {
        Assert.Throws<ManifestException>(() => ManifestClient.Parse("{ not json"));
    }
}

public class ManifestFileIndexTests
{
    private static string Manifest(string pakset) => $$"""
        { "schema_version": 1, "servers": [ { "id": "s", "name": "鯖", "address": "h",
          "engine": { "revision": "r1", "builds": { "windows-x64": { "url": "engine.zip", "sha256": "{{new string('a', 64)}}", "exe": "simutrans.exe" } } },
          "pakset": { "name": "p", "folder": "pak", {{pakset}} } } ] }
        """;

    private static readonly string Sha = new('b', 64);

    [Fact]
    public void ResolvesRelativeUrlsAgainstManifestLocation()
    {
        var m = ManifestClient.Parse(Manifest($$"""
            "index_url": "pak128.japan/index.json", "index_sha256": "{{Sha}}"
            """), new Uri("https://example.com/simutrans/manifest.json"));
        var s = m.Servers[0];
        Assert.True(s.Pakset.UsesFileIndex);
        Assert.Equal("https://example.com/simutrans/pak128.japan/index.json", s.Pakset.IndexUrl);
        Assert.Equal("https://example.com/simutrans/engine.zip", s.Engine!.Builds!["windows-x64"].Url);
    }

    [Fact]
    public void RelativeUrlsNeedBase()
    {
        Assert.Throws<ManifestException>(() => ManifestClient.Parse(Manifest($$"""
            "index_url": "index.json", "index_sha256": "{{Sha}}"
            """)));
    }

    [Fact]
    public void WebListCannotPointToLocalFiles()
    {
        Assert.Throws<ManifestException>(() => ManifestClient.Parse(Manifest($$"""
            "url": "file:///C:/secret.zip", "sha256": "{{Sha}}"
            """), new Uri("https://example.com/manifest.json")));
    }

    [Theory]
    [InlineData("\"url\": \"https://x/a.zip\", \"sha256\": \"B\", \"index_url\": \"https://x/i.json\", \"index_sha256\": \"B\"")]
    [InlineData("\"index_url\": \"https://x/i.json\"")]
    [InlineData("\"name2\": 1")]
    public void RequiresExactlyOneMethod(string pakset)
    {
        Assert.Throws<ManifestException>(() => ManifestClient.Parse(Manifest(pakset.Replace("\"B\"", $"\"{Sha}\""))));
    }

    private static string Index(string path) =>
        $$"""{ "schema_version": 1, "files": [ { "path": {{System.Text.Json.JsonSerializer.Serialize(path)}}, "size": 1, "sha256": "{{Sha}}" } ] }""";

    [Theory]
    [InlineData("ground.pak")]
    [InlineData("config/simuconf.tab")]
    [InlineData("addons/日本語 車両.pak")]
    [InlineData("tool/script.nut")]
    public void AcceptsIndexPaths(string path)
    {
        Assert.Single(ManifestClient.ParseIndex(Index(path)).Files);
    }

    [Theory]
    [InlineData("../evil.pak")]
    [InlineData("a/../../evil.pak")]
    [InlineData("/abs.pak")]
    [InlineData("C:/abs.pak")]
    [InlineData("a\\b.pak")]
    [InlineData("a//b.pak")]
    [InlineData("./a.pak")]
    [InlineData("con.pak")]
    [InlineData("a./b.pak")]
    [InlineData("virus.exe")]
    [InlineData("run.BAT")]
    [InlineData("lib/x.dll")]
    [InlineData("")]
    public void RejectsUnsafeIndexPaths(string path)
    {
        Assert.Throws<ManifestException>(() => ManifestClient.ParseIndex(Index(path)));
    }

    [Fact]
    public void RejectsDuplicateAndConflictingPaths()
    {
        string Two(string a, string b) => $$"""
            { "schema_version": 1, "files": [
              { "path": "{{a}}", "size": 1, "sha256": "{{Sha}}" }, { "path": "{{b}}", "size": 1, "sha256": "{{Sha}}" } ] }
            """;
        Assert.Throws<ManifestException>(() => ManifestClient.ParseIndex(Two("a.pak", "A.PAK")));
        Assert.Throws<ManifestException>(() => ManifestClient.ParseIndex(Two("a", "a/b.pak")));
    }
}

public class EngineTrustTests
{
    private static string Manifest(string engineUrl) => $$"""
        { "schema_version": 1, "servers": [ { "id": "s", "name": "鯖", "address": "h",
          "engine": { "revision": "r1", "builds": { "windows-x64": { "url": "{{engineUrl}}", "sha256": "{{new string('a', 64)}}", "exe": "simutrans.exe" } } },
          "pakset": { "name": "p", "folder": "pak", "index_url": "pak/index.json", "index_sha256": "{{new string('b', 64)}}" } } ] }
        """;

    [Theory]
    [InlineData("https://example.com/manifest.json", "engine/a.zip", true)]
    [InlineData("https://example.com/manifest.json", "https://cdn.example.com/a.zip", true)]
    [InlineData("https://example.com/manifest.json", "http://cdn.example.com/a.zip", false)]
    [InlineData("http://example.com:8080/manifest.json", "engine/a.zip", false)]
    [InlineData("http://example.com:8080/manifest.json", "https://cdn.example.com/a.zip", false)]
    [InlineData("file:///C:/lists/manifest.json", "engine/a.zip", true)]
    public void AllowsEngineOnlyOverHttps(string listUrl, string engineUrl, bool allowed)
    {
        var m = ManifestClient.Parse(Manifest(engineUrl), new Uri(listUrl));
        Assert.Equal(allowed, m.Servers[0].EngineDownloadAllowed);
    }
}
