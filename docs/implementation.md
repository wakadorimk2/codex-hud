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

テストは、三状態の描画、可視ピクセル、Storeの状態遷移、Hook payloadの匿名化、Named Pipe、bridgeの終了コード、DPI配置を確認します。

## Runtime modes

引数なしで起動すると、HUDとNamed Pipe serverを起動します。

`--hook`で起動すると、標準入力をsanitized messageへ変換してNamed Pipeへ送信し、終了します。

`SessionStart`のbridge invocationだけが、HUDプロセスの起動を試みます。

Pipe serverが停止していても、bridgeは終了コード0を返します。

## Manual acceptance

- `Idle`が作業を妨げない。
- `Running`が弱く動く。
- `NeedsAttention`が明確に目立つ。
- `NeedsAttention`が時間経過で消えない。
- 次の`UserPromptSubmit`で`Running`へ戻る。
- ランプがクリックを透過する。
- HUDがフォーカスを奪わない。
- Primary monitorの右下に約36 DIPで表示される。
- 100%と150% DPIで位置とサイズが許容範囲になる。

複数モニター最適化、サウンド、粒子、3D、クリック操作は今回の対象外です。
