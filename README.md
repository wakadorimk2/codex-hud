# Codex HUD

Codexのローカルセッション記録を監視し、現在状態を画面端へ小さく表示するWindows HUDです。

HUDはCodexを操作しません。Hookを状態同期の起点にしません。

## Features

- `%CODEX_HOME%\sessions`以下のJSONLを再帰探索します。
- `state_5.sqlite`を任意の補助根拠として読み取ります。
- FileSystemWatcherと3秒周期の全体再探索で表示集合を同期します。
- 同じセッションIDを一つのランプだけで表示します。
- 表示集合を最近30分、最大64セッションへ制限します。
- セッション0件でもHUDを常駐します。
- 36 DIPのランプ、折返し、位置保存、クリック透過を維持します。
- prompt、回答本文、command、tool input、cwd、生JSONLをStore、ログ、UIへ渡しません。

## Lamp states

| 状態 | 根拠 | 色 |
| --- | --- | --- |
| `Active` | `task_started`、または新しいSQLite活動 | 青 |
| `Listening` | 最近のファイル活動、読取待ち、identity保留 | 紫 |
| `Idle` | 最近の活動なし | 灰 |
| `Completed` | 非silentな`task_complete` | 緑 |
| `Aborted` | `turn_aborted` | 赤 |
| `ReadError` | JSONLの読取エラー | 赤橙 |

`Listening`は承認待ちまたは質問待ちを断定しません。

`Active`と`Listening`だけが弱く動きます。その他の状態は短い遷移後に安定します。

## Current implementation

```text
sessions/**/*.jsonl ─┐
                     ├─> SessionMonitorEngine ─> MainWindow ─> SkiaSharp lamps
state_5.sqlite  ─────┘       ▲
                             │
              FileSystemWatcher + 3秒周期全体再探索
```

参考方式は、[codex-monitor-hud](https://github.com/LH-03/codex-monitor-hud)のローカルJSONL探索、増分読取、定期照合の考え方に合わせています。

`state_5.sqlite`は`winsqlite3.dll`で読み取り専用に開きます。DLL、DB、schema、lockの問題はJSONLの動作を止めません。

`session_index.jsonl`は変更時の全体再探索を起動します。古い記録だけではランプを作りません。

`%LOCALAPPDATA%\CodexHud\sessions.json`は表示集合の正ではありません。既存ファイルを削除しません。

## Hook compatibility

通常起動はHookを待ちません。

`CodexHud.exe --hook`は互換用の即時終了です。状態更新とHUD起動を行わず、終了コード0を返します。

インストーラーとアンインストーラーは`hooks.json`を読み書きしません。既存Hookを変更しません。

`tools\install-hooks.ps1`は手動移行用の旧資料です。通常のリリースZIPとインストール経路には含めません。

## Quick start

### Requirements

- Windows
- .NET 10 SDK

リリースZIPはself-containedです。

### Development

```powershell
dotnet build .\src\CodexHud\CodexHud.csproj -c Release
dotnet run --project .\src\CodexHud\CodexHud.csproj -c Release
```

起動後、セッションが0件でもHUDは常駐します。

### Release package

```powershell
pwsh -NoProfile -File .\tools\publish-release.ps1
```

### Install

展開したリリースZIPで実行します。

```powershell
powershell -NoProfile -File .\Install-CodexHud.ps1
```

インストーラーはアプリとスタートメニューショートカットだけを更新します。

### Uninstall

```powershell
powershell -NoProfile -File .\Uninstall-CodexHud.ps1
```

アンインストーラーはアプリと所有するショートカットだけを削除します。

Hook設定は変更しません。

## Tests

```powershell
dotnet run --project .\tests\CodexHud.Tests\CodexHud.Tests.csproj -c Release
python -m unittest discover -s experiments -p 'test_*.py'
```

テストはJSONLイベント、silent完了、malformed JSON、未知イベント、ファイル置換、SQLiteフォールバック、重複排除、64件上限、30分窓、内部セッション除外、状態遷移、Watcher、描画、配置、Hook非接触を確認します。

## Documentation

| Document | Description |
| --- | --- |
| [`docs/architecture.md`](docs/architecture.md) | 監視エンジン、情報源、状態モデル |
| [`docs/probe.md`](docs/probe.md) | JSONLとSQLiteの観測境界 |
| [`docs/implementation.md`](docs/implementation.md) | 実装構成、定数、検証コマンド |
| [`docs/hud.md`](docs/hud.md) | ランプ、配置、トレイ表示 |
| [`docs/hooks-findings.md`](docs/hooks-findings.md) | 旧Hook方式の歴史資料 |
| [`docs/hooks-setup.md`](docs/hooks-setup.md) | 旧Hook手動資料。現行経路では使用しない |
