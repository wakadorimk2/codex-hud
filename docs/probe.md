# Codex State Probe 技術検証計画

## Probeの問い

Codex Desktopを通常通り利用したまま、外部プロセスからCodexセッションの状態を十分な精度と遅延で検知できるかを検証します。

Probeは既存Codexの実行方法を変更しません。

ProbeはCodexをラップしません。

## 検証の境界

- Codex Desktopを最初の検証対象にします。
- Codex CLIは、Desktopで得られない情報を比較する補助対象にします。
- 既存のCodexプロセスへコードを注入しません。
- Codexの起動方法、承認方法、通常の利用手順を変更しません。
- `%USERPROFILE%\.codex`の調査は読み取り専用で行います。
- 生ログを共有しません。トークン、個人情報、プロジェクト固有情報を記録から除外またはマスクします。
- 未確認のJSONL形式や内部イベント名を、確定した仕様として扱いません。

## 初回実装

初回実装は、`experiments/probe.py` の読み取り専用JSONL Probeです。

Probeは、指定した一つのJSONLファイルだけを読みます。

Probeは、ファイルを変更しません。

Probeは、Codexを起動、操作、ラップしません。

fixtureでの再現試験は、次のコマンドで実行します。

```powershell
python -B experiments\probe.py --fixture experiments\fixtures\probe_sample.jsonl --once
python -B -m unittest discover -s experiments -v
```

実セッションの観測では、対象ファイルを明示します。

```powershell
python -B experiments\probe.py --session-file <session-jsonl-path> --once
python -B experiments\probe.py --session-file <session-jsonl-path> --follow --poll-ms 250
```

`--follow` は現在のファイル末尾から追跡します。

改行前の行は、次の改行まで保留します。

ファイルサイズが小さくなった場合は、読み取り位置を先頭へ戻します。

出力は匿名化したJSONLです。

セッションIDとturn IDは短縮SHA-256へ変換します。

本文、入力、出力、引数、結果、作業ディレクトリ、生のパスは出力しません。

`task_started` と `task_complete` は、実際に観測したイベントとして暫定分類します。

`custom_tool_call` の `status=completed` は、turn完了の根拠にしません。

未確認のイベントは `Unknown` と記録します。

`WaitingForUser` と `WaitingForApproval` は、根拠を確認するまで分類しません。

2026-08-12のローカルJSONLサンプルでは、`event_msg`、`response_item`、`turn_context` などを観測しました。

同サンプルでは、`task_started` と `task_complete` を観測しました。

同サンプルでは、待機状態を示す根拠を確認できませんでした。

この初回実装の判定は `Partial` です。

内部イベント名は公開仕様として扱いません。

## App Server検証

App Server検証は、`experiments/app_server_probe.py` で行います。

Probeは、現在の `codex` CLIを `app-server --stdio` で起動します。

Probeは、既存のCodex Desktopへ接続しません。

Probeは、`ephemeral: true`、`sandbox: read-only`、`approvalPolicy: never` の専用threadを作ります。

専用turnの入力は固定文です。

```powershell
python -B experiments\app_server_probe.py --timeout 90
```

`--no-turn` を指定すると、initializeだけを確認できます。

```powershell
python -B experiments\app_server_probe.py --no-turn
```

出力はJSONLです。

Probeは、JSON-RPCの方向、種別、method、キー名、匿名化した識別子を出力します。

Probeは、message本文、引数、パス、結果、エラー本文を出力しません。

`thread/status/changed` では、`status_type` と `active_flags` の列挙値だけを出力します。

App Serverの終了後に、Desktopの既存PID、ウィンドウ有無、`session_index.jsonl` の匿名化スナップショットを比較します。

2026-08-12の実環境検証では、次を確認しました。

- `initialize` 応答を取得しました。
- `thread/start` と `turn/start` の応答を取得しました。
- `thread/started`、`turn/started`、`thread/status/changed`、`turn/completed` を取得しました。
- App Serverプロセスは終了しました。
- Desktopの既存PID 2件を前後で確認しました。
- Desktopのウィンドウ有無を前後で確認しました。
- `session_index.jsonl` の件数と匿名化ID集合は前後で不変でした。
- 判定は `App Serverイベント取得: Supported` です。
- 判定は `Desktop共存: Supported` です。

