# Codex Hook Probe セットアップ

## 目的

この手順は、Codex Hookが実行されるかを確認するために使います。

Probeは、Hookの標準入力を一回読みます。

Probeは、匿名化した一行だけを一時JSONLへ追記します。

Probeは、HUDを起動しません。

Probeは、短時間の同期Hookとして登録します。

現在のCodex Desktopは、`async` Hookを`async hooks are not supported yet`としてスキップします。

このため、Probeへ`async`を追加しません。

## 記録する情報

Probeは、次の情報だけを記録します。

- Hookイベント名
- SHA-256で短縮したセッション識別子
- SHA-256で短縮したturn識別子
- SHA-256で短縮した作業ディレクトリ識別子
- 受信時刻
- JSONのトップレベルキー名
- 値を除外した機微フィールド名

Probeは、prompt、command、tool input、パス、モデル名、メッセージ本文を記録しません。

既定の出力先は、`%TEMP%\codex-hud-hook-probe.jsonl`です。

## dry-run

リポジトリのルートで、Probeコマンドを組み立てます。

```powershell
$probePath = (Resolve-Path .\experiments\hook_probe.py).Path
$probeCommand = 'python -B "' + $probePath + '"'
pwsh -NoProfile -File .\tools\install-hooks.ps1 -HookCommandWindows $probeCommand
```

dry-runは、`%USERPROFILE%\.codex\hooks.json`を変更しません。

表示されたイベントとコマンドを確認します。

## 明示的な適用

内容を確認した後だけ、次のコマンドを実行します。

```powershell
pwsh -NoProfile -File .\tools\install-hooks.ps1 -HookCommandWindows $probeCommand -Apply
```

`-Apply`は、既存の`hooks.json`をタイムスタンプ付きでバックアップします。

既存のHook定義は削除しません。

同じコマンドが登録済みのイベントには、重複登録しません。

同じProbeコマンドに`async`が付いている場合は、その`async`だけを除去します。

Enterlightなど、別のコマンドHookの`async`設定は変更しません。

## Desktopでの確認

1. Codex Desktopを再起動します。
2. HookのレビューまたはTrustが表示された場合は、内容を確認します。
3. 新しいDesktopセッションを開始します。
4. 通常のturnを一回実行します。
5. `%TEMP%\codex-hud-hook-probe.jsonl`を確認します。
6. 承認が必要な操作を一回実行します。
7. Stop相当の記録を確認します。

Hook画面に`async hooks are not supported yet`が残る場合は、Codex Desktopを再起動します。

Desktopでの実発火を確認するまで、production HUDを成立済みと扱いません。

CLIでの発火だけでは、Desktop Hookの成功条件を満たしません。

## 既存設定の復元

復元が必要な場合は、バックアップの内容と対象パスを確認してから手動で戻します。

このリポジトリのスクリプトは、既存設定を自動削除しません。

## production bridgeへの切替

ProbeでDesktop Hookの実発火を確認した後、production bridgeへ切り替えます。

Release buildを先に実行します。

```powershell
dotnet build .\src\CodexHud\CodexHud.csproj -c Release
```

production commandとProbe commandを組み立てます。

```powershell
$hudPath = (Resolve-Path .\src\CodexHud\bin\Release\net10.0-windows\CodexHud.exe).Path
$hudCommand = $hudPath + ' --hook'
$probePath = (Resolve-Path .\experiments\hook_probe.py).Path
$probeCommand = 'python -B "' + $probePath + '"'
```

dry-runで、対象イベント、削除対象Probe、追加対象production commandを確認します。

```powershell
pwsh -NoProfile -File .\tools\install-hooks.ps1 `
  -HookCommandWindows $hudCommand `
  -HookCommand $hudCommand `
  -RemoveHookCommandWindows $probeCommand
```

確認後だけ`-Apply`を追加します。

```powershell
pwsh -NoProfile -File .\tools\install-hooks.ps1 `
  -HookCommandWindows $hudCommand `
  -HookCommand $hudCommand `
  -RemoveHookCommandWindows $probeCommand `
  -Apply
```

`-Apply`は`hooks.json`をタイムスタンプ付きでバックアップします。

一致するProbe commandだけを削除します。

Enterlightなど、一致しない既存Hookは保持します。

切替後にCodex Desktopを再起動します。

最初の`SessionStart`でHUDが起動することを確認します。

`UserPromptSubmit`で青色の`Running`へ戻ることを確認します。

`PermissionRequest`で橙色の`NeedsAttention`になることを確認します。

`Stop`でグレーの静止表示になり、`NeedsAttention`の一覧優先度を維持することを確認します。

Hook commandが利用できない場合も、bridgeは終了コード0を返します。

## Release ZIPのHook設定

Release ZIPは、`app\CodexHud.exe`をインストール先へ配置します。

ZIPを展開したフォルダーで、次のコマンドを実行します。

```powershell
powershell -NoProfile -File .\Install-CodexHud.ps1
```

インストーラーは、次の5イベントへインストール先HUD commandを追加するdry-runを表示します。

- `SessionStart`
- `UserPromptSubmit`
- `PermissionRequest`
- `Stop`
- `SessionEnd`

既存の`CodexHud.exe --hook` commandが1つだけ見つかった場合、インストーラーはその文字列を完全一致で削除対象にします。

旧commandが複数ある場合、インストーラーは自動適用せず警告します。

Enterlightなど、別のcommandは保持します。

dry-runを確認して`Y`を入力した場合だけ、`hooks.json`を書き換えます。

適用前に`hooks.json.backup-<timestamp>`を作成します。

`N`を入力すると、Hookは変更しません。

Hook設定が成功した場合だけ、インストール先HUDを起動します。

アンインストールは、次のコマンドを実行します。

```powershell
powershell -NoProfile -File .\Uninstall-CodexHud.ps1
```

アンインストーラーは`%LOCALAPPDATA%\CodexHud\App\CodexHud.exe --hook`だけを`-RemoveOnly`でdry-runします。

削除対象がない場合、`hooks.json`を書き換えません。

削除対象がある場合、確認後にバックアップを作成して削除します。

アンインストーラーは、異なるEXEを指す`Codex HUD.lnk`を削除しません。

アンインストーラーは`position.json`と`sessions.json`を残します。
