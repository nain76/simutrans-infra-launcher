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
        var profile = new ManualProfile { Name = "p", Address = "example.net", PaksetFolder = "pak128.japan" };
        var settings = new LauncherSettings { SimutransExe = "/games/default/simutrans" };

        var (_, cmd) = LauncherService.LaunchManual(profile, settings, printOnly: true);
        Assert.Equal("/games/default/simutrans -objects pak128.japan/ -noaddons -load net:example.net:13353", cmd);

        profile.SimutransExe = "/games/mine/simutrans";
        (_, cmd) = LauncherService.LaunchManual(profile, settings, printOnly: true);
        Assert.StartsWith("/games/mine/simutrans ", cmd);

        profile.SimutransExe = null;
        Assert.Throws<SyncException>(() => LauncherService.LaunchManual(profile, new LauncherSettings(), printOnly: true));
    }
}
