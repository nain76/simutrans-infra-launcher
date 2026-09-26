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
     └─ pak128.japan\                ← Publish-Pakset.ps1 が作る
          ├─ index.json
          ├─ web.config
          └─ （pakset のファイル）
```

友人がランチャーに入れるアドレスは `http://<サーバーのドメイン>:8080/manifest.json`（例: `http://example.ddns.net:8080/manifest.json`）です。

## 最初に1回だけやること（ツールで自動構築）

1. この `server-setup` フォルダを Windows Server の好きな場所にコピーする
2. **`Setup-Server.bat` をダブルクリック**し、「このアプリがデバイスに変更を加えることを許可しますか？」で「はい」を押す
3. 質問に答える
   - ランチャーに表示するサーバー名
   - 友人が接続に使うドメイン（例: `example.ddns.net`）
   - simutrans サーバーのポート（起動時の `-server` に付ける番号。1台目は 13353）
   - simutrans サーバーが使っている **pakset フォルダのフルパス**（例: `C:\simutrans-server\pak128.japan`）。エクスプローラーでフォルダを Shift＋右クリック →「パスのコピー」で貼り付けられます
4. 最後に表示される「残りの作業」を行う
   - 外から **TCP 8080 番**に届くようにする（下の「8080 番を外から届くようにする」を参照）
   - 自分の PC のブラウザで `http://<ドメイン>:8080/manifest.json` が開けるか確かめる（外から届くかを確かめるため、サーバー自身ではなく自分の PC で開く）
   - 友人にそのアドレスを伝える。友人はランチャーの「追加」→「サーバー管理者から共有されたリストを追加」に入れる

ツール（`Install-DistServer.ps1`）は次のことをします。何度実行しても同じ状態になるので、途中で失敗したらもう一度実行してください。

1. IIS（Windows 標準の Web サーバー機能）を入れる。再起動が必要と言われたら、再起動してからもう一度実行する
2. 公開フォルダ `C:\simutrans-dist` を作り、ポート 8080 で公開する（フォルダの中身の一覧表示はオフ）
3. Windows ファイアウォールでポート 8080（と simutrans サーバーのポート）を開ける
4. **読み出し（GET / HEAD）以外の要求を断る**設定を入れる。友人を含め、外からファイルを書き換えたり消したりすることはできない
5. サーバーリスト `manifest.json` がなければ作って1台目を登録し、pakset を公開する。あればそのまま使い、登録済みの pakset を公開し直す。答えた内容は `publish-settings.json` に残り、次からは `Publish-Pakset.bat` だけで公開し直せる
6. サーバーリストを取得できること、書き込み要求が断られることを確かめる

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

### 8080 番を外から届くようにする

Windows のファイアウォールはツールが開けます。そのほかに必要な設定は、サーバーの置き場所で変わります。
simutrans の 13353 番を開けたのと同じ場所に、8080 番を足すと考えてください。

| 置き場所 | やること |
|---|---|
| **VPS** | 事業者の管理画面にサーバー手前のファイアウォールがあれば、TCP 8080 を許可する（ConoHa の「セキュリティグループ」、さくらの VPS・Xserver VPS の「パケットフィルター」など）。その仕組みがなければ何もしない。VPS にはルーターがないので、ポート転送は要らない |
| **自宅など、ルーターの内側** | ルーターの「ポート転送」（「ポートマッピング」「静的 IP マスカレード」とも呼ぶ）で、TCP 8080 をこのサーバーへ転送する |

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

## できればやること: HTTPS にする

HTTP のままでも動きます。ただし通信経路の途中で `manifest.json` を書き換えられると、ハッシュの確認をすり抜けて別のファイルを配られるおそれがあります。
その場合でも、実行ファイルなど（`.exe`、`.dll` など）はランチャーが受け付けません。

無料の証明書（Let's Encrypt）を IIS に設定するツール [win-acme](https://www.win-acme.com/) を使えば、DDNS のドメインでも HTTPS にできます。
その場合は IIS のサイトに HTTPS のバインドを追加してそのポートも開け、友人には `https://…/manifest.json` を伝えます。

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
