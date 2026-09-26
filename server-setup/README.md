# サーバー側のスクリプト

## Publish-Pakset.ps1 — pakset を公開する

サーバーが使っている pakset フォルダを Web 公開用フォルダへコピーし、ランチャーの差分同期に使うファイル一覧（`index.json`）を作ります。
サーバーリストの該当サーバーも書き換えます。Windows PowerShell 5.1（Windows Server に最初から入っているもの）と PowerShell 7 のどちらでも動きます。

```powershell
.\Publish-Pakset.ps1 `
    -Source      C:\simutrans\pak128.japan `
    -Destination C:\inetpub\wwwroot\simutrans\pak128.japan `
    -Manifest    C:\inetpub\wwwroot\simutrans\manifest.json `
    -ServerId    friends-a
```

| 引数 | 説明 |
|---|---|
| `-Source` | サーバーが使っている pakset フォルダ |
| `-Destination` | Web サーバーで公開するフォルダ（pakset ごとに分ける） |
| `-Manifest` | 書き換えるサーバーリスト。省略するとファイル一覧だけ作る |
| `-ServerId` | サーバーリストの中で書き換えるサーバーの `id` |
| `-Version` | 書き込む pakset のバージョン。省略すると日時（例: `2026.09.26-2100`） |
| `-IndexUrl` | `index.json` のアドレス。省略すると、サーバーリストから見た相対パスになる（公開フォルダがサーバーリストと同じフォルダかその下にある場合） |

スクリプトは次のことをします。

1. 変わったファイルだけをコピーする。`-Source` から消えたファイルは公開フォルダからも消す
2. `index.json`（各ファイルのパス・サイズ・SHA256）を書く
3. IIS で `.pak` や `.tab` を配信できるように `web.config` を書く。自分で作った `web.config` がある場合は触らない
4. サーバーリストの `pakset` を `index_url` / `index_sha256` / `version` に書き換える。zip 方式の `url` / `sha256` は消す

実行ファイルなど（`.exe`、`.dll`、`.bat` など）は、警告を出して公開しません。

### アドオンを足すとき

1. サーバーの pakset フォルダ（`-Source`）に pak をコピーする
2. simutrans サーバーを再起動する（pak は起動時にしか読み込まれないため）
3. このスクリプトを実行する

友人のランチャーは、次に接続するとき足したファイルだけを落とします。
