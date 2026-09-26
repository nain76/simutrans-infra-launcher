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
   - 友人が接続に使うドメイン（例: `example.ddns.net`）
   - ランチャーに表示するサーバー名
   - pakset のフォルダ名（例: `pak128.japan`）
4. 最後に表示される「残りの作業」を行う
   - ルーターで **TCP 8080 番**をこのサーバーへ転送する（simutrans の 13353 番と同じ要領）
   - 自分の PC のブラウザで `http://<ドメイン>:8080/manifest.json` が開けるか確かめる（サーバー自身からだと、ルーターの都合で開けないことがあります）
   - 友人にそのアドレスを伝える。友人はランチャーの「追加」→「サーバー管理者から共有されたリストを追加」に入れる

ツール（`Install-DistServer.ps1`）は次のことをします。何度実行しても同じ状態になるので、途中で失敗したらもう一度実行してください。

1. IIS（Windows 標準の Web サーバー機能）を入れる。再起動が必要と言われたら、再起動してからもう一度実行する
2. 公開フォルダ `C:\simutrans-dist` を作り、ポート 8080 で公開する（フォルダの中身の一覧表示はオフ）
3. Windows ファイアウォールでポート 8080 を開ける
4. サーバーリスト `manifest.json` がなければ作る。あればそのまま使う
5. `-PaksetSource` を指定した場合は、pakset も公開する
6. 実際にサーバーリストを取得できるか確かめる

pakset の公開まで一度に済ませるなら、PowerShell を管理者として開いて次のように実行します。

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
| `-ServerId` | `friends-a` | サーバーリストに書くサーバーの id |
| `-GamePort` | `13353` | simutrans サーバーのポート |
| `-PaksetSource` | なし | simutrans サーバーが使っている pakset フォルダ。指定すると公開まで行う |

### pakset を公開する（構築のあと）

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
| アドオンを足す・入れ替える | ① `C:\simutrans-server\pak128.japan` に pak をコピー → ② simutrans サーバーを再起動 → ③ `Publish-Pakset.ps1` を実行 |
| アドオンを外す | ① pak を消す → ② 再起動 → ③ `Publish-Pakset.ps1` を実行（外したアドオンを使っているセーブデータは読めなくなることがあります） |
| お知らせや状態を変える | `manifest.json` の `message` や `status`（`online` / `offline` / `maintenance`）を書き換える |
| サーバーを増やす | `manifest.json` の `servers` に項目を足し、そのサーバー用に `Publish-Pakset.ps1` を実行する（`-ServerId` を変える） |

友人のランチャーは、次に接続するときに変わったファイルだけを自動で落とします。

## できればやること: HTTPS にする

HTTP のままでも動きます。ただし通信経路の途中で `manifest.json` を書き換えられると、ハッシュの確認をすり抜けて別のファイルを配られるおそれがあります。
その場合でも、実行ファイルなど（`.exe`、`.dll` など）はランチャーが受け付けません。

無料の証明書（Let's Encrypt）を IIS に設定するツール [win-acme](https://www.win-acme.com/) を使えば、DDNS のドメインでも HTTPS にできます。
その場合は IIS のサイトに HTTPS のバインドを追加してそのポートも開け、友人には `https://…/manifest.json` を伝えます。

---

## Publish-Pakset.ps1 の詳細

| 引数 | 説明 |
|---|---|
| `-Source` | サーバーが使っている pakset フォルダ |
| `-Destination` | 公開フォルダの中の、pakset 用のフォルダ |
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
