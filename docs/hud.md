# Codex HUD UX / UI 方針

## UX Goal

別の作業中でも、Codexの要対応状態を見逃さないようにします。

HUDはCodexを操作するUIではありません。

HUDは、Session State Storeが保持する現在状態を画面端に持続表示するUIです。

## 設計原則

- 現在状態を正として表示します。
- 一時的な通知イベントだけを正にしません。
- 通常時は作業を妨げません。
- 要対応状態は状態遷移として時間経過だけで解除しません。ただし、時刻がある一覧項目は5分で整理します。
- 色、音、点滅だけに意味を依存しません。Expanded modeでは状態名を表示します。
- ゲーム中に大きなモーダルやポップアップを出しません。
- ユーザー操作なしに、他のウィンドウからフォーカスを奪いません。

## Passive mode

通常時は、画面端に非常に小さく表示します。

表示例:

```text
● ● ● !
```

または、セッションごとの状態を小さな行で表示します。

```text
● quiet-days       RUN
● mod-manager      RUN
! wakadori.me      WAIT
✓ research         DONE
```

Passive modeでは、セッション名を短く表示できます。

状態の注意度を、次の優先順位で表現します。

1. `Error`
2. `WaitingForApproval`
3. `WaitingForUser`
4. `Running`
5. `Completed`
6. `Unknown`

`Unknown`は根拠不足を示す状態です。

`Unknown`をエラーとして通知しません。

Passive modeは、現在状態の存在を示します。

イベント履歴や生ログを、常時表示しません。

## Expanded mode

Hoverまたはクリックで、セッションの詳細を表示します。

表示例:

```text
Quiet Days       Running
Mod Manager      Waiting for approval
wakadori.me      Completed
```

Expanded modeでは、最低限次を表示します。

- セッションの表示名またはプロジェクト名
- 状態名
- 状態の判断根拠を短くした説明
- 最終観測時刻。取得できる場合に表示します

command approvalとfile change approvalは、状態名を増やさず、根拠説明で区別します。

生ログ、秘密情報、長いCodex本文を既定表示にしません。

## Notification policy

| 状態 | 既定の表示 | 音 | 自動消去 |
| --- | --- | --- | --- |
| `Running` | Passive modeで低い注意度にします | なし | 状態が変わるまで保持し、最終活動から5分で一覧整理します |
| `Completed` | Passive modeで低い注意度にします | なし | 最終活動から5分で一覧整理します |
| `WaitingForUser` | 持続的な視覚強調を行います | 将来、状態遷移時の補助音を検討します | 状態遷移では時間で解除せず、最終活動から5分で一覧整理します |
| `WaitingForApproval` | `WaitingForUser`より高い視覚強調を行います | 将来、状態遷移時の補助音を検討します | 状態遷移では時間で解除せず、最終活動から5分で一覧整理します |
| `Error` | 最も高い視覚強調を行います | 将来、状態遷移時の補助音を検討します | 明示的な回復または新しいturnで解除し、最終活動から5分で一覧整理します |
| `Unknown` | 低い注意度または診断表示にします | なし | Probeの観測結果に従います |

音通知を追加する場合も、状態へ入ったときの一回通知を基本にします。

一定間隔で音を繰り返しません。

音通知は無効化できる設計を前提にします。

`WaitingForUser`、`WaitingForApproval`、`Error`を、トーストの終了や描画タイマーで非表示にしません。

Hookで最近観測したセッションは、カタログに一時的に存在しなくてもHUD一覧に保持します。

カタログに存在しないセッションは、最終Hook観測から5分でHUD一覧から削除します。

カタログに存在するセッションは、最終Hook観測またはカタログ最終更新から5分でHUD一覧から削除します。

Hook受信後のカタログ照合は、HUDの状態更新と並行して非同期に開始します。

1分周期のカタログ照合は、Hookがない場合の安全網として維持します。

状態が別の状態へ変わった場合だけ、注意度を更新します。

## Interaction

### Hover

HoverでExpanded modeを表示できます。

HoverだけでCodexを操作しません。

### Click

