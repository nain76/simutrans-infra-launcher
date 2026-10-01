using InfraLauncher.Core;

namespace InfraLauncher.Core.Tests;

public class ServerAddressTests
{
    [Theory]
    [InlineData("example.ddns.net:13353", "example.ddns.net", 13353)]
    [InlineData("example.ddns.net", "example.ddns.net", 13353)]
    [InlineData("192.168.0.10:2000", "192.168.0.10", 2000)]
    [InlineData(" host:1 ", "host", 1)]
    [InlineData("[::1]:13354", "[::1]", 13354)]
    [InlineData("[::1]", "[::1]", 13353)]
    public void Parses(string text, string host, int port)
    {
        Assert.Equal(new ServerAddress(host, port), ServerAddress.Parse(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData(":13353")]
    [InlineData("host:")]
    [InlineData("host:0")]
    [InlineData("host:70000")]
    [InlineData("host:abc")]
    [InlineData("::1")]
    [InlineData("a b:1")]
    public void RejectsInvalid(string text)
    {
        Assert.False(ServerAddress.TryParse(text, out _));
    }
}

public class LaunchCommandBuilderTests
{
    [Fact]
    public void BuildsConnectCommand()
    {
        var args = LaunchCommandBuilder.Build("pak128.japan", ServerAddress.Parse("example.ddns.net"));
        Assert.Equal(["-objects", "pak128.japan/", "-noaddons", "-load", "net:example.ddns.net:13353"], args);
    }

    [Fact]
    public void NormalizesTrailingSeparator()
    {
        var args = LaunchCommandBuilder.Build("pak64\\", ServerAddress.Parse("h:1"));
        Assert.Equal("pak64/", args[1]);
    }

    [Fact]
    public void QuotesPathsWithSpaces()
    {
        var text = LaunchCommandBuilder.ToDisplayString(@"C:\Program Files\simutrans\simutrans.exe", ["-objects", "pak64/"]);
        Assert.Equal("\"C:\\Program Files\\simutrans\\simutrans.exe\" -objects pak64/", text);
    }
}

public class SimutransPathsTests
{
    [Fact]
    public void DataDirIsExeDirectory()
    {
        var exe = Path.Combine(Path.GetTempPath(), "st", "simutrans.exe");
        Assert.Equal(Path.Combine(Path.GetTempPath(), "st"), SimutransPaths.DataDirFor(exe));
    }

    [Fact]
    public void DataDirSkipsMacBundle()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        Assert.Equal("/games/st", SimutransPaths.DataDirFor("/games/st/simutrans.app/Contents/MacOS/simutrans"));
    }
}

public class PaksetDisplayNameTests
{
    [Theory]
    [InlineData("pak128.japan", "pak128.japan", null, "pak128.japan")]
    [InlineData("pak128.japan", "pak128.japan", "2026.09.01", "pak128.japan 2026.09.01")]
    [InlineData("pak128.japan", "pak128.japan-2", "v2", "pak128.japan（pak128.japan-2） v2")]
    public void ShowsFolderWhenItDiffers(string name, string folder, string? version, string expected)
    {
        var p = new InfraLauncher.Core.Models.PaksetInfo { Name = name, Folder = folder, Version = version };
        Assert.Equal(expected, p.DisplayName);
    }
}

public class ServerProbeTests
{
    [Fact]
    public async Task DetectsListeningPort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        Assert.True(await ServerProbe.IsReachableAsync(new ServerAddress("127.0.0.1", port), TimeSpan.FromSeconds(3)));
        listener.Stop();
        Assert.False(await ServerProbe.IsReachableAsync(new ServerAddress("127.0.0.1", port), TimeSpan.FromSeconds(3)));
    }
}
