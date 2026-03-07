# RadiKeep Canary 判定仕様（現行）

## 1. 対象

- 本仕様は、別リポジトリ `radikeep-canary` で実行する Canary ジョブの判定ルールを定義する。
- 実行環境は GitHub-hosted runner（`ubuntu-latest`）+ Tailscale Exit Node を前提とする。

## 2. 共通ルール

## 2.1 実行結果ステータス

- `PASS`: すべての必須判定を満たした
- `WARN`: 一時的要因の可能性がある失敗
- `FAIL`: 仕様変更または継続的障害の可能性が高い失敗

## 2.2 WARN 判定ルール

- 一時的通信障害（タイムアウト、DNS、接続失敗等）は `WARN` とする。
- それ以外の失敗は `FAIL` とする。
- 現行実装ではチェック単位の自動再試行は行わない。

## 2.3 成果物

- `results/status.json`
  - ジョブ全体結果、各チェックの結果、失敗コード、メッセージ
- `logs/<check-id>.log`
  - チェック単位の詳細ログ
- `logs/<check-id>_programs.json`
  - C001/C002 の取得番組表データ
- `artifacts/` へのアップロード対象
  - `results/status.json`
  - `logs/**`
  - 録音ファイルは著作権配慮のためアップロード前に削除
- Artifact アップロード条件
  - `workflow_dispatch`: 成功/失敗に関わらずアップロード
  - `schedule`: WARN/FAIL時のみアップロード
  - `retention-days: 3`

## 3. 入力パラメータ

- `RADIKO_STATION_ID`（例: `TBS`）
- `RADIRU_AREA_ID`（例: `JP13`）
- `RADIRU_STATION_ID`（例: `r1`）
- `REALTIME_RECORD_SECONDS`（例: `30`）
- `TIMEFREE_RECORD_SECONDS`（例: `30`）
- `RADIKO_USER_ID` / `RADIKO_PASSWORD`（必要時）
- `TS_OAUTH_CLIENT_ID` / `TS_OAUTH_SECRET` / `TS_EXIT_NODE`

## 4. チェック一覧

## 4.1 C001: radiko 1日分番組表取得

- ID: `C001_RADIKO_DAILY_FETCH`
- 目的: radiko の番組表取得API変更を検知する
- 入力: `RADIKO_STATION_ID`, 実行日の JST 日付
- 手順:
  - 対象局の 1 日分番組表を取得
- 判定:
  - 応答取得成功
  - 番組件数 > 0
  - 各番組で必須項目（`ProgramId`, `StartTime`, `EndTime`, `Title`）が存在
- 失敗コード:
  - `E-C001-NETWORK`
  - `E-C001-EMPTY`
  - `E-C001-SCHEMA`

## 4.2 C002: らじる 1日分番組表取得

- ID: `C002_RADIRU_DAILY_FETCH`
- 目的: らじる★らじる番組表API変更を検知する
- 入力: `RADIRU_AREA_ID`, `RADIRU_STATION_ID`, 実行日の JST 日付
- 手順:
  - 対象エリア・局の 1 日分番組表を取得
- 判定:
  - 応答取得成功
  - 番組件数 > 0
  - 各番組で必須項目（`ProgramId`, `StartTime`, `EndTime`, `Title`）が存在
- 失敗コード:
  - `E-C002-NETWORK`
  - `E-C002-EMPTY`
  - `E-C002-SCHEMA`

## 4.3 C003(radiko): リアルタイム録音検証

- ID: `C003_RADIKO_REALTIME_RECORD`
- 目的: 現在放送中番組の録音経路が正常か検証する
- 入力: `REALTIME_RECORD_SECONDS`
- 手順:
  - 対象局（必要に応じて現在エリア局へフォールバック）から放送中番組を 1 件選択
  - 指定秒数のみ録音実行
- 判定:
  - 録音処理成功
  - 出力ファイル生成
  - 出力サイズ >= 32768 bytes
