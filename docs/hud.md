# HUD仕様

## 表示

HUDは、監視Engineが返す一意なセッション集合をランプへ変換します。

セッションが0件でもHUDは常駐します。

表示上限は64ランプです。

ランプセルは36 DIPです。

セル間隔、折返し、DPI、位置保存、クリック透過は既存仕様を維持します。

表示順は次の順です。

1. `Active`
2. `Listening`
3. `ReadError`
4. `Aborted`
5. `Completed`
6. `Idle`

同じ状態では初回発見順を使います。

## 状態色

| 状態 | 色 | 動作 |
| --- | --- | --- |
| `Active` | 青 | 弱い動き |
| `Listening` | 紫 | 弱い動き |
| `Idle` | 灰 | 安定 |
| `Completed` | 緑 | 短い遷移後に安定 |
| `Aborted` | 赤 | 短い遷移後に安定 |
| `ReadError` | 赤橙 | 短い遷移後に安定 |

`LampAppearance`は使いません。

MutedやStop専用の表示規則はありません。

## 状態の意味

`Active`は、taskの開始または新しいSQLite活動を示します。

`Listening`は、最近の記録活動を示します。

`Listening`から承認待ち、質問待ち、回答待ちを推測しません。

`Idle`は、表示集合に残っているが最近の活動がない状態です。

`Completed`は非silentなtask完了を120秒保持します。

`Aborted`はturn中断を120秒保持します。

`ReadError`はJSONLのI/O読取エラーを30秒保持します。

## トレイ

トレイのツールチップには、6状態の件数を表示します。

表示ラベルは`Act`、`Lis`、`Idl`、`Cmp`、`Abt`、`Err`です。

トレイからHUDの表示、位置編集、終了を操作できます。

HUDを非表示にしても監視ワーカーは動作します。

## ウィンドウ

通常表示はタスクバーのボタンを表示しません。

HUDはAlways on topです。

通常時のランプはクリックを透過します。

位置編集モードでは、HUDの位置をドラッグできます。

位置は`%LOCALAPPDATA%\CodexHud\position.json`へ保存します。

## 入力境界

HUDはCodexの承認、回答、turn操作を実行しません。

ランプへ渡す値は匿名化済みセッションID、6状態、表示順、時刻メタデータだけです。

prompt、回答本文、command、tool input、cwd、raw JSONLを表示しません。

## Hook互換

通常起動はHookを待ちません。

`CodexHud.exe --hook`は即時終了します。

Hookの状態更新、HUD起動、Named Pipe送信はありません。
