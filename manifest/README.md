# サーバー一覧マニフェスト

ランチャーが読み込むサーバー一覧の JSON ファイルです。Web サーバー（IIS の静的ファイル配信など）に置いて、その URL を友人に伝えます。API サーバーは不要です。

- 形式の定義: [manifest.schema.json](manifest.schema.json)（JSON Schema）
- 記入例: [manifest.sample.json](manifest.sample.json)

## 項目

| 項目 | 必須 | 説明 |
|---|---|---|
| `schema_version` | ○ | 形式のバージョン。今は `1` |
| `updated_at` | | ファイルを更新した日時 |
| `servers[].id` | ○ | 変わらない識別子（英数字と `_ . + -`）。表示名を変えてもこれは変えない |
| `servers[].name` | ○ | 表示名 |
| `servers[].address` | ○ | `host` か `host:port`。ポートを省略すると 13353 |
| `servers[].status` | | `online` / `offline` / `maintenance` / `unknown` |
| `servers[].players` | | 接続中の人数 |
| `servers[].message` | | お知らせ |
| `servers[].engine.revision` | | 本体のリビジョン名。ランチャーはリビジョンごとに別フォルダに本体を入れる |
| `servers[].engine.builds.<OS>` | | OS ごとの本体の zip。キーは `windows-x64` / `windows-arm64` / `linux-x64` / `linux-arm64` / `macos-x64` / `macos-arm64` |
| `…builds.<OS>.url` / `.sha256` | | zip の URL と SHA256 |
| `…builds.<OS>.exe` | | zip を展開した場所から見た実行ファイルの位置（例: `simutrans.exe`） |
| `servers[].pakset.name` / `.version` | ○ / | 表示用の名前とバージョン |
| `servers[].pakset.folder` | ○ | `-objects` に渡すフォルダ名。本体の実行ファイルと同じフォルダの中に、この名前で展開される |
| `servers[].pakset.url` / `.sha256` | ○ | pakset の zip の URL と SHA256 |

zip の最上位がフォルダ1つだけ（公式配布の `pak128.japan/…` や `simutrans/…` など）の場合、そのフォルダは取り除いて展開されます。配布されている zip をそのまま使えます。

今の OS 用の `engine.builds` がない場合、ランチャーはユーザーが設定した手元の simutrans を使い、pakset だけを同期します。

## SHA256 の求め方

zip ファイルそのもののハッシュを書きます。PowerShell なら:

```powershell
(Get-FileHash .\pak128.japan-2026.09.01.zip -Algorithm SHA256).Hash.ToLower()
```

zip の中身を入れ替えたら、ファイル名が同じでも SHA256 を書き換えてください。ランチャーは SHA256 が変わったときだけダウンロードし直します。

## 更新のしかた

- `engine` と `pakset`: バージョンを上げたときに手で書き換える
- `status` / `players` / `message`: 今は手で書き換える。次の段階で、サーバー側の PowerShell スクリプトが `nettool clients` の結果から定期的に書き換える予定
