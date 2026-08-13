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

テストは、三状態の描画、可視ピクセル、複数セッションの独立遷移、状態優先順、`SessionEnd`の猶予と削除中止、状態スナップショットの復元、Hook payloadの匿名化、Named Pipe、bridgeの終了コード、DPI配置、折返し、位置の永続化を確認します。

## Runtime modes

引数なしで起動すると、HUDとNamed Pipe serverを起動します。

`--hook`で起動すると、標準入力をsanitized messageへ変換してNamed Pipeへ送信し、終了します。

`SessionStart`のbridge invocationだけが、HUDプロセスの起動を試みます。

Pipe serverが停止していても、bridgeは終了コード0を返します。

HUD起動時は`%LOCALAPPDATA%\CodexHud\sessions.json`から`Running`と`NeedsAttention`を復元します。

`SessionEnd`を受けたセッションは約240msだけ`Idle`で表示し、その後に削除します。

`SessionEnd`が届かない場合、時間経過でセッションを削除しません。

Hook設定とEnterlightなどの既存Hookは変更しません。

## Manual acceptance

- `Idle`が作業を妨げない。
- `Running`が弱く動く。
- `NeedsAttention`が明確に目立つ。
- `NeedsAttention`が時間経過で消えない。
- 次の`UserPromptSubmit`で`Running`へ戻る。
- 2つ以上のセッションを同時に表示できる。
- `NeedsAttention`、`Running`、`Idle`の順に並ぶ。
- 同じ状態の順序が初回観測順で安定する。
- 画面幅を超えたランプが次の行へ折り返す。
- `Stop`で対象セッションが橙色になる。
- `SessionEnd`で対象セッションがグレーになり、約240ms後に消える。
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