- 失敗コード:
  - `E-C003-NO-ONAIR`
  - `E-C003-RECORD-EXEC`
  - `E-C003-OUTPUT-MISSING`
  - `E-C003-OUTPUT-TOO-SMALL`

## 4.4 C003(らじる): リアルタイム録音検証

- ID: `C003_RADIRU_REALTIME_RECORD`
- 目的: 現在放送中番組の録音経路が正常か検証する
- 入力: `REALTIME_RECORD_SECONDS`
- 手順:
  - 対象エリア・局の放送中番組を 1 件選択
  - 指定秒数のみ録音実行
- 判定:
  - 録音処理成功
  - 出力ファイル生成
  - 出力サイズ >= 32768 bytes
- 失敗コード:
  - `E-C003-NO-ONAIR`
  - `E-C003-RECORD-EXEC`
  - `E-C003-OUTPUT-MISSING`
  - `E-C003-OUTPUT-TOO-SMALL`

## 4.5 C004: radiko タイムフリー録音検証

- ID: `C004_RADIKO_TIMEFREE_RECORD`
- 目的: radiko タイムフリー録音経路の健全性を検知する
- 入力: `TIMEFREE_RECORD_SECONDS`, `RADIKO_USER_ID`, `RADIKO_PASSWORD`（必要時）
- 手順:
  - 終了済み番組からタイムフリー候補を 1 件選択（必要に応じてエリア局フォールバック）
  - 指定秒数のみ録音実行
- 判定:
  - 録音処理成功
  - 出力ファイル生成
  - 出力サイズ >= 32768 bytes
- 失敗コード:
  - `E-C004-NO-TIMEFREE-CANDIDATE`
  - `E-C004-AUTH`
  - `E-C004-RECORD-EXEC`
  - `E-C004-OUTPUT-MISSING`
  - `E-C004-OUTPUT-TOO-SMALL`

## 4.6 C005: らじる 聞き逃し配信録音検証

- ID: `C005_RADIRU_ONDEMAND_RECORD`
- 目的: らじる★らじる聞き逃し録音経路の健全性を検知する
- 入力: `RADIRU_AREA_ID`, `RADIRU_STATION_ID`
- 手順:
  - 聞き逃しURLあり・期限内の候補番組を 1 件選択
  - 聞き逃し録音実行
- 判定:
  - 録音処理成功
  - 出力ファイル生成
  - 出力サイズ >= 32768 bytes
- 失敗コード:
  - `E-C005-NO-ONDEMAND-CANDIDATE`
  - `E-C005-EXPIRED`
  - `E-C005-RECORD-EXEC`
  - `E-C005-OUTPUT-MISSING`
  - `E-C005-OUTPUT-TOO-SMALL`

## 4.7 C010: radiko ログイン検証

- ID: `C010_RADIKO_LOGIN`
- 目的: radiko資格情報でログイン可能かを検証する
- 入力: `RADIKO_USER_ID`, `RADIKO_PASSWORD`
- 判定:
  - ログイン成功で `PASS`
- 失敗コード:
  - `E-C010-NO-CREDENTIALS`
  - `E-C010-LOGIN`
  - `E-C010-EXCEPTION`

## 5. 全体終了コード

- `0`: 全チェック `PASS`
- `1`: `WARN` を含むが `FAIL` なし
- `2`: 1件以上 `FAIL`

## 6. Discord 通知ルール

- 通知対象: ジョブ終了コードが `1` または `2` の場合
- 必須項目:
  - 実行日時（JST）
  - 失敗チェックID
  - 失敗コード
  - 要約メッセージ
  - Actions Run URL
- 同一失敗コードが連続する場合はメッセージを集約して通知スパムを抑制する。
  - 現行workflowは抑制ロジック未実装（毎回通知）

## 7. 今後の拡張候補

- 取得レスポンスの構造差分（スキーマ）をスナップショット比較で検知
- 連続失敗回数に応じた通知レベル切り替え
- 失敗時の自動 Issue 起票
