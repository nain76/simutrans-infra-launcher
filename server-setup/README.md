# サーバー側でやること

友人のランチャーは、インターネット越しに次のファイルを取りに来ます。

- **サーバーリスト**（`manifest.json`）: サーバー名・接続先・pakset の情報
- **pakset のファイル**と、その**ファイル一覧**（`index.json`）

そのため、これらのファイルを「アドレス（URL）で取れる場所」に置く必要があります。
ここでは、**simutrans サーバーを動かしている Windows Server に、Windows 標準の Web サーバー機能（IIS）を追加して置く**方法を説明します。
IIS はフォルダの中のファイルをそのまま配るだけで、プログラムは動かしません。

## 全体像

```
Windows Server
├─ C:\simutrans-server\              ← simutrans サーバー本体（今までどおり）
│    ├─ simutrans.exe
│    └─ pak128.japan\                ← サーバーが使う pakset。アドオンはここに入れる
│
└─ C:\simutrans-dist\                ← IIS でポート 8080 番で公開するフォルダ（構築ツールが作る）
     ├─ manifest.json                ← サーバーリスト（構築ツールが作る。お知らせなどは手で書き換える）
     ├─ engine\                      ← simutrans 本体（版ごとのフォルダ。Publish-Engine.ps1 が作る）
     └─ pak128.japan\                ← Publish-Pakset.ps1 が作る
          ├─ index.json
          ├─ web.config
          └─ （pakset のファイル）
```

友人がランチャーに入れるアドレスは `https://<サーバーのドメイン>:8443/manifest.json`（例: `https://example.ddns.net:8443/manifest.json`）です。
HTTPS にしない場合は `http://<サーバーのドメイン>:8080/manifest.json` ですが、その場合は友人の PC に simutrans 本体が自動で入りません（下の「HTTPS と本体の配布」を参照）。

## 最初に1回だけやること（ツールで自動構築）

1. この `server-setup` フォルダを Windows Server の好きな場所にコピーする
2. **`Setup-Server.bat` をダブルクリック**し、「このアプリがデバイスに変更を加えることを許可しますか？」で「はい」を押す
3. 質問に答える
   - ランチャーに表示するサーバー名
   - 友人が接続に使うドメイン（例: `example.ddns.net`）
   - simutrans サーバーのポート（起動時の `-server` に付ける番号。1台目は 13353）
   - simutrans サーバーが使っている **pakset フォルダのフルパス**（例: `C:\simutrans-server\pak128.japan`）。エクスプローラーでフォルダを Shift＋右クリック →「パスのコピー」で貼り付けられます
4. 「HTTPS にしますか？」と聞かれたら Y（無料の証明書を取ります。80 番を外から届くようにしておく必要があります）
5. 最後に表示される「残りの作業」を行う
   - 外から **TCP 80・8443・simutrans サーバーのポート**に届くようにする（下の「ポートを外から届くようにする」を参照）
   - 自分の PC のブラウザで `http://<ドメイン>:8080/manifest.json` が開けるか確かめる（外から届くかを確かめるため、サーバー自身ではなく自分の PC で開く）
   - 友人にそのアドレスを伝える。友人はランチャーの「追加」→「サーバー管理者から共有されたリストを追加」に入れる

ツール（`Install-DistServer.ps1`）は次のことをします。何度実行しても同じ状態になるので、途中で失敗したらもう一度実行してください。

1. IIS（Windows 標準の Web サーバー機能）を入れる。再起動が必要と言われたら、再起動してからもう一度実行する
2. 公開フォルダ `C:\simutrans-dist` を作り、ポート 8080 で公開する（フォルダの中身の一覧表示はオフ）
3. Windows ファイアウォールでポート 8080（と simutrans サーバーのポート）を開ける
4. **読み出し（GET / HEAD）以外の要求を断る**設定を入れる。友人を含め、外からファイルを書き換えたり消したりすることはできない
5. サーバーリスト `manifest.json` がなければ作って1台目を登録し、pakset と simutrans 本体を公開する。あればそのまま使い、登録済みの pakset を公開し直す。答えた内容は `publish-settings.json` に残り、次からは `Publish-Pakset.bat` だけで公開し直せる
6. サーバーリストを取得できること、書き込み要求が断られることを確かめる
7. HTTPS にする（`Enable-Https.ps1`。聞かれたときに Y と答えた場合）