この結果は、別プロセスの専用App ServerとDesktopが共存できたことを示します。

この結果は、既存DesktopセッションをApp Serverから観測できることを示しません。

App Serverの内部イベント名は、今回のCLIバージョンで観測した事実として扱います。

## Hook検証

Hook検証は、`experiments/hook_probe.py`で行います。

ProbeはHookの標準入力を読み取ります。

Probeは、イベント名、匿名化したセッション識別子、匿名化したturn識別子、匿名化した作業ディレクトリ識別子、観測時刻、トップレベルキー名だけを記録します。

Probeは、prompt、command、tool input、パス、モデル名、メッセージ本文を記録しません。

Hookの追加候補は、`tools/install-hooks.ps1`で確認します。

スクリプトは既定でdry-runです。

`-Apply`を指定した場合だけ、既存のユーザーHook設定へ追加します。

現在のCodex Desktopでは`async` Hookが未対応のため、Probeは同期Hookとして登録します。

Probeは短時間で終了し、Hookの入力を保持してから終了します。

既存Hookのバックアップを作成します。

Desktopの実発火とTrustを確認する手順は、`docs/hooks-setup.md`に記録します。

2026-08-13時点で、Desktop実セッションから`SessionStart`、`UserPromptSubmit`、`PermissionRequest`、`Stop`を確認しました。

`SessionEnd`の実発火は未確認です。

この結果を根拠として、productionのbridge、Named Pipe receiver、Session State Store、HUDを実装しました。

productionの実装は`docs/implementation.md`に記録します。

## 調査対象の優先順位

最も単純で壊れにくい観測方法から調べます。

| 順位 | 対象 | 目的 | 進む条件 |
| --- | --- | --- | --- |
| 1 | `%USERPROFILE%\.codex`の保存ファイル、JSONL、ログ | セッションID、状態、turn、エラー、承認要求の痕跡を確認する | 状態根拠とセッション単位の対応を記録できる場合 |
| 2 | `FileSystemWatcher`などの更新検知 | 状態変化の検知遅延と更新単位を測る | ファイル内容の観測だけでは遅延または取りこぼしがある場合 |
| 3 | プロセスとウィンドウの対応 | セッションIDとプロセス、HWND、表示名の対応を確認する | 複数セッションの識別が不足する場合 |
| 4 | Codex App Server | 公式または通常利用で観測できるイベントモデルを確認する | 前段階で`WaitingForUser`と`WaitingForApproval`を区別できない場合 |
| 5 | CLIのstdout、stderr | CLIで利用可能な状態根拠を比較する | Desktop側の観測を補足できる場合 |
| 6 | その他の低侵襲な方法 | 前段階で不足した情報を補う | 採用理由と安全性を説明できる場合 |

App ServerやCLIを調べる場合も、通常のCodex利用をラップしたり、操作を差し替えたりしません。

特別な接続や未確認の内部APIが必要な場合は、利用可能性と侵襲性を先に記録します。

## 実験プロトコル

1. 観測対象のセッションを一つ作ります。
2. 実験開始時刻、セッション識別子の候補、対象プロジェクト名を記録します。
3. 一回の実験では一つの操作だけを行います。
4. Codex側の操作時刻と、外部から根拠を観測した時刻を記録します。
5. 観測した事実と、状態への解釈を分けて記録します。
6. 同じ操作を複数回行い、遅延、取りこぼし、重複を比較します。
7. 複数セッションの実験では、セッションを同時に進め、識別の混同を確認します。

各観測記録に、次の情報を含めます。

- 実験IDと操作
- セッションIDの候補
- 情報源とイベント種別
- 操作時刻と観測時刻
- 検出遅延
- 観測した事実
- 判断した状態
- 状態判断の根拠
- 未確認事項、取りこぼし、重複

判断根拠を説明できない状態遷移は、成功した状態検知として扱いません。

## 実験項目

