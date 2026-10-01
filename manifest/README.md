# サーバー一覧マニフェスト

ランチャーが読み込むサーバー一覧のJSONファイルです。ランチャーの画面では「サーバーリスト」と呼んでいます。Webサーバー（IISの静的ファイル配信など）に置き、そのURLを友人に伝えます。友人はランチャーの「追加」から「サーバー管理者から共有されたリストを追加」を選び、そのURLを入れます。APIサーバーは要りません。

形式の定義は[manifest.schema.json](manifest.schema.json)（JSON Schema）に、ファイル一覧の形式は[index.schema.json](index.schema.json)にあります。記入例は[manifest.sample.json](manifest.sample.json)です。

## paksetの配り方は2通り

| | zip方式 | ファイル一覧方式（おすすめ） |
|---|---|---|
| 書く項目 | `pakset.url`と`pakset.sha256` | `pakset.index_url`と`pakset.index_sha256` |
| 置くもの | paksetのzip 1つ | paksetのファイルそのままと、ファイル一覧`index.json` |
| アドオンを1つ足したとき | 友人はzip全体を落とし直す | 友人は足したファイルだけを落とす |
| 準備 | zipを作ってSHA256を書く | [Publish-Pakset.ps1](../server-setup/README.md)を実行するだけ |

ファイル一覧方式では、ランチャーは接続のたびに手元のファイルを一覧と照合します。そのため、友人が手元のpakを消したり書き換えたりしても元に戻ります。

## 項目

| 項目 | 必須 | 説明 |
|---|---|---|
| `schema_version` | ○ | 形式のバージョン。今は`1` |
| `updated_at` | | ファイルを更新した日時 |
| `servers[].id` | ○ | 変わらない識別子（英数字と`_ . + -`）。表示名を変えてもこれは変えない |
| `servers[].name` | ○ | 表示名 |
| `servers[].address` | ○ | `host`か`host:port`。ポートを省略すると13353 |
| `servers[].status` | | `online` / `offline` / `maintenance` / `unknown`。ランチャーは稼働中かどうかを実際にポートにつないで確かめるので、`online` / `offline`は表示に使わない。`maintenance`だけは「メンテナンス中」と出す |
| `servers[].players` | | 接続中の人数 |
| `servers[].message` | | お知らせ |
| `servers[].engine.revision` | | 本体のリビジョン名。ランチャーはリビジョンごとに別フォルダに本体を入れる |
| `servers[].engine.builds.<OS>` | | OSごとの本体のzip。キーは`windows-x64` / `windows-arm64` / `linux-x64` / `linux-arm64` / `macos-x64` / `macos-arm64` |
| `…builds.<OS>.url` / `.sha256` | | zipのURLとSHA256 |
| `…builds.<OS>.exe` | | zipを展開した場所から見た実行ファイルの位置（例: `simutrans.exe`） |
| `servers[].pakset.name` / `.version` | ○ / | 表示用の名前とバージョン |
| `servers[].pakset.folder` | ○ | `-objects`に渡すフォルダ名。本体の実行ファイルと同じフォルダの中に、この名前で展開される |
| `servers[].pakset.url` / `.sha256` | ※ | zip方式: paksetのzipのアドレスとSHA256 |
| `servers[].pakset.index_url` / `.index_sha256` | ※ | ファイル一覧方式: `index.json`のアドレスとSHA256 |

※ zip方式とファイル一覧方式のどちらか一方を書きます。

アドレスは`https://…`の絶対アドレスのほか、サーバーリストの場所から見た相対パス（`pak128.japan/index.json`や`engine.zip`）でも書けます。Web上のサーバーリストから手元のファイル（`file://`）を指すことはできません。

zipの最上位がフォルダ1つだけ（公式配布の`pak128.japan/…`や`simutrans/…`など）の場合、そのフォルダは取り除いて展開されます。配布されているzipをそのまま使えます。

今のOS用の`engine.builds`がない場合、ランチャーはユーザーが設定した手元のsimutransを使い、paksetだけを同期します。

## ファイル一覧方式の注意

paksetのファイル一覧には、`..`を含むパス、絶対パス、Windowsで作れない名前は載せられません。実行ファイルなど（`.exe`、`.dll`、`.bat`、`.ps1`、`.vbs`など）も、ランチャーが受け付けません。simutransのスクリプト（`.nut`）はpaksetの正式な中身なので載せられます。

IISは初めの設定では`.pak`や`.tab`を配信しません。そのため、`Publish-Pakset.ps1`が必要な`web.config`を公開フォルダに作ります。

公開中にファイルを入れ替えると、ちょうど同期していた友人の同期が、ハッシュの不一致で止まることがあります。その場合は、もう一度同期すれば直ります。

## SHA256の求め方（zip方式）

zipファイルそのもののハッシュを書きます。PowerShellでは次のコマンドで求められます。

```powershell
(Get-FileHash .\pak128.japan-2026.09.01.zip -Algorithm SHA256).Hash.ToLower()
```

zipの中身を入れ替えたら、ファイル名が同じでもSHA256を書き換えてください。ランチャーはSHA256が変わったときだけダウンロードし直します。

## 更新のしかた

ファイル一覧方式の`pakset`と`engine`は、`Publish-Pakset.ps1`が書き換えます。zip方式の`engine`と`pakset`は、バージョンを上げたときに手で書き換えます。`status`と`players`と`message`は今のところ手で書き換えます。将来は、サーバー側のスクリプトが`nettool clients`の結果から定期的に書き換える予定です。

手で書き換えたあとは、`server-setup/Manage-SigningKey.bat`の「3. サーバーリストに署名し直す」を実行してください。署名については次の節で説明します。

## 署名（manifest.sig.json）

署名はサーバーリストの隣に置きます。`manifest.json`なら`manifest.sig.json`です。`.json`で終わらないアドレスなら、末尾に`.sig.json`を足した場所に置きます。server-setupのスクリプトは、サーバーリストを書き換えるたびに署名を作り直します。

```json
{
  "format": "infra-launcher-signature-1",
  "public_key": "<公開鍵。P-256 の SubjectPublicKeyInfo を base64 にしたもの>",
  "signature": "<manifest.json のバイト列そのものに対する ECDSA P-256 / SHA-256 の署名。r と s を並べた 64 バイトを base64 にしたもの>",
  "signed_at": "2026-09-29T20:00:00+09:00"
}
```

確認コードは、公開鍵（base64を戻したバイト列）のSHA256の先頭10バイトです。16進数の大文字にして、4文字ずつ`-`で区切ります。

ランチャーは、ユーザーが確認コードを登録した公開鍵を覚えます。以後は、その鍵の正しい署名がなければ読み込みません。署名はmanifest.jsonのバイト列に対して作るので、署名したあとは1バイトも変えないでください。

paksetと本体のファイル一覧やzipは、manifest.jsonに書いたSHA256で結び付いています。そのため、manifest.jsonの署名を確かめれば、配っているファイルすべてを確かめたことになります。本体（engine）の自動インストールは、確認コードを登録したリストに限ります。
