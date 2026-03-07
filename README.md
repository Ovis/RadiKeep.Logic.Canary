# RadiKeep.Logic.Canary

RadiKeep が依存する外部サービス変更を検知する Canary 実行リポジトリです。

## 構成

- `vendor/RadiKeep`: RadiKeep submodule
- `src/Canary.Runner`: Canary 実行CLI
- `.github/workflows/canary.yml`: GitHub-hosted runner + Tailscale Exit Node 実行ワークフロー
- `docs/design.md`: 設計メモ
- `docs/spec.md`: チェック仕様

## 現在のチェック

- `C000_FFMPEG`
- `C001_RADIKO_DAILY_FETCH`
- `C002_RADIRU_DAILY_FETCH`
- `C010_RADIKO_LOGIN`
- `C003_RADIKO_REALTIME_RECORD`
- `C003_RADIRU_REALTIME_RECORD`
- `C004_RADIKO_TIMEFREE_RECORD`
- `C005_RADIRU_ONDEMAND_RECORD`

## 結果コード

- `0`: PASS
- `1`: WARN
- `2`: FAIL

## セットアップ

1. submodule 初期化
```powershell
git submodule update --init --recursive
```

2. 必要な Secrets 設定
- Tailscale
  - `TS_OAUTH_CLIENT_ID`
  - `TS_OAUTH_SECRET`
  - `TS_EXIT_NODE`（推奨: Tailscale IP `100.x.y.z`）
- Canary
  - `DISCORD_WEBHOOK_URL`
  - `RADIKO_USER_ID`（必要時）
  - `RADIKO_PASSWORD`（必要時）

3. workflow_dispatch 入力
- `radiko_station_id`
- `radiru_area_id`
- `radiru_station_id`
- `realtime_record_seconds`
- `timefree_record_seconds`

4. ローカル実行（雛形）
```powershell
dotnet run --project src/Canary.Runner/Canary.Runner.csproj -- --status-json results/status.json --log-dir logs --record-output-dir artifacts/recordings
```

## Artifact 方針

- `workflow_dispatch`（手動実行）: 成功時もArtifactアップロード
- `schedule`（定期実行）: WARN/FAIL時のみArtifactアップロード
- 保持期間: 3日
- アップロード対象: `results/status.json`, `logs/**`
- 録音ファイルは著作権配慮のためアップロード前に削除