以前に pakset の名前だけ答えて構築した場合も、もう一度 `Setup-Server.bat` を実行してフルパスを答えれば公開されます（サーバーリストはそのまま使います）。

質問に答える代わりに引数で指定することもできます（PowerShell を管理者として開いて実行）。

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\Install-DistServer.ps1 -PublicHost example.ddns.net -ServerName "友達内輪鯖A" `
    -PaksetSource C:\simutrans-server\pak128.japan
```

| 引数 | 既定値 | 説明 |
|---|---|---|
| `-Port` | `8080` | 配信に使うポート |
| `-DistDir` | `C:\simutrans-dist` | 公開フォルダ |
| `-PublicHost` | （質問する） | 友人が接続に使うドメイン |
| `-ServerName` | （質問する） | ランチャーに表示するサーバー名 |
| `-ServerId` | （自動） | サーバーリストに書くサーバーの id |
| `-GamePort` | （質問する） | simutrans サーバーのポート |
| `-PaksetSource` | （質問する） | simutrans サーバーが使っている pakset フォルダのフルパス |
| `-HttpsPort` | `8443` | HTTPS のポート |
| `-SkipHttps` | なし | HTTPS にしない（simutrans 本体は配れない） |
| `-Email` | なし | 証明書の期限切れなどの連絡先（任意） |

### ポートを外から届くようにする

使うポートは次のとおりです。Windows のファイアウォールはツールが開けます。

| ポート | 使いみち |
|---|---|
| TCP 8443 | HTTPS での配信（友人のランチャーがここを見る） |
| TCP 80 | 無料証明書の取得と更新の確認（Let's Encrypt がアクセスする）。開けたままにする |
| TCP 8080 | HTTP での配信（HTTPS にしない場合） |
| simutrans サーバーのポート | ゲームの接続（例: 13353） |

そのほかに必要な設定は、サーバーの置き場所で変わります。simutrans のポートを開けたのと同じ場所に足すと考えてください。

| 置き場所 | やること |
|---|---|
| **VPS** | 事業者の管理画面にサーバー手前のファイアウォールがあれば、上のポートを許可する（ConoHa の「セキュリティグループ」、さくらの VPS・Xserver VPS の「パケットフィルター」など）。その仕組みがなければ何もしない。VPS にはルーターがないので、ポート転送は要らない |
| **自宅など、ルーターの内側** | ルーターの「ポート転送」（「ポートマッピング」「静的 IP マスカレード」とも呼ぶ）で、上のポートをこのサーバーへ転送する |

## simutrans サーバーを増やすとき

**`Add-Server.bat` をダブルクリック**し、サーバー名・ポート・pakset フォルダのフルパスに答えます。
サーバーリストへの追加、pakset の公開、Windows ファイアウォールでのポート開放まで行います。

```
C:\simutrans-dist\                 ← 8080 番で公開（増やしても1つのまま）
 ├─ manifest.json                 ← サーバーリスト。全サーバー分が入る
 ├─ pak128.japan\                 ← サーバーA が使う pakset
 └─ pak128.japan-2\               ← サーバーB が使う pakset（中身が違う）
```

- 友人が登録するアドレスは `http://<ドメイン>:8080/manifest.json` の1つのままです。ランチャーの一覧に全サーバーが並びます
- **ポートを分けるのは simutrans サーバーだけ**です（例: 13353、13354）。8081 番などを増やす必要はありません
- 同じ pakset フォルダを使うサーバーを足した場合は、公開済みの pakset を共有します
- 中身の違う pakset が同じフォルダ名（例: どちらも `pak128.japan`）の場合は、公開用に `pak128.japan-2` のような別の名前を自動で付けます。友人の PC でも別のフォルダに入るので、サーバーを切り替えても落とし直しになりません（接続時に照合されるのは中身のチェックサムで、フォルダ名は照合されません）
- VPS 事業者のパケットフィルターなどがある場合は、そこでも新しい simutrans サーバーのポートを許可してください

