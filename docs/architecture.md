# Codex HUD アーキテクチャ

## 目的

Codexのローカルセッション記録から、最近のユーザーセッション集合と現在状態を作ります。

HUDはCodexを起動、承認、入力、操作しません。

Hook通知は状態の正ではありません。

## 責務

| コンポーネント | 責務 | 境界 |
| --- | --- | --- |
| `CodexSessionFileDiscovery` | `sessions`以下のJSONLを再帰探索し、最近の候補を作る | UIを持たない |
| `CodexSessionEventProbe` | JSONLを増分読取し、許可したイベントだけを正規化する | 本文とraw JSONを保持しない |
| `WindowsSessionActivitySource` | `state_5.sqlite`からSQLite活動を読み取る | 読み取り専用。JSONLを必須にしない |
| `SessionMonitorEngine` | セッション集合、状態遷移、ライフサイクル、表示順を管理する | Hook、UI、Codex操作を持たない |
| `CodexSessionFileWatcher` | JSONL、`session_index.jsonl`、`state_5.sqlite`の変更を通知する | 状態を判断しない |
| `MainWindow` | Engineの一意な表示集合をランプへ反映する | `.codex`とSQLiteを直接読まない |

## 現行データフロー

```text
CODEX_HOME\sessions\**\*.jsonl ──┐
                                  ├─> discovery ──┐
CODEX_HOME\state_5.sqlite ───────┘               │
                                                  ├─> SessionMonitorEngine
FileSystemWatcher / 30秒周期 ─────────────────────┘
                                                          │
                                                          ├─> MainWindow
                                                          └─> tray counts
```

通常の変更は変更パスだけを`PollPaths(paths, now)`へ渡します。

起動、作成、削除、名前変更、Watcher overflow、`session_index.jsonl`変更、`state_5.sqlite`変更、30秒周期は`RefreshActiveSessions(now)`を使います。

`AdvanceLifecycle(now)`は活動時刻を状態へ反映し、期限切れの表示を削除します。

## 表示集合

Engineは次の和集合を作ります。

1. 最終更新が通常30分以内のJSONL。
2. SQLiteの未アーカイブ、ユーザー由来、通常30分以内の活動行。

全体の表示上限は64セッションです。

セッションIDを辞書キーにします。

同じIDのJSONLが複数ある場合は、最終更新が新しい候補を使います。

SQLiteの`rollout_path`は`CODEX_HOME\sessions`配下であることを確認します。

SQLiteのIDをSHA-256短縮値へ変換し、JSONLファイル名のIDと一致することを確認します。

`session_meta.payload.source.subagent`が確認できるJSONLは除外します。

`session_index.jsonl`の古い記録だけでは表示集合を保持しません。

探索が部分的な場合、既存の表示を即時に全削除しません。

## 情報源の境界

### JSONL

`CodexSessionEventProbe`はファイルごとのbyte offsetを保持します。

ファイル縮小、置換、先頭レコード変更を検出した場合はcursorをリセットします。

1ファイルの読取上限は256KBです。

1回の全体読取上限は4MBです。

1行の上限は64KBです。

未完了行は次回の読取まで保持します。

malformed JSON、未知イベント、過大行は状態根拠にしません。

JSONLから採用するイベントは、`task_started`、非silentな`task_complete`、`turn_aborted`です。

silentな`task_complete`は無視します。

### SQLite

`WindowsSessionActivitySource`は`winsqlite3.dll`を読み取り専用で使います。

必要な列は、セッションID、`rollout_path`、`updated_at_ms`だけです。

busy timeoutは短く設定します。

DLL不足、DB不足、schema不一致、lock、読取エラーでは空の補助結果を返します。

EngineはJSONLだけで継続します。

SQLiteの活動は、JSONLの最終更新が古くてもセッションを保持できます。

## 状態モデル

| 状態 | 根拠 | 既定の鮮度または保持 |
| --- | --- | --- |
| `Active` | `task_started`、または新しいSQLite活動 | JSONL 12秒。SQLite 3分 |
| `Listening` | 最近のファイル活動、読取待ち、identity保留 | 90秒 |
| `Idle` | 最近の活動なし | セッションが集合にある間 |
| `Completed` | 非silentな`task_complete` | 120秒 |
| `Aborted` | `turn_aborted` | 120秒 |
| `ReadError` | JSONLのI/O読取エラー | 30秒 |

`Listening`は承認待ち、質問待ち、回答待ちを断定しません。

状態の正はEngineの現在状態です。

イベントは状態判断の根拠です。

ファイルが存在するだけでは`Active`にしません。

無更新だけで`Completed`、`Aborted`、`ReadError`へ遷移させません。

## ランプ表示

`LampState`を直接固定色へ変換します。

`LampAppearance`、Muted、Stop専用表示規則はありません。

`Active`と`Listening`だけが弱く動きます。

`Idle`、`Completed`、`Aborted`、`ReadError`は状態色を維持します。

表示順は、`Active`、`Listening`、`ReadError`、`Aborted`、`Completed`、`Idle`です。

同じ状態では`FirstSeenOrder`を使います。

## Hook互換

通常起動はHookを待ちません。

`CodexHud.exe --hook`は終了コード0で即時終了します。

状態更新、HUD起動、Named Pipe送信をしません。

インストーラーとアンインストーラーはHook設定を読み書きしません。

`tools/install-hooks.ps1`は旧方式の手動資料として残します。

## セキュリティ境界

次の値をEngine、ログ、UIへ渡しません。

- prompt
- 回答本文
- command
- tool input
- cwd
- raw JSONL
- SQLiteの未使用列

セッションIDは匿名化済み値だけをUIへ渡します。

## 参考

探索、増分読取、定期照合、SQLite補助源の設計は、[codex-monitor-hud](https://github.com/LH-03/codex-monitor-hud)を参考にしています。
