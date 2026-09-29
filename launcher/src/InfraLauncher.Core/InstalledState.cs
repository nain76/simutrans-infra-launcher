namespace InfraLauncher.Core;

/// <summary>
/// 展開済みのフォルダと、その元になった zip の sha256 の記録。
/// 展開後のフォルダは zip と直接比べられないので、この記録とマニフェストの sha256 を比べて同期の要否を決める。
/// </summary>
public sealed class InstalledState
{
    /// <summary>キーは展開先フォルダのフルパス。</summary>
    public Dictionary<string, InstalledRecord> Items { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static InstalledState Load(InstallLayout layout)
    {
        var state = Json.Load(layout.InstalledStatePath, Json.Context.InstalledState);
        // 読み込み直後は比較方法が既定に戻るので作り直す
        state.Items = new(state.Items, StringComparer.OrdinalIgnoreCase);
        return state;
    }

    public void Save(InstallLayout layout) => Json.Save(layout.InstalledStatePath, this, Json.Context.InstalledState);

    public InstalledRecord? Get(string dir) => Items.GetValueOrDefault(Key(dir));

    public void Set(string dir, InstalledRecord record) => Items[Key(dir)] = record;

    /// <summary>フォルダを消したとき、その中にあった記録もまとめて消す。</summary>
    public void RemoveUnder(string dir)
    {
        var key = Key(dir);
        foreach (var k in Items.Keys.Where(k => k.Equals(key, StringComparison.OrdinalIgnoreCase)
                     || k.StartsWith(key + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            Items.Remove(k);
        }
    }

    private static string Key(string dir) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
}

public sealed class InstalledRecord
{
    public string Sha256 { get; set; } = "";
    public string Url { get; set; } = "";
    public DateTimeOffset InstalledAt { get; set; }
    /// <summary>ファイル一覧方式のときだけ、各ファイルの照合用の記録（キーは pakset フォルダからの相対パス）。</summary>
    public Dictionary<string, FileStamp>? Files { get; set; }
    /// <summary>本体の部品の選び方（"recommended" か、選んだ部品の id を並べたもの）。変わったら同期し直す。</summary>
    public string? Selection { get; set; }
}
