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
| `server-setup/` | サーバー側の PowerShell スクリプト（配信サーバーの構築、HTTPS 化、pakset と本体の公開） → [説明](server-setup/README.md) |

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

## 使い方

画面上の「追加」「編集」「削除」でサーバー一覧を管理します。

- **追加**: 次のどちらかを選びます
  - **サーバー管理者から共有されたリストを追加**: 管理者から教えてもらった配信アドレス（`https://…/manifest.json` など）か、受け取ったファイルを指定します。リストのサーバーがまとめて表示され、pakset や本体は自動で同期されます
  - **プロファイルを手動で設定**: 接続先のアドレス・pakset のフォルダ・simutrans 本体を自分で指定します。自動同期はしません
- **編集 / 削除**: 一覧で選んだものを編集・削除します。共有されたリストのサーバーは内容を管理者が管理しているので、編集ではリストの表示名と配信アドレスを変え、削除ではリストごと消します
- **☆ / ★**: お気に入りの印です。印を付けたサーバーは一覧の上に並び、「お気に入りだけ表示」で絞り込めます
- **設定**: 共有されたリストにこの PC 用の simutrans 本体が含まれていないときに使う、手元の simutrans 本体を指定します

画面では、マニフェストのことを「サーバーリスト」と呼んでいます。

## 動き

1. 共有されたサーバーリスト（マニフェスト）を読んで一覧を作る
2. 共有リストのサーバーを選んで「同期して接続」を押すと、足りないものだけをダウンロードする。ダウンロードしたものは SHA256 を確かめてから置く
   - zip 方式: 本体と pakset の zip の SHA256 を記録（`installed.json`）と比べ、違えば zip を丸ごと入れ替える
   - ファイル一覧方式: pakset のファイルを1つずつ一覧と照合し、変わったファイルだけを落とす。一覧にないファイルは片付ける
   - 本体: サーバーリストに本体があり、サーバーリストを HTTPS で取得した場合だけ、サーバーと同じ本体を入れる。手元のほかのフォルダに同じ中身の pakset ファイルがあればコピーして使う
3. `simutrans -objects <pak>/ -noaddons -load net:<host>:<port>` で起動して接続する（手動プロファイルは 2 を飛ばす）

ランチャーのデータは `%LOCALAPPDATA%\InfraLauncher`（Linux は `~/.local/share/InfraLauncher`）に置かれます。

- `settings.json`: 設定（サーバーリスト、手動プロファイル、お気に入り）
- `installed.json`: 展開したものの記録
- `simutrans/<revision>/`: 本体と pakset
- `indexes/`: 取得したファイル一覧

ユーザーが自分で入れた pakset フォルダを置き換えるときは、消さずに `<フォルダ名>.backup-<日時>` に名前を変えて残します。