### pakset を公開し直す（構築のあと）

**`Publish-Pakset.bat` をダブルクリック**します。登録したすべての pakset を公開し直します（変わっていないものはすぐ終わります）。
別の設定で公開したいときは、PowerShell で引数を指定して実行します。

```powershell
.\Publish-Pakset.ps1 `
    -Source      C:\simutrans-server\pak128.japan `
    -Destination C:\simutrans-dist\pak128.japan `
    -Manifest    C:\simutrans-dist\manifest.json `
    -ServerId    friends-a
```

スクリプトの実行が止められた場合は、先に `Set-ExecutionPolicy -Scope Process Bypass` を実行してください（その PowerShell の画面の中だけ許可します）。

## ふだんの作業

| やりたいこと | やること |
|---|---|
| アドオンを足す・入れ替える | ① `C:\simutrans-server\pak128.japan` に pak をコピー → ② simutrans サーバーを再起動 → ③ `Publish-Pakset.bat` をダブルクリック |
| アドオンを外す | ① pak を消す → ② 再起動 → ③ `Publish-Pakset.bat` をダブルクリック（外したアドオンを使っているセーブデータは読めなくなることがあります） |
| お知らせや状態を変える | `manifest.json` の `message` や `status`（`online` / `offline` / `maintenance`）を書き換える |
| simutrans サーバーを増やす | `Add-Server.bat` をダブルクリック |

友人のランチャーは、次に接続するときに変わったファイルだけを自動で落とします。

## 安全面

- 8080 番でできるのはファイルの読み出しだけです。構築ツールが「読み出し（GET / HEAD）以外の要求を断る」設定を公開フォルダの `web.config` に入れ、実際に断られることを確かめます
- フォルダの中身の一覧表示はオフです。`web.config` は IIS が配りません。`publish-settings.json` は公開フォルダの外（この `server-setup` フォルダ）にあります
- ファイルを書き換えられるのは Windows Server にログインできる人だけです。リモートデスクトップのパスワードは強くしておいてください

## HTTPS と本体の配布

**サーバーと同じ simutrans 本体を、友人の PC に自動で入れられます。** 友人は simutrans を入れていなくても、ランチャーで「同期して接続」を押すだけで遊べます。サーバーと同じ本体なので、本体の違いによるチェックサムのずれも起きません。

- pakset を公開するとき、**pakset フォルダの1つ上にある本体**も部品ごとに公開します（`Publish-Engine.ps1`。`Add-Server.bat` と `Publish-Pakset.bat` が自動で呼びます）
- どの exe を配るかは次の順で決めます
  1. その simutrans サーバーのポート（`-server 8634` など）で**動いているプロセスの exe**。サーバーの本体を新しい版に入れ替えたときも、次の公開で追従します
  2. `simutrans.exe`、または `simutrans*.exe` / `sim-*.exe`（OTRP の `sim-WinGDI64-OTRPv57_0_1.exe` など）が1つだけならそれ
  3. 決められなければ、フォルダ内の exe を新しい順に並べるので、番号で選びます（空欄なら本体は配りません）
- 配るものは**許可リスト方式**で、**部品（コンポーネント）ごと**に決めます。友人はランチャーの「インストール設定」で「推奨」か「カスタム」を選べます

  | 部品（推奨設定） | 中身 | 扱い |
  |---|---|---|
  | 本体と設定・スクリプト | 選んだ本体の exe、`.dll`、`ai` `config` `font` `scenario` `script` `text` | 必須（友人は外せない） |
  | 音楽 | `music` | 推奨 |
  | テーマ・スキン | `skin` `themes` | 推奨 |
  | ライセンス・説明書 | `license*.txt` `copyright*.txt` `readme*.txt` | 推奨 |

  - 必須: 友人は外せない。推奨: 友人が「推奨」を選ぶと落とす。任意（どちらでもない）: 友人が「カスタム」で選んだときだけ落とす
  - 上にないもの（`maps`、`generated-scripts`、`history.txt` など）は配りません。配るときは部品に足します
  - **絶対に配らないもの**（設定でも変わらない）: `.bat` `.cmd` `.ps1` `.vbs` などのスクリプト、本体以外の exe（Nettool / makeobj / ほかの版）、`.sve`（セーブデータやパスワード）、`settings.xml`、ログ、`save` `screenshot` `addons` フォルダ、pakset のフォルダ
