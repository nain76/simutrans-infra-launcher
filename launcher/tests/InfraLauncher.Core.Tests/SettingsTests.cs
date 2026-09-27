using InfraLauncher.Core;

namespace InfraLauncher.Core.Tests;

public sealed class SettingsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("infralauncher-settings-").FullName;
    private InstallLayout Layout => new(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void RoundTrips()
    {
        var s = new LauncherSettings { SimutransExe = @"C:\simutrans\simutrans.exe" };
        var list = new ServerListSource { Name = "友達グループ", Url = "https://example.com/manifest.json" };
        var profile = new ManualProfile { Name = "手動鯖", Address = "h:1", PaksetFolder = "pak64" };
        s.ServerLists.Add(list);
        s.ManualProfiles.Add(profile);
        s.SetFavorite(FavoriteKeys.ForListed(list, "friends-a"), true);
        s.SetFavorite(FavoriteKeys.ForManual(profile), true);
        s.Save(Layout);

        var loaded = LauncherSettings.Load(Layout);
        Assert.Equal("友達グループ", Assert.Single(loaded.ServerLists).Name);
        Assert.Equal(profile.Id, Assert.Single(loaded.ManualProfiles).Id);
        Assert.True(loaded.IsFavorite("list:https://example.com/manifest.json#friends-a"));
        Assert.True(loaded.IsFavorite(FavoriteKeys.ForManual(profile)));
        Assert.Contains("友達グループ", File.ReadAllText(Layout.SettingsPath));
    }

    [Fact]
    public void MigratesOldManifestUrls()
    {
        File.WriteAllText(Layout.SettingsPath, """{ "manifest_urls": ["https://a/m.json", "https://b/m.json"] }""");
        var s = LauncherSettings.Load(Layout);
        Assert.Equal(["https://a/m.json", "https://b/m.json"], s.ServerLists.Select(l => l.Url));
        Assert.Null(s.ManifestUrls);
    }

    [Fact]
    public void SetFavoriteToggles()
    {
        var s = new LauncherSettings();
        s.SetFavorite("k", true);
        s.SetFavorite("k", true);
        Assert.Single(s.FavoriteKeys);
        s.SetFavorite("k", false);
        Assert.Empty(s.FavoriteKeys);
    }

    [Fact]
    public void ManualProfileUsesItsOwnExeOrDefault()
    {
        var def = Path.Combine(_dir, "default", "simutrans");
        var mine = Path.Combine(_dir, "mine", "simutrans");
        foreach (var f in new[] { def, mine })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(f)!);
            File.WriteAllText(f, f);
        }
        var profile = new ManualProfile { Name = "p", Address = "example.net", PaksetFolder = "pak128.japan" };
        var settings = new LauncherSettings { SimutransExe = def };

        var info = LauncherService.PrepareManual(profile, settings);
        Assert.Equal(["-objects", "pak128.japan/", "-noaddons", "-load", "net:example.net:13353"], info.Args);
        Assert.Equal(def, info.ExePath);
        // 本人が指定した simutrans なので承認は要らない
        Assert.False(info.ManagedEngine);
        Assert.False(info.NeedsApproval);

        profile.SimutransExe = mine;
        Assert.Equal(mine, LauncherService.PrepareManual(profile, settings).ExePath);

        profile.SimutransExe = null;
        Assert.Throws<SyncException>(() => LauncherService.PrepareManual(profile, new LauncherSettings()));
    }

    [Fact]
    public void ApprovalIsCaseInsensitiveAndUnique()
    {
        var s = new LauncherSettings();
        s.Approve("ABCDEF");
        s.Approve("abcdef");
        Assert.Single(s.ApprovedExecutables);
        Assert.True(s.IsApproved("AbCdEf"));
    }
}