| 実験 | 操作 | 確認すること |
| --- | --- | --- |
| セッション開始 | Codex Desktopで新しいセッションを開始する | セッションID、作成時刻、プロジェクト対応を取得できるか |
| `Running` | 通常のturnを実行する | 実行中の根拠を取得できるか。プロセス存在だけに依存していないか |
| ユーザーへの質問 | Codexが通常の回答を待つ操作を行う | `WaitingForUser`を明示的に識別できるか |
| command approval | コマンド承認が必要な操作を行う | `WaitingForApproval`と承認種別を識別できるか |
| file change approval | ファイル変更承認が必要な操作を行う | command approvalと区別できる根拠を取得できるか |
| turn completion | 成功するturnを完了する | 明示的な完了根拠を取得できるか |
| error | エラーになる操作を行う | 明示的なエラー根拠を取得できるか |
| 新しいturn | 待機または完了後に新しいturnを開始する | 現在状態を`Running`へ戻せるか |
| 複数セッション | 複数のCodexセッションを同時に利用する | セッション、状態、プロジェクトを混同しないか |

## 状態判定規則

単一の観測根拠に対して、Probeは次の分類を行います。

1. 明示的なエラー根拠があれば`Error`とします。
2. 明示的なユーザー質問があれば`WaitingForUser`とします。
3. 明示的なcommand approvalまたはfile change approvalがあれば`WaitingForApproval`とします。
4. 明示的なturn完了根拠があれば`Completed`とします。
5. 新しいturnまたは実行中のturnの根拠があれば`Running`とします。
6. 根拠が不足する場合は`Unknown`とします。

同じ観測バッチに複数の根拠がある場合は、情報源が提供するイベント順序を優先します。

イベント順序がない場合は観測時刻を優先します。

観測時刻も同じ場合は、状態を推測で選びません。

競合した根拠をすべて記録し、現在状態を`Unknown`として保持します。

無更新、プロセス終了、ウィンドウ非表示だけでは、`Completed`または`Error`に遷移しません。

`WaitingForUser`と`WaitingForApproval`は、時間経過だけで解除しません。

## 成功条件

次の条件を、反復実験で確認できた場合にProbeの検証を成功とします。

- 既存のCodex Desktopを変更しない。
- Codexをラップまたは操作しない。
- 状態変化を数秒以内に検出できる。実測値を記録する。
- `WaitingForUser`と`WaitingForApproval`を区別できる。
- command approvalとfile change approvalの根拠を区別または明示的に区別不能と記録できる。
- 複数セッションを識別できる。
- 状態遷移ごとに根拠を説明できる。
- 取りこぼし、重複、遅延を再現可能な形で記録できる。
- 秘密情報を実験記録へ残さない。

「状態を取得できた」という結論は、状態名だけでなく、根拠と遅延を含めて判断します。

## Failure / fallback

### ファイルの内容は読めるが、更新検知が遅い場合

ファイルの読み取りとファイル更新通知を分けて測定します。

FileSystemWatcherの遅延、重複、取りこぼしを確認します。

更新通知だけで状態を決定せず、必要な場合はファイル内容を再読み取りします。

### 状態は読めるが、セッションを識別できない場合

プロセス、ウィンドウ、HWND、プロジェクト表示名との対応を補助的に調べます。

対応が一意にならない場合は、複数セッション対応を成功としません。

### `WaitingForUser`と`WaitingForApproval`を区別できない場合

App Serverの通常利用可能なイベントモデルを調べます。

次にCLIのstdoutまたはstderrを補助根拠として調べます。

それでも区別できず、Codexの変更、ラップ、コード注入が必要になる場合は、その方法を採用しません。

### ファイル監視とApp Serverの結果が一致しない場合

両方の観測を別の根拠として保存します。

一致しない状態を推測で統合しません。

再現条件、時刻、対象セッション、情報源を記録し、Probeの成功範囲を限定します。

### 判定結果

- **Supported**: 成功条件を満たし、HUDへ渡せる根拠がある状態です。
- **Partial**: `Running`や`Completed`など一部の状態だけを扱える状態です。
- **Unsupported**: 必須の待機状態を低侵襲に区別できない状態です。

`Partial`または`Unsupported`の場合は、HUD実装を先行しません。

## Probe後の判断

Probeの結果を、次の事実と推測に分けて記録します。

- 観測できた情報源
- 観測できた状態
- セッション識別の精度
- 検出遅延
- 取りこぼしと重複
- 未確認の挙動
- 採用または不採用にした観測方法

この記録をもとに、Session State StoreとHUDの設計を具体化します。
