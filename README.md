# stakeout

エージェント駆動ネイティブ C デバッグ基盤。

Visual Studio 上で動くネイティブ C コード（C# ホスト + C/C++ DLL）に対して、
LLM エージェントがブレークポイント・データブレークポイント・変数読み取りを行い、
「どこが壊したか」を特定するための CLI とデーモンです。

設計は [docs/design.md](docs/design.md) が正。判断の記録は
[docs/decisions/](docs/decisions/)、進捗は [docs/phase-log.md](docs/phase-log.md)。

## 構成

| 実行ファイル | 役割 |
|---|---|
| `stakeoutd.exe` | 常駐デーモン。Visual Studio への COM 接続・ログ・カーソルを保持する |
| `stakeout.exe`  | CLI。1 コマンド = 原則 1 RPC |
| `stakeout-mcp.exe` | MCP の stdio サーバー。シェルを持たないクライアント向け |

エージェント向けの使い方は [skills/stakeout/SKILL.md](skills/stakeout/SKILL.md) にあります。

## できること

```bash
stakeout targets                       # アタッチできるプロセス（allowlist 適用後）
stakeout attach --pid 1234             # Visual Studio 経由でアタッチ
stakeout run-until 'state.c:142' --cond 'g_ctx.state == 7' --expr 'g_ctx.tick'
stakeout watch-until-change '{,,NativeLib.dll}g_shared.counter'
stakeout find-corruption '{,,NativeLib.dll}g_shared.counter'
stakeout dump 'g_ctx' --depth 2
stakeout mem '&g_ctx' -n 64            # 生のメモリ（型が信用できないとき。実機未検証）
stakeout task-map                      # スレッドとタスク名の対応
stakeout code writers g_shared.counter # 静的な書き込み候補（gtags）
stakeout doctor                        # この環境で何が分かっていて何が分からないか
stakeout detach                        # Target は生かしたまま
```

新しい Target を調べ始めるときは、まず `stakeout doctor` を実行してください。
design.md §3.2 の確認項目（プロセス構成・タスクとスレッドの対応・bitness・
昇格レベル・ASan の可否）に、実機を見て答えます。
**答えられないものは「答えられない」と、次に何を見ればよいかを返します。**

エージェントから使うときは常に `--json` を付けます。結果は `data` に入ります。

```
$ stakeout status --json
{ "ok": true, "data": { "version": "1.0.0", "pid": 18764, ... } }
```

## 使い始めるまで

配布物はありません。**自分でビルドします。**

### 1. .NET を入れる

要るものが 2 つあり、**別物です。**

| | 要件 |
|---|---|
| ビルド | `net8.0` を対象にできる .NET SDK。**8 に限りません**（9 / 10 でも通ります） |
| 実行 | **.NET 8 Desktop Runtime**。デーモンは `Microsoft.WindowsDesktop.App` の 8.x を要求します |

まず今の状態を見てください。

```powershell
dotnet --list-sdks
dotnet --list-runtimes | Select-String WindowsDesktop
```

`dotnet` が無い、または `Microsoft.WindowsDesktop.App 8.` の行が無ければ入れます。

```powershell
winget install Microsoft.DotNet.SDK.8
winget install Microsoft.DotNet.DesktopRuntime.8
```

新しい SDK が既にあるなら、SDK の追加は要りません。
（この道具は SDK 10.0.201 だけの環境でビルドと検証を通しています。）

Visual Studio はビルドには不要です。**実行時には必要です**（stakeout はそれを操作します）。

### 2. ビルドする

```
dotnet build
pwsh tests/run.ps1
```

`dotnet test` は使えません。xunit v3 のテストを検出できないためです（ADR 0010）。

### 3. 実行ファイルを 1 か所にまとめる

**`stakeoutd.exe` は `stakeout.exe` と同じフォルダに置いてください。** CLI は自分の
隣からデーモンを探します。別の場所に置く場合は環境変数 `STAKEOUT_EXE` で指定します。

```powershell
dotnet publish src/Stakeout.Cli    -c Release -o dist
dotnet publish src/Stakeout.Daemon -c Release -o dist
dotnet publish src/Stakeout.Mcp    -c Release -o dist
```

`dist` に `stakeout.exe` / `stakeoutd.exe` / `stakeout-mcp.exe` が並びます。
この `dist` を PATH に通してください。

動かす機械に .NET を入れられない場合は、単体実行形式にできます。

```powershell
dotnet publish src/Stakeout.Cli -c Release -r win-x64 --self-contained -o dist
```

**ダウンロードした実行ファイルの起動が止められる環境**では、その機械で
クローンしてビルドしてください。運ぶのがソースだけになるので、生成物は
その機械で作られたものになります。`--self-contained` を付ければ、
.NET ランタイムを入れる必要もありません（SDK は要ります）。

管理者権限なしで SDK を入れるなら、Microsoft の導入スクリプトが使えます。
`%USERPROFILE%\.dotnet` に展開するだけで、既存の .NET は消しません。

