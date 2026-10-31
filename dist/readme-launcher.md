# はじめにお読みください（遊ぶ人向け）

このzipには、Simutransのサーバーに参加するためのランチャーが入っています。サーバーと同じsimutrans本体とpaksetを自動でそろえて、サーバーに接続できます。

## 中身

| ファイル | 内容 |
|---|---|
| `launcher/Simutrans_Launcher.exe` | ランチャー本体。好きな場所に置いて使う |
| `docs/quickstart-client.md` | 初めて遊ぶまでの手順 |
| `docs/faq-client.md` | よくある質問 |
| `docs/security.md` | 安全のための仕組み |

## 始め方

サーバー管理者から、サーバーリストのアドレスと確認コードを教えてもらってから、`docs/quickstart-client.md`の手順で進めてください。

ランチャーを初めて開くと「WindowsによってPCが保護されました」と出ることがあります。ランチャーに電子署名がないためです。このzipをサーバー管理者から直接受け取ったときだけ、「詳細情報」を押してから「実行」を押してください。管理者がSHA256を教えてくれた場合は、PowerShellで`Get-FileHash simutrans_Launcher.zip`を実行し、同じ値か確かめてから開いてください。

simutrans本体とpaksetは、何も変えなければ`C:\Games\simutrans`に入ります。
