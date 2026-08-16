# ローカルセッション監視 Probe

## 目的

Probeは、Codexのローカル記録から匿名化したセッションID、活動時刻、許可イベントだけをEngineへ渡します。

ProbeはCodexを操作しません。

Hookを状態同期の入力にしません。

## 観測対象

| 対象 | 用途 | 失敗時 |
| --- | --- | --- |
| `CODEX_HOME\sessions\**\*.jsonl` | セッション発見とイベント読取 | JSONLだけで読取可能な範囲を表示 |
| `state_5.sqlite` | 未アーカイブのユーザーセッションと活動時刻 | JSONLへフォールバック |
| `session_index.jsonl` | 全体再探索の起動 | 古い行から表示を作らない |

## JSONLの識別

通常のファイル名は、ファイル名のUUIDをSHA-256短縮値へ変換します。

別形式のファイルは、最初の`session_meta`レコードのIDを使います。

ファイル名IDと`session_meta` IDが一致しないファイルは候補にしません。

`session_meta.payload.source.subagent`が確認できるファイルは候補から除外します。

prompt、本文、command、tool input、cwdは保持しません。

## JSONLイベント

次のイベントだけを採用します。

| JSONL payload type | 正規化結果 |
| --- | --- |
| `task_started` | `Active`の根拠 |
| 非silentな`task_complete` | `Completed`の根拠 |
| `turn_aborted` | `Aborted`の根拠 |

`task_complete`に有効な`last_agent_message`がない場合はsilent完了です。

silent完了は状態根拠にしません。

malformed JSON、未知イベント、未完了行、64KB超の行は状態根拠にしません。

## 増分読取

Probeはファイルごとに次の値をメモリ上で管理します。

- byte offset
- creation time
- 先頭レコードの署名
- 未完了行のバッファ

ファイル縮小、置換、先頭署名変更を検出するとcursorをリセットします。

1ファイルの1回の読取上限は256KBです。

全ファイルの1回の読取上限は4MBです。

読取失敗は`ReadError`を30秒保持します。

malformed JSONは読取エラーではありません。

## SQLite補助源

`WindowsSessionActivitySource`は`winsqlite3.dll`を読み取り専用で呼び出します。

Queryが使う値は次だけです。

- `id`
- `rollout_path`
- `updated_at_ms`

対象は次の条件です。

- `archived = 0`
- `thread_source = 'user'`
- 通常30分以内の`updated_at_ms`

SQLiteのpathは`sessions`配下であることを検証します。

SQLite IDの匿名化値とrolloutファイル名のIDが一致することを検証します。

pathが外部、IDが不一致、IDが空、更新時刻が不正な行は破棄します。

DLL不足、DB不存在、壊れたDB、schema不一致、lock、権限エラーは補助源の失敗です。

補助源の失敗でJSONLのランプを削除しません。

## 状態判断の境界

| 状態 | 時間規則 |
| --- | --- |
| `Active` | JSONL活動12秒。SQLite活動3分 |
| `Listening` | 最近のファイルまたはJSONL活動90秒 |
| `Idle` | 上記の鮮度を超過 |
| `Completed` | 非silent完了から120秒 |
| `Aborted` | 中断から120秒 |
| `ReadError` | 読取エラーから30秒 |

`Listening`は承認待ち、質問待ちを意味しません。

ファイルの存在だけで`Active`にはしません。

## 検証

自動テストは次を確認します。

- 明示イベントとsilent完了
- malformed JSON、未知イベント、未完了行、過大行
- ファイル置換
- JSONLだけの2セッション
- SQLite活動による古いJSONLの保持
- SQLite失敗時のJSONLフォールバック
- 64件上限、30分窓、内部セッション除外、ID重複排除
- 削除と状態保持期限

手動確認では、2つ以上のCodexセッションを同時に動かします。

JSONL更新、SQLite更新、ファイル削除後に、ランプ数、状態、削除遅延を確認します。

Hook経路を手動で追加しません。
