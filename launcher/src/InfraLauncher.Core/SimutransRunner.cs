using System.Diagnostics;

namespace InfraLauncher.Core;

public static class SimutransRunner
{
    public static Process Start(string exe, IEnumerable<string> args)
    {
        if (!File.Exists(exe))
        {
            throw new FileNotFoundException($"simutransの実行ファイルが見つかりません: {exe}", exe);
        }
        //本体はargv[0]のフォルダをデータフォルダにするので、フルパスで起動する
        var psi = new ProcessStartInfo(Path.GetFullPath(exe))
        {
            WorkingDirectory = SimutransPaths.DataDirFor(exe),
            UseShellExecute = false,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }
        return Process.Start(psi) ?? throw new InvalidOperationException("simutransを起動できませんでした");
    }
}
