# RadiCorder.Logic.Canary

RadiCorder が依存する外部サービス変更を検知する Canary 実行リポジトリです。

## 構成

- `vendor/RadiCorder`: RadiCorder submodule
- `src/Canary.Runner`: Canary 実行CLI
- `.github/workflows/canary.yml`: GitHub-hosted runner + Tailscale Exit Node 実行ワークフロー
- `docs/design.md`: 設計メモ
- `docs/spec.md`: チェック仕様

## 現在のチェック

- `C000_FFMPEG`
- `C020_INITIAL_DATABASE_SYNC`
- `C021_INCREMENTAL_DATABASE_SYNC`
- `C006_RADIKO_STATIONS_FETCH`
- `C001_RADIKO_DAILY_FETCH`
- `C002_RADIRU_DAILY_FETCH`
- `C010_RADIKO_LOGIN`
- `C003_RADIKO_REALTIME_RECORD`
- `C004_RADIKO_TIMEFREE_RECORD`
- `C003_RADIRU_REALTIME_RECORD`
- `C005_RADIRU_ONDEMAND_RECORD`
- `C011_RADIKO_LOGOUT`

全国局定義は本体のAPIクライアントで取得・解析し、必須項目を確認する。
ログアウトは全録音チェックの後に専用セッションで行い、録音用の認証キャッシュは使用しない。
本体のDiscord通知・GitHub更新確認・NTP・ブラウザの外部フォント・番組画像の取得と埋め込みはチェック対象に含めない。

局定義と番組表の同期は、空のDBと前回の正常DBに同じ実サービス応答を反映して比較する。
前回DBは `canary-state` Orphan Branchで保持する。初回の増分チェックは明示的にSKIPとし、正常な基準DBを作成する。
詳細・保持対象・リセット方法は [docs/persistent-state.md](docs/persistent-state.md) を参照。

## 結果コード

- `0`: PASS
- `1`: WARN
- `2`: FAIL

## セットアップ

1. submodule 初期化
```powershell
git submodule update --init --recursive
```

補足:
- GitHub Actions の Canary Workflow は実行ごとに `vendor/RadiCorder` を `main` の最新HEADへ更新してから実行する。
- ローカル実行は手元の submodule checkout をそのまま使う。最新 `main` で試す場合は `git submodule update --remote vendor/RadiCorder` を先に実行する。

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
- `reset_state`（前回DBを使わず、全体PASSの場合だけ保存済みDBを置き換える）

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

## 開発時の回帰検証

```powershell
dotnet test tests/Canary.Runner.Tests/Canary.Runner.Tests.csproj --configuration Release
python3 -m unittest discover -s tests/state_storage -v
```

この検証は外部サービス・Tailscale・資格情報を使わず、DI構成、専用DB、配信プロキシ、チェックの判定と出力形式を確認する。
GitHub ActionsのPR検証でも固定コミットのsubmoduleを使って実行する。
実サービスへの取得・認証・録音確認は、従来どおり `Canary Check` ワークフローが日本のネットワーク経由で実施する。

Canaryの実行コードの構成と本体の利用範囲は [docs/design.md](docs/design.md) を参照。
