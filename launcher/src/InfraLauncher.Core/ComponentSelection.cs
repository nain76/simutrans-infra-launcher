using InfraLauncher.Core.Models;

namespace InfraLauncher.Core;

/// <summary>本体の部品の選び分け。</summary>
public static class ComponentSelection
{
    /// <summary>
    ///落とすファイルを選ぶ。部品の一覧がなければ全部。
    ///必須の部品と、部品の指定がないファイルはいつも落とす。componentsがnullなら推奨の部品、あればその部品も落とす。
    /// </summary>
    public static List<PaksetFile> SelectFiles(PaksetIndex index, IReadOnlyCollection<string>? components)
    {
        if (index.Components is not { Count: > 0 })
        {
            return index.Files;
        }
        var selected = index.Components
            .Where(c => c.Required || (components is null ? c.Recommended : components.Contains(c.Id)))
            .Select(c => c.Id)
            .ToHashSet(StringComparer.Ordinal);
        return index.Files.Where(f => f.Component is null || selected.Contains(f.Component)).ToList();
    }

    /// <summary>部品ごとの合計サイズ（バイト）。部品の指定がないファイルは必須として数えない。</summary>
    public static Dictionary<string, long> SizeByComponent(PaksetIndex index) =>
        index.Files.Where(f => f.Component is not null)
            .GroupBy(f => f.Component!)
            .ToDictionary(g => g.Key, g => g.Sum(f => f.Size));
}
