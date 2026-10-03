# CLAUDE.md

Simutransのマルチプレイ用ランチャー（友人のPC向け）と、配信サーバーの構築・管理ツール（Windows Server向け）のリポジトリ。もとは本体のリポジトリ nain76/TID_simutrans の `tools/infra-launcher` にあり、2026-10-03に履歴ごと移した。

## 会話と文章の約束

- 返事は必ず日本語で書く。コミットメッセージも日本語。
- 文章は yomiyasu（https://github.com/nanaism/yomiyasu）の書き方に合わせる。太字と箇条書きは少なめにして、ふつうの文章で説明する。英単語の前後に半角スペースを入れない。絵文字と、行末のコロンは使わない。
- ドキュメント（README、docs）も同じ書き方。利用者は日本語話者で、技術に詳しくない友人も読む。
- 作業したらコミットしてpushする。

## 守ること（ユーザーの方針）

- Simutrans本体のコードは変更しない。このツールは外から本体を起動するだけ。
- 安全でない機能は入れない。迷ったら安全なほうを選ぶ。
- IISは読み出し（GET・HEAD）以外をすべて断る。プログラムを動かす仕組みは入れない。
- nettool、バッチファイル、スクリプト、ほかのexe、セーブデータ、パスワードのハッシュは絶対に配らない。
- ユーザーが確かめないまま知らないexeを実行することがないようにする（特に初回インストール）。本体は署名を確かめたサーバーリストからだけ入れ、初回起動時に配布元とSHA256を見せて承認してもらう。
- 接続先のIPやドメインを画面で目立たせない（配信や画面共有で見えるため）。ランチャーの一覧ではふだん隠し、「表示」「コピー」で扱う。
- サーバー側は、今動いている環境を崩さないことを優先する。書き換えはPowerShellのスクリプトに任せ、サーバー管理ツール（GUI）はスクリプトを呼ぶだけにする。以前の版で構築した環境（manifest.json の名前など）もそのまま動くようにする。

## 構成

| 場所 | 中身 |
|---|---|
| `launcher/src/InfraLauncher.Core` | サーバーリストの読み込みと署名の確認、同期、起動コマンド、管理ツール用の読み取り（`Admin/ServerSetup.cs`） |
| `launcher/src/InfraLauncher.App` | ランチャーの画面（Avalonia）。exe名は `Simutrans_Launcher.exe` |
| `launcher/src/InfraLauncher.ServerManager` | サーバー管理ツール（Avalonia、管理者として動く）。exe名は `Simutrans_ServerManager.exe` |
| `launcher/src/InfraLauncher.Cli` | コマンドライン版（調査用） |
| `launcher/tests/InfraLauncher.Core.Tests` | xUnit のテスト |
| `server-setup/` | サーバー側のPowerShellスクリプトとバッチファイル |
| `manifest/` | サーバーリストとファイル一覧のJSON Schemaとサンプル |
| `docs/` | クイックスタートとQ&A（遊ぶ人向け、管理者向け）、安全性の設計、設計メモ |

server-setup のバッチファイルと、中で動くスクリプトの対応は次のとおり。

| バッチファイル | スクリプト | 用途 |
|---|---|---|
| Setup-Server.bat | Install-DistServer.ps1 | 初回構築と設定の点検。サーバーリストがなければ Add-Server.ps1 を呼ぶ |
| Add-Server.bat | Add-Server.ps1 | サーバーを足す。paksetと本体の公開、署名まで行う |
| Publish-Pakset.bat | Publish-Pakset.ps1 | 登録済みのpaksetと本体（Publish-Engine.ps1）を公開し直して署名 |
| Enable-Https.bat | Enable-Https.ps1 | win-acmeでLet's Encryptの証明書を取り、8443でHTTPS |
| Manage-SigningKey.bat | Manage-SigningKey.ps1 | 確認コードの表示、バックアップ、復元、署名し直し、鍵の作り直し |
| Rename-ServerList.bat | Rename-ServerList.ps1 | サーバーリストとpaksetの公開フォルダの名前のランダムな部分を変える |
| （なし） | Edit-ServerList.ps1 | 表示名、お知らせ、メンテナンス中を書き換えて署名。管理ツールから呼ぶ |

共通の処理は Common.ps1 と Signing.ps1。

## 仕組みの要点

