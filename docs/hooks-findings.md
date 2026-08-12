# Codex Hooks 調査結果

## 目的

Codex DesktopのHookを、productionの状態取得に使えるかを確認します。

## 事実

- ローカルCodex CLIは`0.147.0`です。
- ローカルのHook機能フラグは`stable`です。
- 現在のユーザーHook設定には、既存の`PermissionRequest`、`PreToolUse`、`Stop`、`UserPromptSubmit`のHookがあります。
- 既存Hookには、通知または音に関係するユーザー設定があります。
- `experiments/hook_probe.py`は、Hook標準入力を匿名化してJSONLへ記録します。
- `tools/install-hooks.ps1`は、既定でdry-runを実行します。
- `tools/install-hooks.ps1 -Apply`だけがユーザーHook設定を書き換えます。

## 公開仕様から確認した事項

OpenAI Docsは、`UserPromptSubmit`、`PermissionRequest`、`Stop`、`SessionStart`、`SessionEnd`などのHookイベントを説明しています。

Hookコマンドは、JSONオブジェクトを標準入力で受け取ります。

Hookには、セッション識別子、turn識別子、作業ディレクトリ、イベント名などが含まれます。

複数の一致するHookは実行されます。

未管理のHookは、実行前にレビューとTrustが必要です。

参照: [OpenAI Docs: Hooks](https://learn.chatgpt.com/docs/hooks)

## 現在の判定

### Verified

- Hook入力を生値を残さずに記録するProbeを実装しました。
- malformed JSON、未知イベント、機微フィールドを含む入力をテストしました。
- 既存Hook設定を保ったまま、追加候補をdry-runで確認できるスクリプトを実装しました。

### Unknown

- Codex Desktopの実セッションで、Probeが各イベントを受信するかは未確認です。
- Desktopの実行環境で、HookのTrust後に`SessionStart`、`UserPromptSubmit`、`PermissionRequest`、`Stop`、`SessionEnd`がすべて発火するかは未確認です。
- Desktopでのイベント遅延、重複、取りこぼしは未確認です。

## 次の判定条件

Desktopの実セッションで少なくともturn開始、承認要求、turn停止の記録を取得します。

各記録で、イベント名、匿名化セッション識別子、匿名化turn識別子、観測時刻を確認します。

この条件を満たすまで、productionのbridge、receiver、Session State Store、HUDを追加しません。