- **カスタム:** [engine-files.default.json](engine-files.default.json) を `engine-files.json` という名前でコピーして、部品（`components`）ごとの `folders` と `files`（`*` が使える）、`required` / `recommended` を編集します。`engine-files.json` があればそちらを使います
- 公開のたびに、部品ごとのファイル数とサイズ、配らなかったフォルダ、絶対に配らないものを表示するので、確かめてください
- 本体はファイル一覧方式で、`engine\<版>\` に置きます。友人は本体が更新されたときも変わったファイルだけを落とします
- 中身が前回と同じなら置き直しません。どのサーバーも使わなくなった古い版は消します
- 本体は Windows 版だけです。Mac や Linux の友人は、手元の simutrans をランチャーの「設定」で指定します

**本体を配るには HTTPS が必要です。** 本体は実行ファイルなので、HTTP のままだと通信の途中でサーバーリストと本体をすり替えられ、友人の PC で好きなプログラムを動かされるおそれがあります。
そのためランチャーは、**HTTPS で取得したサーバーリストからしか本体を入れません**（HTTP のサーバーリストでは pakset だけを同期し、本体は手元のものを使います）。

HTTPS にするには、構築ツールで「HTTPS にしますか？」に Y と答えるか、あとから **`Enable-Https.bat` をダブルクリック**します。

- 無料の証明書（Let's Encrypt）を、証明書を取るツール [win-acme](https://www.win-acme.com/) で取得して IIS に設定します
- win-acme は GitHub から入れます。公開されている SHA256 と電子署名を確かめてから使います
- 証明書は win-acme が自動で更新します（タスクスケジューラに登録されます）
- 取得と更新のたびに Let's Encrypt が `http://<ドメイン>/`（80 番）にアクセスして確認するので、**80 番は開けたままにしてください**
- HTTPS にしたら、友人には `https://<ドメイン>:8443/manifest.json` を伝え、ランチャーの「編集」で配信アドレスを変えてもらいます

---

## Publish-Pakset.ps1 の詳細

| 引数 | 説明 |
|---|---|
| `-Source` | サーバーが使っている pakset フォルダ。省略すると `publish-settings.json` の値 |
| `-Destination` | 公開フォルダの中の、pakset 用のフォルダ。省略すると `publish-settings.json` の値 |
| `-Manifest` | 書き換えるサーバーリスト。省略するとファイル一覧だけ作る |
| `-ServerId` | サーバーリストの中で書き換えるサーバーの `id`。同じ pakset を使うサーバーが複数あれば、カンマ区切りで並べる |
| `-Version` | 書き込む pakset のバージョン。省略すると日時（例: `2026.09.26-2100`） |
| `-IndexUrl` | `index.json` のアドレス。省略すると、サーバーリストから見た相対パスになる（公開フォルダがサーバーリストと同じフォルダかその下にある場合） |

スクリプトは次のことをします。

1. 変わったファイルだけをコピーする。`-Source` から消えたファイルは公開フォルダからも消す
2. `index.json`（各ファイルのパス・サイズ・SHA256）を書く
3. IIS で `.pak` や `.tab` を配信できるように `web.config` を書く。自分で作った `web.config` がある場合は触らない
4. サーバーリストの `pakset` を `index_url` / `index_sha256` / `version` に書き換える。zip 方式の `url` / `sha256` は消す

実行ファイルなど（`.exe`、`.dll`、`.bat` など）は、警告を出して公開しません。
