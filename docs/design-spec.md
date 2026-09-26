# Simutrans インフラ整備ランチャー 設計メモ

最終更新: 2026-09-26

## 目的・背景

- 本人はインフラ/サーバー保守エンジニア。趣味でSimutransをプレイし、友人とマルチプレイ用に自前サーバーを運用している（現状Windows Server）。
- Simutrans本体（アプリ開発）ではなく、周辺のインフラ・運用ツールを作ることで、実務スキルを活かしつつ趣味の運用を楽にしたい。
- **制約: Simutrans本体（ゲームエンジン自体）は極力触らない。** 外付けのランチャー/配布ツールとして実装する。

## 解決したい3つの課題（当初案）

1. **pakset不一致がだるい** → Windows側にpaksetを持たせて自動ダウンロード・更新する仕組みが欲しい（ランチャーで自動インストール的に）。
2. **公式の「オンラインゲーム参加」タブより高機能なサーバーランチャーが欲しい**（現状人数表示やお知らせ機能などが弱い）。
3. **サーバー構築の敷居を下げたい**（非エンジニアの友人でも構築できるように）。

議論の結果、1と2は「マニフェスト駆動の同期」という同じ仕組みの表裏であるため、1本のランチャーに統合するのが良いという結論になった。3は同じマニフェスト形式をサーバー構築スクリプトにも流用できる。

## 統合後のアーキテクチャ方針

ランチャー起動時の流れ:

1. ランチャーがサーバー一覧マニフェスト（JSON）を取得する。ホスト元はWindows Server上の簡易静的ファイル配信 or 軽量HTTP APIでよい（GitHub Releasesでも可）。
2. マニフェストには各サーバーごとに: 名前・接続先アドレス・稼働状況・現在の接続人数・使用paksetの情報・**simutrans本体のエンジンビルド情報**を含める（理由は下記「チェックサムに関する重要な知見」を参照）。
3. ユーザーが一覧からサーバーを選択すると、ランチャーはローカルのpakset/エンジンのハッシュとマニフェストの値を比較し、不一致があれば該当ファイルをダウンロード・展開する（差分がなければ何もしない）。
4. 同期完了後、`-objects <folder>/` 等の起動引数を付与してsimutrans本体を起動し、選択したサーバーに接続する。

これにより「サーバーを選ぶ」というワンクリックだけで「pakset自動同期 → 正しい引数で起動 → 接続」が完結する。

### マニフェストスキーマ（案）

```json
{
  "servers": [
    {
      "name": "友達内輪鯖A",
      "address": "example.ddns.net:13353",
      "status": "online",
      "players": 3,
      "message": "今日20時から再開します",
      "engine": {
        "revision": "r1234-2026-09",
        "download_url": "https://.../simutrans-r1234.zip",
        "sha256": "yyyy..."
      },
      "pakset": {
        "name": "pak128.japan",
        "sha256": "xxxx...",
        "download_url": "https://.../pak128.japan-2026.09.01.zip"
      }
    }
  ]
}
```

- `sha256` は**ファイル内容のハッシュ**（ファイル名だけの突合は不十分。同名でも中身が変わるrevisionがあるため）。
- `engine.sha256` / `engine.revision` を含める理由: pakset自体は同一でも、simutrans本体のビルド（コンパイラやrevisionの違い）によってネットワーク接続時のチェックサムが変わりうるため、pakset単体でなく本体側のバージョンも同期対象に含めておくと事故が減る。

## 3つの機能の詳細

### 1. Pakset自動同期
- サーバー側に `manifest.json`（pakset名・SHA256・ダウンロードURL）を配置。
- ランチャーが起動時に取得し、ローカルのpaksetフォルダとハッシュ比較。差分があればzip取得→展開。
- 展開後、`-objects <folder>/` を付けて起動すれば常に正しいpaksetで起動される。

### 2. 高機能サーバーランチャー
- 公式の「オンラインゲーム参加」タブ（内部的には servers.simutrans.org のような公開一覧を参照していると見られる）に依存せず、身内運用向けに自前の軽量ステータスAPIをWindows Server側に立てる。
- 表示したい情報: 現在の接続人数、稼働状況、pakset/エンジンの同期状態、管理者からのお知らせメッセージ、お気に入りサーバーの複数登録・切り替え。
- 「サーバーを選ぶ」だけで同期〜起動〜接続が完結するのが目玉機能。

