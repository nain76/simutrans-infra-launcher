# Simutrans インフラ整備ランチャー

Simutrans のマルチプレイ運用を楽にするための外部ツールです。
**Simutrans 本体とは独立しています。** 本体のソースやビルド（Makefile / CMakeLists.txt / Visual Studio プロジェクト）には含まれず、本体のコードも変更しません。

![ランチャーの画面](docs/screenshot.png)

## やりたいこと

1. **pakset の自動同期** — サーバーと同じ pakset・本体ビルドを自動でダウンロード、展開する
2. **高機能サーバーランチャー** — 接続人数・稼働状況・お知らせを表示し、選ぶだけで同期→起動→接続まで済ませる
3. **サーバー構築の自動化** — PowerShell で Windows Server 上のサーバー構築を簡単にする（未着手）

詳しくは [設計メモ](docs/design-spec.md) を見てください。

## フォルダ構成

| フォルダ | 中身 |
|---|---|
| `docs/` | 設計メモなどのドキュメント |
| `manifest/` | サーバー一覧マニフェストの形式（JSON Schema）とサンプル → [説明](manifest/README.md) |
| `launcher/` | ランチャー本体（C# / .NET 10 / Avalonia） |
| `server-setup/` | サーバー構築用 PowerShell スクリプト（予定） |

### launcher の中身

| プロジェクト | 役割 |
|---|---|
| `src/InfraLauncher.Core` | マニフェストの読み込み、同期、起動コマンドの組み立て（画面に依存しない） |
| `src/InfraLauncher.App` | 画面（Avalonia） |
| `src/InfraLauncher.Cli` | コマンドライン版（動作確認・トラブル調査用） |
| `tests/InfraLauncher.Core.Tests` | Core のテスト（xUnit） |

## ビルドと実行

[.NET 10 SDK](https://dotnet.microsoft.com/download) が必要です。

```sh
cd tools/infra-launcher/launcher
dotnet build
dotnet test

# 画面版
dotnet run --project src/InfraLauncher.App

# コマンドライン版
dotnet run --project src/InfraLauncher.Cli -- list   https://example.ddns.net/manifest.json
dotnet run --project src/InfraLauncher.Cli -- sync   https://example.ddns.net/manifest.json friends-a
dotnet run --project src/InfraLauncher.Cli -- launch https://example.ddns.net/manifest.json friends-a --print-only
```

友人に配る実行ファイル（.NET のインストール不要）を作るには:

```sh
dotnet publish src/InfraLauncher.App -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true -p:PublishTrimmed=true -p:DebugType=none -o publish
```

`publish/InfraLauncher.exe`（約21MB）の1ファイルだけで動きます。`PublishTrimmed` で使わないコードを削って小さくしています。Core の JSON 処理はこれに対応するためソース生成を使っているので、JSON で読み書きする型を増やしたら `Json.cs` の `JsonContext` にも追加してください。

## 動き

1. 設定に登録したマニフェストの URL からサーバー一覧を読む
2. サーバーを選んで「同期して接続」を押すと、本体と pakset の SHA256 を記録（`installed.json`）と比べ、違うものだけをダウンロードする。ダウンロードしたものは SHA256 を確かめてから展開する
3. `simutrans -objects <pak>/ -noaddons -load net:<host>:<port>` で起動して接続する

ランチャーのデータは `%LOCALAPPDATA%\InfraLauncher`（Linux は `~/.local/share/InfraLauncher`）に置かれます。

- `settings.json`: 設定
- `installed.json`: 展開したものの記録
- `simutrans/<revision>/`: 本体と pakset

ユーザーが自分で入れた pakset フォルダを置き換えるときは、消さずに `<フォルダ名>.backup-<日時>` に名前を変えて残します。