- サーバーリスト（`list-ランダム10文字.json`）に ECDSA P-256 で署名し、`list-….sig.json` を隣に置く。サーバーリストに各ファイル一覧（index.json）のSHA256、ファイル一覧に各ファイルのSHA256があり、署名から全ファイルまでつながる。
- 確認コードは公開鍵（SPKI）のSHA256の先頭10バイトを16進20文字にしたもの。ランチャーは画面に出さず、友人が手で入力する。登録時の公開鍵を settings.json に覚える。
- 署名の鍵は `%LOCALAPPDATA%\InfraLauncherServer\signing-key.dat`（DPAPI CurrentUser）。バックアップは PBKDF2-SHA256 30万回、AES-256-CBC、HMAC-SHA256。
- 公開の設定は `server-setup/publish-settings.json`（manifest、share_url、paksets）。サーバーリストの場所は `Resolve-ManifestPath` で決める。新しく決めたランダムな名前はすぐ設定に保存し、続けて呼ばれたスクリプトも同じ名前を使う。
- 本体は許可リスト（`engine-files.default.json`、上書きは `engine-files.json`）の部品だけ配る。`themes/*.tab` のような「フォルダ/名前」は直下のファイルだけ。paksetのフォルダは本体と別に公開する。
- ランチャーの同期は差分ダウンロード。30秒データが来なければ打ち切り、途切れたら3回まで再試行。途中のファイルは `%LOCALAPPDATA%\InfraLauncher\downloads\partial`、記録は `logs/sync.log`。
- `config/simuconf.tab` は同期で上書きしない。プレイヤー名は起動のたびにこのファイルの先頭に印付きで書く（simutransに名前の起動オプションがなく、tabファイルは最初に出たキーを使うため）。

## 技術的な注意点（実際にはまったもの）

Windows PowerShell 5.1（サーバーで使う）

- `Measure-Object -Property {スクリプトブロック}` は使えない。合計は自分で足す。
- `.Directory` や `.Parent` で得たものには PSIsContainer がない。`-is [System.IO.DirectoryInfo]` で判定する。
- 文字列の中で `$var` の直後に日本語を続けると変数名の一部になる。`$($var)` か `${var}` にする。
- .ps1 はBOM付きUTF-8で保存する。.ps1 と .bat は .gitattributes で CRLF。
- .NETのメソッドの文字列引数に `$null` を渡すと空文字に変わる。nullを渡すときは `[NullString]::Value`（CngKey.Create で名前の空の鍵が残る不具合があった）。
- GUIからスクリプトを動かすときは `-OutputFormat Text` を付ける（付けないとCLIXMLが混ざる）。

IIS

- 隠しファイルは配らない（desktop.ini、Thumbs.db はもともと公開しない）。
- ファイル名の `+` には `allowDoubleEscaping="true"` が要る。知らない拡張子には mimeMap が要る。どちらも Write-DistWebConfig が書く。

ランチャー（C#）

- `Progress<T>` の通知は遅れて届くことがある。終わったあとの表示を上書きしないよう、フラグで受け付けを止める。
- OneDriveの中や、消えたフォルダのファイルを使い回そうとして失敗したことがある。存在確認と例外処理を入れている。インストール先はCドライブ直下を勧める。

## ビルド、テスト、配布

この環境では .NET 10 SDK と pwsh を `~/.dotnet` に入れて使っている（`export DOTNET_ROOT=$HOME/.dotnet PATH=$HOME/.dotnet:$HOME/.dotnet/tools:$PATH`）。ない場合は dotnet-install.sh で入れる。

```sh
cd launcher
dotnet build
dotnet test
# 配布用exe（App と ServerManager のそれぞれ）
dotnet publish src/InfraLauncher.App -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true -p:PublishTrimmed=true -p:DebugType=none -o publish
```

- テストは127件。PowerShellのスクリプトはLinuxのpwshで構文と一部の動作を確かめられる。署名の鍵はDPAPIを使うので、Linuxでは環境変数 `INFRA_SIGNING_INSECURE_TEST=1` を付けて試す（テスト専用）。
- 配るzipは2つに分けている（アップロードの上限30MiBを超えるため）。`simutrans_Launcher.zip` はランチャーのexeと遊ぶ人向けの説明、`simutrans_ServerTools.zip` は server-setup 一式（管理ツールのexeを含む）と管理者向けの説明と manifest。
- zipはPythonの zipfile で作り、日本語のファイル名にUTF-8のフラグ（0x800）を立てる。付けないとWindowsで文字化けする。

## 今の状態と、まだWindowsで確かめていないこと

ユーザーのWindows環境で、構築、同期、起動、接続、プレイヤー名の表示までは動いている。HTTPS化と鍵の作り直しも、ユーザーが実行中だった。

次はLinuxでの確認だけで、Windowsではまだ試していない。

- サーバー管理ツールで、作業のボタンがスクリプトをPowerShellの画面で開き、画面を閉じると自動で読み込み直すこと
- 「保存して署名」のあとにサーバーの選択が外れること
- Rename-ServerList.ps1 が、すでにランダムな文字の付いたpaksetの公開フォルダも新しい名前に変えること。そのあと友人のランチャーがpaksetを落とし直さないこと
- 鍵を作り直すときの `[NullString]::Value` の修正と、以前の版が残した鍵の写しを消す処理

そのほか

- `docs/server-manager.png` は古い画面のまま。
- 構成図（AWSの構成図ふう）は claude.ai のArtifactとして作った（リポジトリには入っていない）。
- 元のリポジトリ（TID_simutrans）の作業ブランチ `claude/modest-carson-ln7cuy` では、`tools/infra-launcher` を移転の案内だけにした。main に取り込むかはまだ決めていない。
