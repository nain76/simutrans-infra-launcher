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
└─ C:\simutrans-dist\                ← IIS で公開するフォルダ（新しく作る）
     ├─ manifest.json                ← サーバーリスト（手で作る。以降の書き換えはスクリプト）
     └─ pak128.japan\                ← Publish-Pakset.ps1 が作る
          ├─ index.json
          ├─ web.config
          └─ （pakset のファイル）
```

友人がランチャーに入れるアドレスは `http://<サーバーのドメイン>/manifest.json`（例: `http://example.ddns.net/manifest.json`）です。

## 最初に1回だけやること

以下のコマンドは、PowerShell を「管理者として実行」して入力します。

### 1. IIS を入れる

```powershell
Install-WindowsFeature Web-Server, Web-Static-Content -IncludeManagementTools
```

### 2. 公開フォルダを作り、IIS で公開する

```powershell
New-Item -ItemType Directory C:\simutrans-dist
Import-Module WebAdministration

# 初期設定のサイト（Default Web Site）を止めて、公開フォルダ用のサイトを作る
Stop-Website "Default Web Site"
New-Website -Name simutrans-dist -PhysicalPath C:\simutrans-dist -Port 80
```

IIS は初期設定で「フォルダの中身の一覧表示」がオフなので、公開フォルダのファイル名を一覧で見られることはありません。

### 3. ポートを開ける

```powershell
New-NetFirewallRule -DisplayName "simutrans-dist (HTTP)" -Direction Inbound -Protocol TCP -LocalPort 80 -Action Allow
```

ルーターでも、simutrans の 13353 番と同じように、**TCP 80 番**をこのサーバーへ転送します。

プロバイダーによっては 80 番が使えないことがあります。その場合は `New-Website` の `-Port` とファイアウォール・ルーターの設定を 8080 などに変え、友人には `http://example.ddns.net:8080/manifest.json` を伝えます。

### 4. サーバーリストを作る

[manifest.template.json](manifest.template.json) を `C:\simutrans-dist\manifest.json` にコピーし、次の項目を書き換えます。

| 項目 | 書く内容 |
|---|---|
| `id` | 英数字の識別子。あとで変えない（例: `friends-a`） |
| `name` | ランチャーに表示されるサーバー名 |
| `address` | simutrans サーバーの接続先（例: `example.ddns.net:13353`） |
| `message` | お知らせ（空でもよい） |
| `pakset.name` / `pakset.folder` | pakset の名前と、フォルダ名（例: `pak128.japan`） |

メモ帳で保存するときは、文字コードを「UTF-8」にしてください。

### 5. pakset を公開する

```powershell
cd <このフォルダ（server-setup）の場所>
.\Publish-Pakset.ps1 `
    -Source      C:\simutrans-server\pak128.japan `
    -Destination C:\simutrans-dist\pak128.japan `
    -Manifest    C:\simutrans-dist\manifest.json `
    -ServerId    friends-a
```

スクリプトの実行が止められた場合は、先に次を実行してください（この PowerShell の画面の中だけ許可します）。

```powershell
Set-ExecutionPolicy -Scope Process Bypass
```

### 6. 確認して友人に伝える

1. 自分の PC のブラウザで `http://example.ddns.net/manifest.json` を開き、中身が表示されることを確かめる（サーバー自身からだと、ルーターの都合で開けないことがあります）
2. 友人にそのアドレスを伝える
3. 友人はランチャーで「追加」→「サーバー管理者から共有されたリストを追加」を選び、そのアドレスを入れる

## ふだんの作業

| やりたいこと | やること |
|---|---|
| アドオンを足す・入れ替える | ① `C:\simutrans-server\pak128.japan` に pak をコピー → ② simutrans サーバーを再起動 → ③ 手順5のスクリプトを実行 |
| アドオンを外す | ① pak を消す → ② 再起動 → ③ 手順5のスクリプトを実行（外したアドオンを使っているセーブデータは読めなくなることがあります） |
| お知らせや状態を変える | `manifest.json` の `message` や `status`（`online` / `offline` / `maintenance`）を書き換える |
| サーバーを増やす | `manifest.json` の `servers` に項目を足し、そのサーバー用に手順5を実行する（`-ServerId` を変える） |

友人のランチャーは、次に接続するときに変わったファイルだけを自動で落とします。

## できればやること: HTTPS にする

HTTP のままでも動きます。ただし通信経路の途中で `manifest.json` を書き換えられると、ハッシュの確認をすり抜けて別のファイルを配られるおそれがあります。
その場合でも、実行ファイルなど（`.exe`、`.dll` など）はランチャーが受け付けません。

無料の証明書（Let's Encrypt）を IIS に設定するツール [win-acme](https://www.win-acme.com/) を使えば、DDNS のドメインでも HTTPS にできます。
その場合はポート 443 も開け、友人には `https://…/manifest.json` を伝えます。

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
