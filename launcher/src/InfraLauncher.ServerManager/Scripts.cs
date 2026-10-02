using System.Diagnostics;
using System.Text;
using InfraLauncher.Core.Admin;

namespace InfraLauncher.ServerManager;

/// <summary>
/// server-setup のスクリプトを呼ぶ。書き換えはすべてスクリプトに任せ、このツールでは同じ処理を作り直さない
/// （今動いている環境と同じやり方で書き換えるため）。
/// </summary>
internal static class Scripts
{
    /// <summary>
    /// 質問をしないスクリプト（Edit-ServerList.ps1 など）を実行し、終わるまで待って出力を返す。
    /// Windows PowerShell 5.1（powershell.exe）で動かす。ほかのバッチファイルと同じものを使うため。
    /// </summary>
    public static async Task<(bool Ok, string Output)> RunAsync(string folder, string script, IEnumerable<KeyValuePair<string, object?>> arguments)
    {
        var command = PowerShellCommand.Build(Path.Combine(folder, script), arguments);
        var start = new ProcessStartInfo("powershell.exe")
        {
            WorkingDirectory = folder,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-OutputFormat", "Text", "-EncodedCommand", PowerShellCommand.Encode(command) })
        {
            start.ArgumentList.Add(arg);
        }
        try
        {
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var output = (await stdout + await stderr).Trim();
            return (process.ExitCode == 0, output);
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            return (false, $"PowerShell を起動できませんでした: {e.Message}");
        }
    }

    /// <summary>
    /// 質問をするスクリプトを、別のPowerShellの画面で開く。バッチファイルが動かすのと同じスクリプトを、
    /// 同じ PowerShell 5.1 で動かす（このツールは管理者として動いているので、そのまま管理者で動く）。
    /// 画面が閉じられたら <paramref name="exited"/> を呼ぶ。
    /// </summary>
    public static string? OpenInteractive(string folder, string script, Action exited)
    {
        var path = Path.Combine(folder, script);
        if (!File.Exists(path))
        {
            return $"{script} が見つかりません: {path}";
        }
        var command = PowerShellCommand.BuildInteractive(path,
            "終わりました。この画面を閉じると、サーバー管理ツールの表示が新しくなります。");
        var start = new ProcessStartInfo("powershell.exe") { WorkingDirectory = folder, UseShellExecute = false };
        foreach (var arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-NoExit", "-OutputFormat", "Text", "-EncodedCommand", PowerShellCommand.Encode(command) })
        {
            start.ArgumentList.Add(arg);
        }
        try
        {
            var process = Process.Start(start)!;
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) =>
            {
                process.Dispose();
                exited();
            };
            return null;
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            return $"PowerShell を起動できませんでした: {e.Message}";
        }
    }

    public static void OpenFolder(string folder)
    {
        try
        {
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }
}
