# 実装メモ

## 実装構成

| 領域 | 主なファイル |
| --- | --- |
| 起動と常駐 | `src/CodexHud/App.xaml.cs` |
| セッション集合と状態 | `src/CodexHud/Infrastructure/SessionMonitorEngine.cs` |
| JSONL探索 | `src/CodexHud/Infrastructure/CodexSessionFileDiscovery.cs` |
| JSONL増分読取 | `src/CodexHud/Infrastructure/CodexSessionEventProbe.cs` |
| SQLite補助源 | `src/CodexHud/Infrastructure/WindowsSessionActivitySource.cs` |
| FileSystemWatcher | `src/CodexHud/Infrastructure/CodexSessionFileWatcher.cs` |
| 監視処理の直列化 | `src/CodexHud/Infrastructure/SessionMonitorWorkQueue.cs` |
| ランプ描画 | `src/CodexHud/Rendering/SkiaLampRenderer.cs`、`SkiaLampView.cs` |
| UI | `src/CodexHud/MainWindow.xaml.cs` |

旧Store、Named Pipe、HookBridge、Hookスナップショットは通常経路から削除しました。

## 既定値

- JSONL探索窓: 30分
- 表示上限: 64セッション
- SQLite活動鮮度: 3分
- JSONLのActive鮮度: 12秒
- Listening鮮度: 90秒
- Completed保持: 120秒
- Aborted保持: 120秒
- ReadError保持: 30秒
- 全体再探索: 3秒
- JSONL 1ファイル読取上限: 256KB
- JSONL 1回全体読取上限: 4MB
- JSONL 1行上限: 64KB

## 起動経路

通常起動では、HUD、Watcher、監視ワーカー、3秒タイマーを起動します。

セッションが0件でもウィンドウを表示します。

起動時に全体再探索を一度要求します。

`--hook`では終了コード0で即時終了します。

`--hook`は状態更新、HUD起動、IPC送信をしません。

## 同期経路

Watcherは次のイベントを変更パスへ変換します。

- JSONLの通常変更: 増分読取
- JSONLの作成、削除、名前変更: 全体再探索
- `session_index.jsonl`: 全体再探索
- `state_5.sqlite`: 全体再探索
- Watcher overflow: 全体再探索
- 3秒周期: 全体再探索

複数の変更は`ConcurrentDictionary`でまとめます。

監視処理は一つのワーカーで実行します。

UI更新はWPF Dispatcherへ戻します。

## 表示集合

JSONL候補とSQLite活動をセッションIDで結合します。

同じIDの候補は一つへ統合します。

全体を64件へ制限します。

古い`session_index.jsonl`だけではセッションを保持しません。

`%LOCALAPPDATA%\CodexHud\sessions.json`を読みません。

既存の`position.json`は位置保存に使います。

## テスト

```powershell
dotnet build .\src\CodexHud\CodexHud.csproj -c Release
dotnet run --project .\tests\CodexHud.Tests\CodexHud.Tests.csproj -c Release
python -m unittest discover -s experiments -p 'test_*.py'
```

テストプログラムは、状態、探索、増分読取、フォールバック、Watcher、描画、配置、Hook非接触を確認します。

通常ビルドは既存の`NU1701`警告が3件あります。

## 手動確認

1. 2つ以上のCodexセッションを起動します。
2. JSONLだけでランプが2件になることを確認します。
3. 一方のJSONL更新後に`Active`または`Listening`になることを確認します。
4. 非silent完了後に`Completed`になることを確認します。
5. 中断後に`Aborted`になることを確認します。
6. JSONLを削除した後にランプが消えることを確認します。
7. SQLite更新時に古いJSONLセッションが保持されることを確認します。
8. SQLiteを読めない状態でもJSONLランプが残ることを確認します。
9. `hooks.json`のSHA-256がインストール前後で同じことを確認します。
10. `CodexHud.exe --hook`が終了コード0で状態を変更しないことを確認します。

手動確認の結果は、自動テストの結果と分けて記録します。
