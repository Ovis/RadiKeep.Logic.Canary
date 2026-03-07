# RadiKeep.Logic.Canary

RadiKeep が依存する外部サービス変更を検知する Canary 実行リポジトリです。

## 構成

- `vendor/RadiKeep`: RadiKeep submodule
- `src/Canary.Runner`: Canary 実行CLI
- `.github/workflows/canary.yml`: self-hosted runner 実行ワークフロー
- `docs/spec.md`: チェック仕様

## セットアップ

1. submodule 初期化
```powershell
git submodule update --init --recursive
```

2. 必要な Secrets 設定
- `DISCORD_WEBHOOK_URL`
- `RADIKO_USER_ID`（必要時）
- `RADIKO_PASSWORD`（必要時）

3. ローカル実行（雛形）
```powershell
dotnet run --project src/Canary.Runner/Canary.Runner.csproj -- --status-json results/status.json --log-dir logs --record-output-dir artifacts/recordings
```

現在の Runner は bootstrap として `ffmpeg -version` のみ検証します。
C001-C005 は今後段階的に実装します。
