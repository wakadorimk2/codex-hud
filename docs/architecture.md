# Codex HUD アーキテクチャ

## Goal

Codexをゲーム、イラスト、ブラウジングなどの別作業と並行して利用すると、ユーザーは現在のセッション状態を見失うことがあります。

特に、Codexがユーザー入力または承認を待っている状態は、短い通知だけでは見逃す可能性があります。

本プロジェクトは、Codexの利用方法を変更せずに、セッションの現在状態を外部から観測し、画面端の小さなHUDへ持続的に表示することを目指します。

最初の成立条件は、`WaitingForUser`と`WaitingForApproval`を外部から区別し、状態として保持できることです。

## Non-goals

現時点では、次の機能を対象にしません。

- Codex自体の代替UI
- Codexの実行管理
- HUDからの承認操作
- ChatGPT対応
- Claude対応
- モバイル通知
- 複数PC同期
- セッション履歴分析
- 生産性分析
- タスク管理
- AI Agent orchestration

HUDはCodexを操作しません。

HUDは現在状態の表示と、将来の対象ウィンドウへの移動だけを扱います。

## Components

```text
Codex
  ↓
Codex State Probe / Monitor
  ↓
Session State Store
  ↓
HUD
  ├─ Visual notification
  ├─ Sound notification
  └─ Window focus
```

| コンポーネント | 主な責務 | 境界 |
| --- | --- | --- |
| Codex | 既存のセッション実行、turn実行、ユーザー対話 | HUDのために利用方法を変えない |
| Codex State Probe / Monitor | `.codex`、App Server、CLIなどのCodex固有情報源を観測する | UIを描画しない。Codexをラップまたは操作しない |
| Session State Store | セッションごとの現在状態、状態遷移、判断根拠を保持する | 情報源ごとのファイル形式を直接解釈しない |
| HUD | Storeの現在状態をPassive modeまたはExpanded modeで表示する | Codex固有情報源を直接参照しない |
| Visual notification | 状態に応じて視覚的な注意度を変える | 現在状態を変更しない |
| Sound notification | 将来、状態遷移時に補助通知を出す | 繰り返し音や操作を妨げる音を既定にしない |
| Window focus | 将来、ユーザーのクリックで対象ウィンドウへ移動する | ユーザー操作なしにフォーカスを奪わない |

## Production implementation

productionの最小経路は、次の固定境界を使います。

```text
Codex Hook
  → CodexHud.exe --hook
  → sanitized HookObservation
  → Named Pipe
  → SessionStateStore
  → WPF MainWindow
  → SkiaSharpLampRenderer
```

`HookBridge`は標準入力を一回読みます。

`HookObservationParser`はイベント名を許可済みの列挙値へ変換します。

セッション識別子はSHA-256の短縮値へ変換します。

prompt、command、tool input、cwd、生のHook JSONはNamed Pipeへ渡しません。

`NamedPipeStateServer`は一つのsanitized messageを受信します。

`SessionStateStore`はHook JSONを解釈しません。

HUDはStoreのセッション一覧だけを読み取ります。

`SessionLampState`は、匿名化済みセッションID、ランプ状態、初回観測順、最終Hook観測日時を保持します。

HUDは生のHook payload、prompt、command、tool input、cwdを受け取りません。

Named Pipeが停止しても、Hook bridgeは終了コード0を返します。

`SessionStart`だけが、必要な場合にHUDプロセスを起動します。

`CodexSessionCatalogProbe`は、`session_index.jsonl`と`archived_sessions`を読み取ります。

Probeは、セッションIDを匿名化し、最終更新時刻とアーカイブ状態だけをStoreへ渡します。

Probeは、Codexの履歴ファイルを変更しません。

Storeは、アーカイブ済みセッションをHUDの一覧から削除します。

成功したカタログに存在しないセッションは、時刻に関係なくHUDの一覧から削除します。

カタログに存在するセッションは、最終Hook観測またはカタログ最終更新から1時間以上経過した場合に削除します。

Hook観測時刻とカタログ最終更新時刻がどちらもないセッションは、HUDへ表示しません。

カタログを読み取れない場合、Storeはその周期の自動削除を実行しません。

HUDはCodexプロセスを監視しません。

## Lamp state projection

この実装のランプは、研究用の六状態モデルを三状態へ投影します。

| Hook event | Lamp state |
| --- | --- |
| `SessionStart`、`UserPromptSubmit` | `Running` |
| `PermissionRequest`、`Stop` | `NeedsAttention` |
| `SessionEnd` | `Idle` |
| malformed、unknown | 現在状態を維持 |

複数セッションがある場合、Storeは`NeedsAttention`、`Running`、`Idle`の順で一覧を返します。

同じ状態のセッションは、初回観測順を維持します。

`SessionEnd`は対象セッションを短時間`Idle`として保持します。

約240ms後に対象セッションを一覧から削除します。

猶予中の`UserPromptSubmit`は削除を中止し、対象セッションを`Running`へ戻します。

`SessionEnd`の一時的な`Idle`状態は保存しません。

実環境で`SessionEnd`が届かない場合も、Session Catalog Cleanupは別の期限判定を行います。

`NeedsAttention`は通常の状態更新では時間経過で解除しません。

成功したカタログに存在しないセッションは、状態に関係なくHUD一覧から削除します。

カタログに存在するセッションの1時間超過による自動整理は、状態解除ではなくHUD一覧からの削除です。

