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