将来、セッション行をクリックすると対象Codexウィンドウへ移動できます。

移動には、Probeが補助的に取得したプロセスまたはHWNDの対応を使います。

対応が一意でない場合は、フォーカス移動を実行しません。

### 対象外の操作

HUDから次の操作を提供しません。

- command approval
- file change approval
- Codexへの回答入力
- turnの開始、停止、再実行
- Codexの設定変更

## Windows integration

次の機能を検証対象にします。

- Always on top
- 画面端への配置
- DPIスケーリング
- 複数モニター
- 全画面ゲーム中の表示
- HWNDと対象Codexウィンドウの対応
- ユーザークリック時のフォーカス移動
- 通知音の音量と無効化

Always on topは、常時最前面で作業を妨げることがないかを確認します。

全画面ゲームでは、大きな表示や入力を奪う表示を避けます。

フォーカス移動は、ユーザーが明示的にクリックした場合だけ行います。

`.NET`、`WPF`、`WebView2`、Win32 APIの採用は、このUX条件とProbeの成立結果を確認してから決定します。

## 表示と状態の境界

HUDはSession State Storeから現在状態を読み取ります。

HUDは`.codex`のファイル形式、App Serverのイベント、CLIの出力形式を直接解釈しません。

検知方法が変わっても、HUDが扱う状態名と表示規則を維持できる構造にします。

## 現在のランプ実装

現在のPassive modeは、セッションごとに約36 DIP四方のランプを表示します。

ランプはグループの左上を基準に横へ並びます。

Primary monitorの作業領域幅を超える場合は、次の行へ折り返します。

ランプ間隔は8 DIPです。

ランプは縮小しません。

表示順は`NeedsAttention`、`Running`、`Idle`です。

同じ状態のランプは、初回観測順を維持します。

セッション数が0の場合、表示領域を非表示にします。

`SessionEnd`後のランプは約240msだけグレーで表示し、その後に削除します。

猶予中に`UserPromptSubmit`を受けた場合は、削除せずに青色へ戻します。

`Stop`のランプはカタログ整理または次の状態更新まで一覧に残ります。

`Stop`のランプは暗いグレーで静止します。

UIシェルはWPFです。

描画面は`SkiaSharp.Views.WPF`の`SKElement`です。

描画はSkiaSharpの2D APIだけを使います。

描画レイヤーは、環境光、状態色の放射状グラデーション、中間リング、中心コア、残光リングです。

`Idle`は暗いグレーで静止します。

`Running`は青色で弱く呼吸します。

`Default`の`NeedsAttention`は橙色で強く発光し、緩く脈動します。

`Stop`の`Muted`表示は暗いグレーで静止し、残光リングを表示しません。

`NeedsAttention`は点滅で解除しません。

状態または表示属性の変更時だけ色遷移を行います。

アニメーション中だけ描画更新を行います。

粒子、3D、音、通常時のクリック操作、WebView2は追加しません。

ウィンドウは枠なし、透明、Always on top、タスクバー非表示、非アクティブ化で動作します。

通常時はクリック透過と非アクティブ化を有効にします。

`Ctrl + Alt + Shift + L`で位置編集モードへ切り替えます。

位置編集モードでは、白い枠を表示し、グループ全体をドラッグできます。

同じホットキーで位置編集モードを終了し、位置を保存します。

位置は`%LOCALAPPDATA%\CodexHud\position.json`へ保存します。

保存位置はPrimary monitorの作業領域内へ制限します。

セッション状態は`%LOCALAPPDATA%\CodexHud\sessions.json`へ保存します。

保存する値は、匿名化済みセッションID、ランプ状態、表示属性、初回観測順、最終Hook観測日時です。

`SessionEnd`の一時的な`Idle`状態は保存しません。

HUD再起動時は、最終観測日時がある保存済みの`Running`と`NeedsAttention`を復元します。

表示属性がない旧スナップショットは`Default`として復元します。

保存位置がない場合は、Primary monitorの右下へ16 DIPの余白を付けて配置します。

複数モニター最適化は未実装です。