次の`UserPromptSubmit`または`SessionStart`で、そのセッションを`Running`へ戻します。

### 正規化された観測

Probeは情報源ごとの形式を、次の概念情報へ正規化します。

- セッションID。値は不透明な識別子として扱います。
- 表示名またはプロジェクト名。取得できない場合は省略します。
- 現在状態。
- 観測時刻。
- 状態判断の根拠。情報源、イベント種別、該当位置を含めます。

これは文書上の責務境界です。

現段階では、公開API、プラグイン契約、汎用フレームワークを実装しません。

## State Model

状態の正は、Session State Storeが保持する現在状態です。

通知イベントは、状態を判断した根拠です。

通知イベントそのものを、HUDが表示する正のデータにしません。

| 状態 | 意味 | 遷移条件 |
| --- | --- | --- |
| `Unknown` | 信頼できる状態根拠がまだない状態です | 新しいセッションの初期状態です。根拠不足時にも使います |
| `Running` | 現在のturnが実行中である根拠があります | セッション開始、新しいturn、待機解除後の実行イベントで設定します |
| `WaitingForUser` | Codexが通常のユーザー回答を待っています | Codexが質問または回答要求を明示したときに設定します |
| `WaitingForApproval` | Codexが承認結果を待っています | command approvalまたはfile change approvalを明示したときに設定します |
| `Completed` | 最新のturnが明示的に完了しました | turn完了の根拠を観測したときに設定します。セッションプロセスの終了だけでは設定しません |
| `Error` | Codexまたはturnの明示的なエラーを観測しました | エラーの根拠を観測したときに設定します。無更新だけでは設定しません |

`WaitingForApproval`のcommand approvalとfile change approvalは、状態を増やさず、判断根拠のイベント種別で区別します。

### 状態遷移

```text
Unknown ── active turn evidence ──> Running
Unknown ── user question ─────────> WaitingForUser
Unknown ── approval request ───────> WaitingForApproval
Running ── user question ─────────> WaitingForUser
Running ── approval request ───────> WaitingForApproval
Running ── explicit completion ───> Completed
Running ── explicit error ─────────> Error
WaitingForUser ── new turn ────────> Running
WaitingForApproval ── new turn ────> Running
Completed/Error ── new turn ───────> Running
```

次の規則を適用します。

- 明示的な状態根拠がない場合、タイムアウトだけで状態を変更しません。
- 新しいセッションで明示的な待機根拠を最初に観測した場合は、`Unknown`から対応する待機状態へ直接遷移できます。
- `WaitingForUser`と`WaitingForApproval`は、状態遷移として時間経過だけで解除しません。
- 成功したカタログに存在しないセッションは、時刻に関係なくHUD一覧から削除します。
- カタログに存在するセッションは、時刻がある場合に最終活動から1時間でHUD一覧から削除します。
- Hook観測時刻とカタログ最終更新時刻がない旧スナップショットは復元しません。
- 承認拒否、キャンセル、再試行の扱いは、Codexから観測できる明示的な結果に従います。
- 新しいturnの開始を観測した場合は`Running`へ戻します。
- `Completed`は最新turnの完了を示します。Codexアプリ全体の終了を意味しません。
- `Error`は明示的なエラーを示します。次のturnを観測した場合は`Running`へ戻します。

## Detection boundary

Codex固有の検知処理をProbe内部に閉じ込めます。

HUDは、Probeの情報源が`.codex`ファイル、FileSystemWatcher、App Server、CLI出力のどれであるかを知りません。

最小構成は次の境界です。

```text
Codex-specific observation
  → normalized observation
  → Session State Store
  → HUD read model
```

将来、検知方式を変更する場合も、変更対象は原則としてProbe側です。

プロセスやHWNDは、セッションIDと対象ウィンドウを対応付ける補助根拠として扱います。

プロセスやHWNDだけで、Codexの状態を決定しません。

現段階では、一つの検知方式を検証するための最小実装を優先します。

過剰なプラグイン基盤、汎用イベントバス、他エージェント向け共通モデルは作りません。

## Windows integration

Windows固有機能は、Probeの状態取得が成立した後に必要な範囲で選定します。

| 候補 | 適している理由 | 未知の点 |
| --- | --- | --- |
| `.NET` | Windowsプロセス、ファイル監視、アプリケーションライフサイクルを扱いやすい | 対象情報源の実際の更新頻度と権限 |
| `WPF` | 小さな常駐ウィンドウ、Always on top、入力、DPI対応を検討しやすい | 全画面ゲーム、複数モニター、表示負荷 |
| `Win32 API` | HWND、ウィンドウ位置、アクティブウィンドウ、フォーカス移動を扱える | フォーカス奪取、権限差、ゲームとの相互作用 |
| `WebView2` | 状態一覧やExpanded modeを柔軟に表現できる | ランタイム依存、メモリ、透明オーバーレイとの相性 |

Always on topは、ユーザーの作業を妨げない表示方法とセットで検証します。

フォーカス移動は、ユーザーがHUDをクリックした場合だけ実行する方針です。

ゲーム中の表示は、入力を奪わないこと、大きなポップアップを出さないこと、DPIと複数モニターを考慮することを条件にします。

通知音は補助機能です。

通知音が状態の正になることはありません。

## First milestone

最初のマイルストーンは、Codexがユーザー入力または承認を待ったことを外部から検知し、`WaitingForUser`または`WaitingForApproval`として保持することです。

HUDの完成、音通知、フォーカス移動は、このマイルストーンの後に扱います。
