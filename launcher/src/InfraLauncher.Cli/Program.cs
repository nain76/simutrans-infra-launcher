using InfraLauncher.Core;
using InfraLauncher.Core.Models;

// コマンドライン版。動作確認とトラブル調査用。
//   list   <manifest>            サーバー一覧と同期の状態
//   sync   <manifest> <server>   同期だけ行う
//   launch <manifest> <server>   同期して起動（--print-only で起動せずにコマンドを表示）
// 共通オプション: --data-dir <dir>（ランチャーのデータフォルダ）, --simutrans <exe>（手元の本体）

Console.OutputEncoding = System.Text.Encoding.UTF8;

var positional = new List<string>();
string? dataDir = null, simutransExe = null;
var printOnly = false;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--data-dir" when i + 1 < args.Length: dataDir = args[++i]; break;
        case "--simutrans" when i + 1 < args.Length: simutransExe = args[++i]; break;
        case "--print-only": printOnly = true; break;
        case "-h" or "--help": return Usage();
        default: positional.Add(args[i]); break;
    }
}
if (positional.Count < 2)
{
    return Usage();
}

var layout = dataDir is null ? InstallLayout.Default() : new InstallLayout(dataDir);
using var http = new HttpClient();
http.DefaultRequestHeaders.UserAgent.ParseAdd("InfraLauncher/0.1");
var service = new LauncherService(layout, http);
var settings = service.LoadSettings();
if (simutransExe is not null)
{
    settings.SimutransExe = simutransExe;
}

try
{
    var manifest = await service.Manifests.LoadAsync(LauncherService.ToUri(positional[1]));
    switch (positional[0])
    {
        case "list":
            foreach (var s in manifest.Servers)
            {
                Console.WriteLine($"{s.Name} [{s.Id}]  {s.Address}  {s.Status ?? "unknown"}  {s.Players?.ToString() ?? "-"}人");
                if (!string.IsNullOrEmpty(s.Message))
                {
                    Console.WriteLine($"  お知らせ: {s.Message}");
                }
                try
                {
                    var plan = service.Sync.Plan(s, settings);
                    foreach (var item in plan.Items)
                    {
                        Console.WriteLine($"  {item.Label}: {(item.Needed ? "要同期" : "最新")}  → {item.TargetDir}");
                    }
                }
                catch (SyncException e)
                {
                    Console.WriteLine($"  同期できません: {e.Message}");
                }
            }
            return 0;

        case "sync":
        {
            var server = Find(manifest, positional);
            var plan = service.Sync.Plan(server, settings);
            var summary = await service.Sync.SyncAsync(plan, new ConsoleProgress());
            Console.WriteLine(summary.Downloads == 0 && summary.Removed == 0
                ? "すべて最新です。ダウンロードは不要でした"
                : $"同期が完了しました（ダウンロード {summary.Downloads} 件・{summary.Bytes:N0} バイト、片付け {summary.Removed} 件）");
            return 0;
        }

        case "launch":
        {
            var server = Find(manifest, positional);
            var (_, command) = await service.SyncAndLaunchAsync(server, settings, printOnly, new ConsoleProgress());
            Console.WriteLine(printOnly ? command : $"起動しました: {command}");
            return 0;
        }

        default:
            return Usage();
    }
}
catch (Exception e) when (e is ManifestException or SyncException or FormatException or FileNotFoundException)
{
    Console.Error.WriteLine($"エラー: {e.Message}");
    return 1;
}

static ServerEntry Find(Manifest manifest, List<string> positional)
{
    if (positional.Count < 3)
    {
        throw new FormatException("サーバーの id か名前を指定してください");
    }
    var key = positional[2];
    return manifest.Servers.FirstOrDefault(s => s.Id == key)
        ?? manifest.Servers.FirstOrDefault(s => s.Name == key)
        ?? throw new FormatException($"サーバーが見つかりません: {key}");
}

static int Usage()
{
    Console.Error.WriteLine("""
        使い方:
          infra-launcher list   <manifest の URL かパス>
          infra-launcher sync   <manifest> <サーバーの id か名前>
          infra-launcher launch <manifest> <サーバーの id か名前> [--print-only]
        オプション:
          --data-dir <dir>    ランチャーのデータフォルダ（既定: %LOCALAPPDATA%\InfraLauncher など）
          --simutrans <exe>   サーバーリストにこの PC 用の本体がないときに使う simutrans の実行ファイル
        """);
    return 2;
}

sealed class ConsoleProgress : IProgress<SyncProgress>
{
    private string? _last;

    public void Report(SyncProgress p)
    {
        var line = p.BytesTotal is > 0
            ? $"{p.Item.Label}: {p.Stage} {p.BytesDone * 100 / p.BytesTotal}%"
            : $"{p.Item.Label}: {p.Stage}";
        if (line != _last)
        {
            Console.WriteLine(line);
            _last = line;
        }
    }
}
