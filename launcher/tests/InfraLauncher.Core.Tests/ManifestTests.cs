using System.Text.Json.Nodes;
using InfraLauncher.Core;

namespace InfraLauncher.Core.Tests;

public class ManifestTests
{
    private static string Sample => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "manifest.sample.json"));

    [Fact]
    public void ParsesSample()
    {
        var m = ManifestClient.Parse(Sample);
        var s = Assert.Single(m.Servers);
        Assert.Equal("friends-a", s.Id);
        Assert.Equal("友達内輪鯖A", s.Name);
        Assert.Equal(3, s.Players);
        Assert.Equal("online", s.Status);
        Assert.Equal("r1234-2026-09", s.Engine!.Revision);
        Assert.Equal("simutrans.exe", s.Engine.Builds!["windows-x64"].Exe);
        Assert.Equal("pak128.japan", s.Pakset.Folder);
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
        Assert.Throws<ManifestException>(() => ManifestClient.Parse(root.ToJsonString()));
    }

    [Fact]
    public void RejectsDuplicateIds()
    {
        var root = JsonNode.Parse(Sample)!;
        var servers = root["servers"]!.AsArray();
        servers.Add(servers[0]!.DeepClone());
        Assert.Throws<ManifestException>(() => ManifestClient.Parse(root.ToJsonString()));
    }

    [Fact]
    public void RejectsBrokenJson()
    {
        Assert.Throws<ManifestException>(() => ManifestClient.Parse("{ not json"));
    }
}