```powershell
Invoke-WebRequest https://dot.net/v1/dotnet-install.ps1 -OutFile dotnet-install.ps1
./dotnet-install.ps1 -Channel 8.0
```

### 4. 調査対象に `.stakeout.json` を置く

**これを書くまで何にもアタッチできません。** `allowProcesses` は空だと全拒否です
（design.md §15.3）。既定で何にでも繋がる道具にはしていません。

```jsonc
{
  "allowProcesses": ["^MyProductHost\\.exe$"],
  "code": { "gtagsRoot": "C:\\src\\product" }
}
```

`stakeout` は**実行したディレクトリ**から `.stakeout.json` を探します。
調査対象のリポジトリ直下に置き、そこで実行してください。

### 5. Visual Studio を開く

スタートウィンドウを抜けて、**モーダルダイアログが出ていない状態**にします。
更新通知が 1 つ出ているだけで DTE は一切応答しません。

### 6. 動かす

```
stakeout targets                  # allowlist に一致するプロセスが出るか
stakeout attach --pid 1234
stakeout doctor                   # ここから始める
```

デーモンは最初のコマンドで自動起動します（最大 5 秒待ちます）。
止めるときは `stakeout daemon stop`。

`doctor` が出す「注意」と「不明」が、その環境で気をつけることの一覧になります。
とくにタスク名が 1 つも付かない場合は、`doctor` の `entryFunction` を見て
`.stakeout.json` の `tasks.taskEntryPatterns` を実物に合わせてください。

## 動作環境

- Windows 10/11 x64
- **ビルドに**: `net8.0` を対象にできる .NET SDK（8 / 9 / 10 のいずれでも可）
- **実行に**: .NET 8 Desktop Runtime。デーモンは `Microsoft.WindowsDesktop.App` を
  要求するので、`Microsoft.NETCore.App` だけでは起動しません
- Visual Studio。ProgID は固定せず、起動中のものを ROT から自動検出します（ADR 0002）。
  **動かして確認したのは DTE 18.0 の 1 版だけです。** 設計上は 2019（DTE 16.0）以降を
  想定していますが、そちらは未検証です（ADR 0001）
- GNU GLOBAL（`code *` を使う場合）

Visual Studio がスタートウィンドウのままだったり、モーダルダイアログを表示していると、
DTE は一切応答しません。stakeout は待たずにその旨を返します（ADR 0004）。

## MCP から使う

シェルを持たないクライアント（Claude Desktop など）向けに stdio サーバーがあります。
出すのは「調査 1 手」になっている道具だけで、低レベル操作は載せていません。
細かい操作が要るときは CLI を使ってください。

```jsonc
{
  "mcpServers": {
    "stakeout": { "command": "C:\\path\\to\\stakeout-mcp.exe" }
  }
}
```

```
stakeout-mcp.exe --tools    # 道具の定義とその大きさを出す
```

## 設定

`./.stakeout.json` → `%APPDATA%\stakeout\stakeout.json` の順で探し、前者が勝ちます。
入れ子は深くマージされるので、`limits.waitSec` だけを上書きしても他の既定値は残ります。

```jsonc
{
  "backend": "envdte",
  "allowProcesses": ["^MyProductHost\\.exe$"],  // 空だと全拒否
  "code": { "gtagsRoot": "C:\\src\\product" },
  "tasks": {
    "taskEntryPatterns": [ { "pattern": "^Task_(\\w+)_Main$", "name": "$1" } ]
  }
}
```

`code *` は gtags の索引を読みます。索引はリポジトリに入れないので、
`gtagsRoot` に指定した場所で一度 `gtags` を実行してください。
ソースを変えたら `global -u` で更新します。

## 検証

```
pwsh tests/run.ps1                                    # 単体 190 件
pwsh tests/integration/phase1.ps1 -VsPid <devenv pid> # アタッチ〜デタッチ
pwsh tests/integration/phase2.ps1 -VsPid <devenv pid> # 複合コマンドとページング
pwsh tests/integration/phase4.ps1 -VsPid <devenv pid> # Code Index と find-corruption
pwsh tests/integration/phase7.ps1 -VsPid <devenv pid> # MCP の会話
pwsh eval/run.ps1 -Case BUG_04 -VsPid <devenv pid>    # エージェントが解けるか
```

統合検証は実際の Visual Studio と `samples/target` を使うので CI では動きません。
`eval/run.ps1` は Claude Code を起動するため API の利用料がかかります。

## まだできないこと

- **Visual Studio 無しでの調査。** Backend は EnvDTE だけです。Windows 同梱の
  dbgeng では侵入アタッチが成立しないことを確認しています（ADR 0018、ADR 0019）
- **数百ヒット規模のトレースと、実行同士の比較。** `trace-expr` は 1 ヒットあたり
  160〜400 ms かかるため、少数の観測点向けです
- **ダンプの取得と解析**

内部の進捗は [docs/phase-log.md](docs/phase-log.md)、設計上の判断は
[docs/decisions/](docs/decisions/) にあります。

## ライセンス

MIT。[LICENSE](LICENSE) を参照してください。
