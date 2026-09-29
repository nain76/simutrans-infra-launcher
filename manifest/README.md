# サーバー一覧マニフェスト

ランチャーが読み込むサーバー一覧の JSON ファイルです（ランチャーの画面では「サーバーリスト」と呼びます）。Web サーバー（IIS の静的ファイル配信など）に置いて、その URL を友人に伝えます。友人はランチャーの「追加」→「サーバー管理者から共有されたリストを追加」にその URL を入れます。API サーバーは不要です。

- 形式の定義: [manifest.schema.json](manifest.schema.json)（JSON Schema）、ファイル一覧は [index.schema.json](index.schema.json)
- 記入例: [manifest.sample.json](manifest.sample.json)

## pakset の配り方は2通り

| | zip 方式 | ファイル一覧方式（おすすめ） |
|---|---|---|
| 書く項目 | `pakset.url` と `pakset.sha256` | `pakset.index_url` と `pakset.index_sha256` |
| 置くもの | pakset の zip 1つ | pakset のファイルそのままと、ファイル一覧 `index.json` |
| アドオンを1つ足したとき | 友人は zip 全体を落とし直す | 友人は足したファイルだけを落とす |
| 準備 | zip を作って SHA256 を書く | [Publish-Pakset.ps1](../server-setup/README.md) を実行するだけ |

ファイル一覧方式では、ランチャーは接続のたびに手元のファイルを一覧と照合します。そのため、友人が手元の pak を消したり書き換えたりしても元に戻ります。

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
| `servers[].pakset.url` / `.sha256` | ※ | zip 方式: pakset の zip のアドレスと SHA256 |
| `servers[].pakset.index_url` / `.index_sha256` | ※ | ファイル一覧方式: `index.json` のアドレスと SHA256 |

※ zip 方式とファイル一覧方式のどちらか一方を書きます。

アドレスは `https://…` の絶対アドレスのほか、サーバーリストの場所から見た相対パス（`pak128.japan/index.json` や `engine.zip`）でも書けます。Web 上のサーバーリストから手元のファイル（`file://`）を指すことはできません。

zip の最上位がフォルダ1つだけ（公式配布の `pak128.japan/…` や `simutrans/…` など）の場合、そのフォルダは取り除いて展開されます。配布されている zip をそのまま使えます。

今の OS 用の `engine.builds` がない場合、ランチャーはユーザーが設定した手元の simutrans を使い、pakset だけを同期します。

## ファイル一覧方式の注意

- ファイル一覧に載せられないもの（ランチャーが拒否します）
  - `..` を含むパス、絶対パス、Windows で作れない名前
  - 実行ファイルなど（`.exe`、`.dll`、`.bat`、`.ps1`、`.vbs` など）
- simutrans のスクリプト（`.nut`）は pakset の正式な中身なので載せられます
- IIS で配信する場合、`.pak` や `.tab` は初期設定では配信されません。`Publish-Pakset.ps1` が必要な `web.config` を公開フォルダに作ります
- 公開中にファイルを入れ替えると、ちょうど同期中だった友人の同期がハッシュの不一致で止まることがあります。その場合はもう一度接続すれば直ります

## SHA256 の求め方（zip 方式）

zip ファイルそのもののハッシュを書きます。PowerShell なら:

```powershell
(Get-FileHash .\pak128.japan-2026.09.01.zip -Algorithm SHA256).Hash.ToLower()
```

zip の中身を入れ替えたら、ファイル名が同じでも SHA256 を書き換えてください。ランチャーは SHA256 が変わったときだけダウンロードし直します。

## 更新のしかた

- `pakset`（ファイル一覧方式）: `Publish-Pakset.ps1` が書き換える
- `engine` と `pakset`（zip 方式）: バージョンを上げたときに手で書き換える
- `status` / `players` / `message`: 今は手で書き換える。次の段階で、サーバー側の PowerShell スクリプトが `nettool clients` の結果から定期的に書き換える予定
- 手で書き換えたあとは、`server-setup/Manage-SigningKey.bat` の「3. サーバーリストに署名し直す」を実行する（下の「署名」）

## 署名（manifest.sig.json）

サーバーリストの隣（`manifest.json` なら `manifest.sig.json`。`.json` で終わらないアドレスなら末尾に `.sig.json` を足した場所）に署名を置く。
server-setup のスクリプトが、サーバーリストを書き換えるたびに自動で作る。

```json
{
  "format": "infra-launcher-signature-1",
  "public_key": "<公開鍵。P-256 の SubjectPublicKeyInfo を base64 にしたもの>",
  "signature": "<manifest.json のバイト列そのものに対する ECDSA P-256 / SHA-256 の署名。r と s を並べた 64 バイトを base64 にしたもの>",
  "signed_at": "2026-09-29T20:00:00+09:00"
}
```

- 確認コードは、公開鍵（base64 を戻したバイト列）の SHA256 の先頭 10 バイトを 16 進数の大文字にして4文字ずつ `-` で区切ったもの
- ランチャーは、ユーザーが確認コードを登録した公開鍵を覚え、以後その鍵の正しい署名がなければ読み込まない。署名は manifest.json のバイト列に対するものなので、署名したあとは1バイトでも変えてはいけない
- pakset と本体のファイル一覧・zip は manifest.json の SHA256 で結び付いているので、manifest.json の署名だけですべてを確かめられる
- 本体（engine）の自動インストールは、確認コードを登録したリストだけに許す
