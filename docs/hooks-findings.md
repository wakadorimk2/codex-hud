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
- Codex Desktopは、Probeの`async` Hookを`async hooks are not supported yet`としてスキップしました。

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
- Desktopのエラー表示から、現在の実行環境では`async` Hookが使えないことを確認しました。
- Probeを同期Hookとして登録する修正を追加しました。
- 2026-08-13 07:26-07:27 JSTに、Probe JSONLへ`SessionStart` 3件と`UserPromptSubmit` 2件を記録しました。
- 上記の記録では、`error_kind`はすべて空でした。
- 上記の記録は同期Probeの起動を示します。
- 2026-08-13の同一Probe JSONLで、`PermissionRequest`と`Stop`の記録も確認しました。
- `SessionStart`、`UserPromptSubmit`、`PermissionRequest`、`Stop`の実発火を確認しました。
- `SessionEnd`の実発火は未確認です。

### Unknown

- `SessionEnd`の実発火は未確認です。
- Desktopでのイベント遅延、重複、取りこぼしは未確認です。

### Compatibility note

OpenAI Docsは、`async`をバックグラウンドHookの設定として説明しています。

今回のDesktop実行環境は、その設定を受理しませんでした。

このリポジトリは、公開仕様よりも現在のDesktopで観測した互換性を優先し、Probeを同期Hookとして扱います。

## Production implementation status

production bridge、Named Pipe server、SessionStateStore、WPF/SkiaSharp lampを実装しました。

production bridgeは、Probeと同じHook payloadからイベント名だけを読み取ります。

production bridgeは、セッション識別子を短縮SHA-256へ変換します。

production bridgeは、Pipe停止時に終了コード0を返します。

production Hookへの切替は、`docs/hooks-setup.md`のバックアップ付き手順を使います。

production切替後のDesktop E2Eで、HUDの位置、クリック透過、フォーカス非取得を確認します。

2026-08-13 08:10 JSTに、ユーザーHookをProbeからproduction bridgeへ切り替えました。

切替時に`hooks.json.backup-<timestamp>`を作成しました。

Probe commandは対象5イベントから削除しました。

Enterlightの`PermissionRequest`、`PreToolUse`、`Stop`、`UserPromptSubmit`は保持しました。

Hook commandのハッシュが変わったため、Codex Desktopの再起動とTrust確認が必要です。

## 次の判定条件

Desktopの実セッションで少なくともturn開始、承認要求、turn停止の記録を取得しました。

各記録で、イベント名、匿名化セッション識別子、匿名化turn識別子、観測時刻を確認します。

この条件を根拠として、productionのbridge、receiver、Session State Store、HUDを実装しました。