### 3. サーバー構築の敷居を下げる
- PowerShellスクリプトで「pakset選択→ダウンロード配置→simuconf.tab自動生成→NSSMでWindowsサービス登録→ファイアウォール規則追加」を自動化。
- 1番のマニフェスト形式をサーバー構築側にも流用し、構築後に自分のエントリをマニフェストへ登録する流れにできると発展性がある。
- GUIウィザード（Inno Setupや簡易WPF）でラップすれば非エンジニアでも数クリックで構築可能に。

## 検証済みの技術情報（Web調査で確認したファクト）

- 起動パラメータ: `simutrans -objects <folder>/`（pakset指定）、`simutrans -server [port]`（デディケートサーバー起動）。出典: [Starting Simutrans | Simutrans Tikiwiki](https://simutrans-germany.com/wiki/wiki/en_start_parameter)
- 公式サーバー一覧: [servers.simutrans.org/list](https://servers.simutrans.org/list) — サーバー名/pakset種別/稼働状況/マップサイズ/接続人数などをHTML表示。JSON APIの有無は未確認。
- バニラのSimutransクライアントには、pakset不一致を自動解決する仕組みは公式ドキュメント上確認できなかった（＝この課題は実際に未解決の隙間であり、自作する価値がある）。
- **チェックサムの仕組み**: Simutrans本体が内部で計算するpaksetチェックサムは、生のファイルバイナリやファイル名からではなく、pakをロードした後の「オブジェクトの重要なプロパティ」から計算されている（開発者コメントより）。出典: [Build with MSVC pakset checksum mismatch. - Simutrans forum](https://forum.simutrans.com/index.php?topic=14206.0)
  - 同じpaksetファイルでも、コンパイラ（MSVC vs GCC）の違いで未初期化変数の扱いが異なり、チェックサムがズレた実例が報告されている。
  - → ランチャー側の同期ロジックは、本体の内部アルゴリズムを再実装する必要はなく、「バイト単位で完全一致するファイルを配布する」ことだけを保証すればよい（ファイル内容のSHA256比較で十分）。ただし本体のビルド差異もチェックサム不一致の原因になりうるため、エンジン本体のバージョンもマニフェストで同期対象にしておくと安全。
- 参考になりそうな既存リポジトリ（内容は未精査）: [aburch/simutrans (SVN mirror)](https://github.com/aburch/simutrans)、[lindleyw/simutrans-pak-tools](https://github.com/lindleyw/simutrans-pak-tools)

## 技術スタック候補（未確定）

- ランチャー本体: C#/.NET（WPFやWinForms、配布のしやすさ重視）が第一候補。友人がWindows以外も使うならTauri/Electron等クロスプラットフォームも検討。
- サーバー側ステータスAPI: 軽量なもので十分（Node.js/Express, Python/FastAPI, ASP.NET Core Minimal APIなど）。
- サーバー構築自動化: PowerShell + NSSM（Windowsサービス化）。

## 未決事項 / 次のステップ

- マニフェストのホスティング方法（静的JSON配信 vs 簡易API）を確定する。
- ランチャーの技術スタック（C#/.NETかクロスプラットフォームか）を確定する。
- クライアントから特定サーバーへ直接接続する正確な起動引数は未確認（公式ドキュメントに記載なし）。実装時は [aburch/simutrans](https://github.com/aburch/simutrans) のソースを確認するか、実際の接続時の挙動を観察して確認する必要がある。
- サーバー一覧・pakset・エンジンバージョンをマニフェストとしてどう管理・更新するか（手動更新 vs 自動生成）を決める。
- 実装はClaude Code（CLIツール）側で継続予定。

## 追記: ソースコードで確認した起動引数（2026-09-26）

このリポジトリ（TID_simutrans）のソースを読んで、上の「未決事項」にあった直接接続の起動引数を確認した。

- **特定サーバーへの直接接続**: `simutrans -load net:<host>:<port>`
  - `simmain.cc` の `-load` 処理で、値が `net:` で始まる場合はセーブファイルではなくネットワーク接続先として扱われる。
- **pakset の指定**: `-objects <folder>/`（`simmain.cc` の `-objects` 処理）
- → ランチャーが実行する起動コマンドは次の形になる:

```
simutrans -objects pak128.japan/ -load net:example.ddns.net:13353
```
