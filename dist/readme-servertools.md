# はじめにお読みください（サーバー管理者向け）

このzipには、Simutransのサーバーに友人のランチャーがpaksetと本体を取りに来るための、配信サーバーを作って管理する道具が入っています。Windows Server 2019以降で使います。

## 中身

| フォルダ・ファイル | 内容 |
|---|---|
| `server-setup/` | 構築と公開のスクリプト（バッチファイルをダブルクリックして使う） |
| `server-setup/Simutrans_ServerManager.exe` | サーバー管理ツール。上のスクリプトを画面から使える |
| `docs/quickstart-server.md` | 初めての構築と、ふだんの作業の手順 |
| `docs/faq-server.md` | よくある質問 |
| `docs/security.md` | 安全のための仕組み |
| `manifest/` | サーバーリストの形式の説明（ふだんは読まなくてよい） |

## 始め方

`server-setup`フォルダをWindows Serverにコピーし、`docs/quickstart-server.md`の手順で進めてください。最初は`Setup-Server.bat`をダブルクリックするか、`Simutrans_ServerManager.exe`を開いて「新規構築を始める」を押します。

構築が終わったら、表示される確認コードを書き留め、署名の鍵のバックアップ（`signing-key-backup.json`）をVPSの外に保管してください。友人には、サーバーリストのアドレスと確認コードを、DiscordのDMなど配信サーバーとは別の経路で伝えます。
