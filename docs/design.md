# RadiCorder Canary 設計メモ

## 1. 目的

- RadiCorder が依存する外部Webサービス（radiko / らじる★らじる）の仕様変更を早期検知する。
- 検知対象は「番組表取得スキーマの破損」と「録音経路の実動作」。
- 本体アプリには Canary 機能を入れず、別リポジトリで定期実行する。

## 2. 方針

- Canary は別リポジトリ（`RadiCorder.Logic.Canary`）で管理する。
- `vendor/RadiCorder` を submodule として参照し、`RadiCorder.Logics` を直接呼び出す。
- `Canary.Runner` は `RadiCorder.Logics` の公開IFに追従する。RadiCorder 側の更新時は submodule 更新と Runner 側ビルド確認をセットで行う。
- 録音チェックは独自実装ではなく、Logic 実装（`RecordingSource` + `MediaTranscodeService`）経由に統一する。
- 実行基盤は GitHub-hosted runner（`ubuntu-latest`）を基本とする。
- 日本IP制約があるため、Tailscale Exit Node 経由で外向き通信を自宅回線へ迂回する。

## 3. 実行環境

- Runner: GitHub-hosted `ubuntu-latest`
- タイムゾーン: `Asia/Tokyo`
- 必須:
  - .NET 10 SDK/Runtime
  - `ffmpeg`
  - Tailscale 接続（OAuth + Exit Node）
- ネットワーク:
  - Runner 自体は海外リージョンを含み得る
  - Tailscale Exit Node により日本国内回線からの外向き通信として実行する

## 4. リポジトリ構成

```text
RadiCorder.Logic.Canary/
  vendor/
    RadiCorder/              # submodule
  src/
    Canary.Runner/
      Program.cs             # CLIエントリポイント
      CanaryExecution.cs     # 実行順序・全体判定
      Inputs/                # CLI入力・エリア・時刻
      Hosting/               # 本体のDI・専用DB・配信HTTPホスト
      Checks/                # 取得・認証・録音の確認と判定
      Recording/             # 対象番組選択・短時間録音用の番組データ
      Reporting/             # status・番組表JSON・ffmpegログ
      Models/                # 結果・検証情報
  tests/
    Canary.Runner.Tests/     # 外部通信を使わない回帰検証
  docs/
    design.md
    spec.md
  .github/workflows/
    canary.yml
```

## 5. チェック対象

- C000: ffmpeg実行可否
- C001: radiko 1日分番組表取得（必須項目スキーマ）
- C002: らじる 1日分番組表取得（必須項目スキーマ）
- C010: radikoログイン
- C003_RADIKO: radiko リアルタイム録音
- C003_RADIRU: らじる リアルタイム録音
- C004: radiko タイムフリー録音
- C005: らじる 聞き逃し録音

## 6. 判定モデル

- 各チェックは `PASS / WARN / FAIL`
- `WARN` は一時的通信障害（timeout / DNS / 接続失敗など）に限定する
- 全体結果:
  - `PASS`: FAIL/WARN なし
  - `WARN`: FAIL なし、WARN あり
  - `FAIL`: 1件以上 FAIL
- プロセス終了コード:
  - `0`: PASS
  - `1`: WARN
  - `2`: FAIL

## 7. GitHub Actions 設計

- トリガー:
  - `schedule`（1日2回）
  - `workflow_dispatch`
- ランナー:
  - `runs-on: ubuntu-latest`
  - `tailscale/github-action@v4` で tailnet 参加
  - `--exit-node=<TS_EXIT_NODE>` を指定して egress を固定する
- 生成物:
  - `results/status.json`
  - `logs/*.log`
  - `logs/*_programs.json`（番組表取得データ）
- Artifact:
  - `workflow_dispatch`: 成功/失敗に関わらずアップロード
  - `schedule`: WARN/FAIL時のみアップロード
  - `retention-days: 3`
  - 対象は `results/status.json` と `logs/**`
  - 録音ファイルは著作権配慮のためアップロードしない
- 通知:
  - 終了コード `!= 0`（WARN/FAIL）時に Discord Webhook 通知

## 8. 入力とSecrets

- 入力（workflow input/env）:
  - `RADIKO_STATION_ID`
  - `RADIRU_AREA_ID`
  - `RADIRU_STATION_ID`
  - `REALTIME_RECORD_SECONDS`
  - `TIMEFREE_RECORD_SECONDS`
- Secrets:
  - `TS_OAUTH_CLIENT_ID`
  - `TS_OAUTH_SECRET`
  - `TS_EXIT_NODE`
  - `RADIKO_USER_ID`
  - `RADIKO_PASSWORD`
  - `DISCORD_WEBHOOK_URL`

## 9. 実装上の注意

- らじる★らじるは `areaId + serviceId` ベースで扱う。`RadiruAreaKind` / `RadiruStationKind` は Runner 側の入力検証や候補選定には使うが、API呼び出しと録音経路の解決は `RadiCorder.Logics` の現行IFに従う。
- 番組表JSONは取得結果の一次証跡として保存する。
- schema検証は「録音に必要な必須項目」に絞り、optional項目は欠落率をログ化する。

