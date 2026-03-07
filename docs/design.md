# RadiKeep Canary 設計メモ

## 1. 目的

- RadiKeep が依存する外部Webサービス（radiko / らじる★らじる）の仕様変更を早期検知する。
- 検知対象は「取得スキーマの破損」と「録音経路の実動作」。
- 本体アプリには Canary 機能を入れず、別リポジトリで定期実行する。

## 2. 方針

- Canary は別リポジトリ（`RadiKeep.Logic.Canary`）で管理。
- `vendor/RadiKeep` を submodule として参照し、`RadiKeep.Logics` を直接呼び出す。
- 録音チェックは独自ffmpeg実装を使わず、Logic 実装（`RecordingSource` + `MediaTranscodeService`）経由に統一する。
- 実行基盤は GitHub-hosted runner（`ubuntu-latest`）を基本とする。
- 日本IP制約があるため、Tailscale Exit Node 経由で外向き通信を自宅回線へ迂回する。

## 3. 実行環境（GitHub-hosted + Tailscale）

- Runner: GitHub-hosted `ubuntu-latest`
- タイムゾーン: `Asia/Tokyo`
- 必須:
  - .NET 10 SDK/Runtime
  - `ffmpeg`
  - Tailscale 接続（OAuth + Exit Node）
- ネットワーク:
  - Runner 自体は海外リージョンを含み得る
  - Tailscale Exit Node により日本国内回線からの外向き通信として実行

## 4. リポジトリ構成

```text
RadiKeep.Logic.Canary/
  vendor/
    RadiKeep/                 # submodule
  src/
    Canary.Runner/            # 判定CLI
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
- `WARN` は一時的通信障害（timeout/DNS/接続失敗など）に限定
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
  - `--exit-node=<TS_EXIT_NODE>` を指定して egress を固定
- 生成物:
  - `results/status.json`
  - `logs/*.log`
  - `logs/*_programs.json`（番組表取得データ）
- Artifact:
  - `workflow_dispatch`: 成功/失敗に関わらずアップロード
  - `schedule`: WARN/FAIL時のみアップロード
  - `retention-days: 3`
  - 対象は `results/status.json` と `logs/**`
  - 録音ファイルは著作権配慮のためアップロード前に削除
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

## 9. 運用

- submodule は固定コミットで参照し、Canary結果の再現性を確保する。
- RadiKeep 側更新時は submodule 更新PRで追従する。
- 障害時は `status.json` と `logs` を一次情報として調査する。
