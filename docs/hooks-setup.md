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