## 10. 運用

- GitHub Actions の Canary Workflow では `vendor/RadiCorder` を実行時に `main` の最新HEADへ更新し、その時点の `RadiCorder.Logics` を検証対象にする。
- ローカルでは固定コミットの submodule を使って再現確認できるようにし、必要に応じて `git submodule update --remote vendor/RadiCorder` で最新 `main` を取り込む。
- `RadiCorder.Logics` の公開IF変更で `Canary.Runner` が追従を要する場合があるため、submodule 更新時は Runner 側ビルド確認も行う。
- 障害時は `results/status.json` と `logs` を一次情報として調査する。

## 11. 本体の処理とCanaryの責務

### 本体から利用するもの

- `AddRadiCorderLogics()` によりHTTPクライアント・APIクライアント・repository・番組表処理・録音ソース・変換処理を登録する。各業務クラスのコンストラクタをRunner側で列挙しない。
- 設定には本体の `AppConfigurationService` を使用する。実行専用のSQLite DBをmigrationで作成し、資格情報の保護にも本体の実装を使う。実行ごとのData Protectionプロバイダーは一時鍵を使用する。
- 放送局情報の保存には本体の `StationRepository` を使用する。Runner独自のインメモリrepositoryは使用しない。
- radikoのHTTP配信には `vendor/RadiCorder/RadiCorder/Features/Program/RadikoStreamingEndpoints.cs` をCompile項目でソース参照する。取得・ライブ解決・解析・URL書き換えを含め、本体の実装を使う。HTTP入力やエラー応答も独自実装しない。
- 上記エンドポイントが参照するログカテゴリの型だけを `Hosting/ProgramEndpointsMarker.cs` に用意する。Webプロジェクト全体やフロントエンドのビルドは不要。
- 録音チェックは従来と同じ `IRecordingSource.PrepareAsync` → `IMediaTranscodeService.RecordAsync` の経路を使う。予約監視や録音結果の正式保存を含むアプリ全体の確認ではない。

### Canaryが担当するもの

- 確認対象の局・日時・番組選択、短時間録音用の番組データの準備。
- 必須項目・録音成功・ファイルの存在とサイズの判定。
- チェックID・エラーコード・PASS/WARN/FAIL・終了コード・ログとstatusの出力。
- UI向けの通知には明示的な `CanaryEventPublisher` を登録する。ブラウザへの通知は行わず、チェック結果は既存のログとstatusで報告する。業務処理への `null` 注入や実行時Moqは使用しない。

### 実行時の隔離と寿命

`LogicContext` は本体のDIを構築し、スコープを実行終了まで保持する。DIのスコープと依存関係を起動時に検証する。
`AddRadiCorderBackgroundServices()` と `StartupTask.InitializeAsync()` は呼ばない。予約監視・定期更新・メンテナンスは開始しない。
HTTPサーバーは `127.0.0.1` の空きポートで配信プロキシだけを公開し、開始後にURLを本体の `ILocalApplicationUrlService` へ渡す。

DB・一時ファイル・ffmpeg元ログはOSの一時ディレクトリ配下に実行ごとのフォルダを作成し、本体のDBと分離する。
終了時はHTTPホストを停止し、DIスコープ・ホストを破棄してから、その実行専用フォルダを削除する。
チェックの成果物として指定された録音ファイル・ログ・statusは別途保持し、既存CIが録音ファイルをアップロード前に削除する。

### 本体へ統一したことで変わる内部処理

以前のCanaryにはなかった `recordingStartUtc` を含むライブ開始同期・プレイリストの切り詰めも、本体の配信実装に従う。
HTTPタイムアウトは従来のRunner独自設定ではなく、本体の共通DI設定（15秒）に従う。
これらは本体の実サービス経路を確認するための統一であり、Canaryの判定仕様を変えるものではない。
資格情報の有無によるログイン判定、秒数制限、ファイルサイズの境界、チェック順序と出力形式は維持する。

## 12. 改修の検証と限界

- ReleaseのRunnerビルドと、外部通信を使わない回帰検証を実施する。
- DI起動時に外部取得が始まらないこと、常駐処理が登録されないこと、実行専用DB・資格情報の読み戻し・後処理を確認する。
- 実HTTPホストで本体の配信エンドポイントに要求し、認証・URL拒否・ライブ解決・音声バイト列・上流失敗を確認する。上流応答は固定のHTTPハンドラーを使う。
- らじるの定義取得・APIクライアント・実SQLite・本体の録音ソースを通し、外部応答と変換処理だけを固定して成功・失敗・32768バイト境界を確認する。
- 改修前のstatus fixtureと出力を比較し、CLI・必須項目・WARN分類・ログ退避を確認する。

これらの成功は、実radiko・実らじるの取得や認証・音声取得が成功した証拠ではない。
最終的な確認には、GitHub Actionsの既存Tailscale経路でCanaryを実行し、8チェックの結果とログを確認する。
実サービスの確認処理をスキップしたり、固定応答でPASSに置き換えたりする機能はCLIに追加しない。
