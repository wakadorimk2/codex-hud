# Codex HUD production実装

## Stack

- Target Framework: `net10.0-windows`
- UI shell: WPF
- Rendering: SkiaSharp 4.150.1
- WPF integration: SkiaSharp.Views.WPF 4.150.1
- Windows native assets: SkiaSharp.NativeAssets.Win32 4.150.1
- IPC: Windows Named Pipe
- State: SessionStateStore

初回restoreはNuGetへ接続します。

`SkiaSharp.Views.WPF`の依存パッケージについて、現在のrestoreではNU1701警告が出ます。

Release buildは成功します。

## Build and test

```powershell
dotnet build .\src\CodexHud\CodexHud.csproj -c Release
dotnet run --project .\tests\CodexHud.Tests\CodexHud.Tests.csproj -c Release
python -m unittest discover -s experiments -p 'test_*.py'
```

テストは、三状態と表示属性の描画、可視ピクセル、複数セッションの独立遷移、状態優先順、`SessionEnd`の猶予と削除中止、状態スナップショットの復元、時刻なし旧スナップショットの除外、表示属性なし旧スナップショットの復元、セッションカタログの匿名化とアーカイブ照合、1時間整理、Hook payloadの匿名化、Named Pipe、bridgeの終了コード、DPI配置、折返し、位置の永続化を確認します。

## Runtime modes

引数なしで起動すると、HUDとNamed Pipe serverを起動します。

`--hook`で起動すると、標準入力をsanitized messageへ変換してNamed Pipeへ送信し、終了します。

`SessionStart`のbridge invocationだけが、HUDプロセスの起動を試みます。

`SessionStart`のbridgeは、HUD起動直後のNamed Pipe接続を送信前だけ再試行します。

Named Pipe接続後の書き込み失敗は再送しません。

Pipe serverが停止していても、bridgeは終了コード0を返します。

HUD起動時は`%LOCALAPPDATA%\CodexHud\sessions.json`から、有効な最終観測日時を持つ`Running`と`NeedsAttention`を復元します。

`SessionEnd`を受けたセッションは約240msだけ`Idle`で表示し、その後に削除します。

HUD起動後にセッションカタログを一回読み取り、アーカイブ済みセッションを削除します。

初回カタログ整理はUIスレッド外で実行します。

HUDは1分ごとにセッションカタログを読み取ります。

Hookで最近観測したセッションは、カタログに一時的に存在しなくても保持します。

カタログに存在しないセッションは、最終Hook観測から1時間以上経過した場合に削除します。

カタログに存在するセッションは、最終Hook観測またはカタログ最終更新から1時間以上経過した場合に削除します。

Hook観測時刻とカタログ最終更新時刻がどちらもないセッションは、HUDへ表示しません。

カタログを読み取れない場合、その周期の自動削除を実行しません。

旧スナップショットに最終観測日時がないセッションは、HUDへ表示しません。

表示中のセッション数は、匿名化済みセッションIDの一意な集合から求めます。

Hook設定とEnterlightなどの既存Hookは変更しません。

## Manual acceptance

- `Idle`が作業を妨げない。
- `Running`が弱く動く。
- `NeedsAttention`が明確に目立つ。
- 時刻がある`NeedsAttention`が1時間未満で保持される。
- 次の`UserPromptSubmit`で`Running`へ戻る。
- 2つ以上のセッションを同時に表示できる。
- `NeedsAttention`、`Running`、`Idle`の順に並ぶ。
- 同じ状態の順序が初回観測順で安定する。
- 画面幅を超えたランプが次の行へ折り返す。
- `PermissionRequest`で対象セッションが橙色で脈動する。
- `Stop`で対象セッションがグレーで静止する。
- `Stop`が`NeedsAttention`の一覧優先度を維持する。
- `SessionEnd`で対象セッションがグレーになり、約240ms後に消える。
- アーカイブ済みセッションが次のカタログ整理で消える。
- カタログにない最近のHook観測が次のカタログ整理で残る。
- カタログにない1時間超過セッションが次のカタログ整理で消える。
- カタログに存在する1時間超過セッションが1分周期の整理で消える。
- 時刻のない旧スナップショットのセッションが表示されない。
- カタログ読み取り失敗時に既存セッションが保持される。
- グレー表示中の`UserPromptSubmit`で対象セッションが青色へ戻る。
- HUD再起動後に残りのセッションが復元する。
- ランプがクリックを透過する。
- HUDがフォーカスを奪わない。
- Primary monitorの右下に約36 DIPで表示される。
- 100%と150% DPIで位置とサイズが許容範囲になる。
- `Ctrl + Alt + Shift + L`で位置編集モードへ切り替えられる。
- 位置編集モードでグループ全体をドラッグできる。
- 同じホットキーで位置編集モードを終了できる。
- 再起動後も保存した位置を復元する。

通常時のクリック透過は維持します。

位置編集モードだけ、一時的にクリック透過を解除します。

位置は`%LOCALAPPDATA%\CodexHud\position.json`へ保存します。

セッション状態は`%LOCALAPPDATA%\CodexHud\sessions.json`へ保存します。

`SessionEnd`のイベント仕様は、[OpenAI公式 Hooks ドキュメント](https://learn.chatgpt.com/docs/hooks)を参照します。

複数モニター最適化、サウンド、粒子、3Dは今回の対象外です。
