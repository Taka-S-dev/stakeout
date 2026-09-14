# stakeout — エージェント駆動ネイティブCデバッグ基盤 設計書

- 版: 0.1（初版）
- 対象読者: 実装を担当する者と、レビュアー
- 状態: Phase 0〜4 と 7 が完了。Phase 5（DbgEng Backend）以降は未着手。現在地は docs/phase-log.md の末尾を見ること

---

## 0. この文書の使い方

1. 最初に §1〜§6 を読み、ドメインモデルと層構造を把握する。
2. 実装は §20 のフェーズ順に進める。**フェーズを飛ばさない。次フェーズの機能を先取りしない。**
3. 各フェーズの「完了条件」を満たしたら、`samples/` の検証手順を実行し、結果を `docs/phase-log.md` に追記してからコミットする。
4. 設計と矛盾する事実が判明したら、勝手に設計を変えず、`docs/decisions/NNNN-*.md`（ADR）に「発見した事実・選択肢・推奨」を書いてユーザーに確認する。
5. コミットは Conventional Commits。件名は英語・命令形。本文は利用者から見た変化の箇条書きにする。
6. `[要検証]` と付いた記述は、公式ドキュメントで裏付けが取れていない箇所。実装時に動作確認し、結果を ADR に残す。

---

## 1. 目的と非目的

### 1.1 目的

Visual Studio 2019 上で動く「組み込みソフトのPC版」（C# ホスト + C/C++ DLL、調査対象はほぼ C 部分）に対して、LLM エージェントが

- ブレークポイント・データブレークポイント・条件付きブレークポイント・トレースポイントを張り
- 実行・ステップ・停止を制御し
- 変数・構造体・メモリ・全スレッドのスタックを読み
- 実行トレースを蓄積して SQL で検索し
- 静的解析（gtags）と組み合わせて「どこが壊したか」を特定する

ための、**再利用可能でエージェント非依存な**デバッグ基盤を作る。

### 1.2 設計原則

| 原則 | 意味 |
|---|---|
| 止めない調査を優先 | トレースポイント・トレースDBが主戦力。ステップ実行は最後の手段 |
| 1呼び出しの情報量を最大化 | 複合ツール（`run-until` 等）で「調査1手」を1コマンドにする |
| デーモン + CLI がコア | MCP は薄いアダプタ。人間もエージェントも同じ CLI を使う |
| バックエンド差し替え可能 | EnvDTE(VS2019) と DbgEng を同一 IF で扱う。将来 gdb を追加できる |
| 全部ログに残す | 全 RPC の入出力を JSONL に記録。調査結果の根拠にする |
| 有限時間で必ず返る | 全操作にタイムアウト。待ちは `running` で返してポーリング |

### 1.3 非目的（今回作らないもの）

- VS の GUI 拡張（VSIX）
- マネージド（C#）側のデバッグ機能。C# フレームは `[External Code]` として扱う
- 自動修正（コードを書き換える機能）。調査・特定まで
- Linux / macOS 対応
- カーネルモードデバッグ

---

## 2. 用語

| 用語 | 定義 |
|---|---|
| Target | デバッグ対象プロセス（C# ホストの exe。中で C DLL が動く） |
| Backend | デバッガエンジンへの接続実装。`EnvDteBackend` / `DbgEngBackend` |
| Session | Backend 経由で1つの Target に接続した状態。DAP の1セッションに相当 |
| Task | 組み込みソフト側のタスク。PC版ではスレッドに対応する（§3 で確認） |
| Stop | Target が停止したイベント。理由（ブレークポイント/例外/ステップ完了/ユーザー要求） |
| Tracepoint | 停止せずに式の値を記録するブレークポイント |
| Trace DB | Tracepoint 等で収集したイベントを蓄積する SQLite |
| Composite | 複数の低レベル操作を組み合わせた高水準コマンド |
| Code Index | gtags による静的シンボル索引 |

---

## 3. 前提と未確定事項

### 3.1 確定している前提

- OS: Windows 10/11 x64
- IDE: Visual Studio 2019 以降。ProgID は固定せず ROT から版数非依存で解決する（ADR 0001 / ADR 0002）
- Target: C# exe が C/C++ DLL を P/Invoke または C++/CLI 経由で呼ぶ。調査対象は C 側
- PDB はローカルに存在し、VS で通常デバッグできている
- 実装言語: C# / .NET 8（Windows 専用でよい）
- 開発者環境: シェル経由で CLI を呼べるエージェント、gtags 利用可

### 3.2 実装前に必ず確認すること

**`stakeout doctor` が答える。** この表を人が埋めるものにしておくと、埋まらないまま実装が進む。
実機で `stakeout attach` してから `stakeout pause` → `stakeout doctor` を実行すれば、Q1〜Q8 のうち
道具が答えられるものは埋まり、答えられないものは「答えられない」と、次に何を見るかが出る。

Q3（同時に実行中のスレッドは常に 1 本か）は**道具では判定できない**。
1 回の観測で「常に」は言えないためである。分かったふりをしない。


| # | 確認項目 | 確認方法 | 結果 |
|---|---|---|---|
| Q1 | Target は単一プロセスか複数プロセスか | ブレーク中に「デバッグ→ウィンドウ→プロセス」の行数 | （未記入） |
| Q2 | Task はスレッドに対応しているか | 「デバッグ→ウィンドウ→スレッド」でスレッド数と場所列 | （未記入） |
| Q3 | 同時に「実行中」のスレッドは常に1本か（スケジューラエミュレーション） | 同上で実行中スレッドの本数 | （未記入） |
| Q4 | スレッドに名前が付いているか | スレッドウィンドウの名前列 | （未記入） |
| Q5 | Target の bitness（x86 / x64） | プロセスウィンドウまたは PDB | （未記入） |
| Q6 | cdb.exe / dbgeng.dll の有無 | `C:\Program Files (x86)\Windows Kits\10\Debuggers\{x64,x86}\` | （未記入） |
| Q7 | VS を管理者権限で起動しているか | タスクマネージャの「昇格」列 | （未記入） |
| Q8 | ASan（/fsanitize=address）が使えるツールセットか | VS 16.9 以降かつ x64/x86 | （未記入） |

### 3.3 設計上の仮定（Q1〜Q3 が判明したら見直す）

- **仮定 A（本設計の既定）**: 単一プロセス・複数スレッド。ブレークで全スレッドが止まる。
- 仮定 A が崩れた（複数プロセス）場合は付録 C「複数プロセス対応」を有効化する。Phase 1 の Backend IF はどちらにも対応できるよう `SessionGroup` を薄く残す。

---

## 4. アーキテクチャ

```
┌────────────────────────────────────────────────────────────────┐
│ エージェント（オーケストレータ）                                  │
│   skills/stakeout/SKILL.md ← 調査の型・コマンド一覧                     │
└───────────────┬────────────────────────────┬───────────────────┘
                │ Bash                        │ MCP (Phase 7)
        ┌───────▼───────┐             ┌───────▼───────┐
        │ stakeout.exe (CLI) │             │ stakeout-mcp.exe   │
        └───────┬───────┘             └───────┬───────┘
                │ JSON-RPC 2.0 / named pipe    │
        ┌───────▼─────────────────────────────▼───────┐
        │ stakeoutd.exe（常駐デーモン）                       │
        │  ┌──────────────┐ ┌───────────┐ ┌─────────┐ │
        │  │ Composite     │ │ Trace DB  │ │ Code    │ │
        │  │ (run-until…)  │ │ (SQLite)  │ │ Index   │ │
        │  └──────┬───────┘ └───────────┘ └─────────┘ │
        │  ┌──────▼───────────────────────────────┐    │
        │  │ IDebuggerBackend（DAP 語彙）           │    │
        │  └──────┬──────────────────┬────────────┘    │
        │  ┌──────▼──────┐   ┌───────▼─────────┐       │
        │  │ EnvDte      │   │ DbgEng (Phase 6) │       │
        │  │ (STA thread)│   │ (thread/session) │       │
        │  └──────┬──────┘   └───────┬─────────┘       │
        │  Session Log (JSONL)                          │
        └─────────┼──────────────────┼──────────────────┘
                  │ COM/ROT          │ dbgeng.dll
          ┌───────▼───────┐  ┌───────▼───────┐
          │ Visual Studio │  │ Target 直接    │
          │ 2019          │  │               │
          └───────┬───────┘  └───────┬───────┘
                  └────────┬─────────┘
                   ┌───────▼────────┐
                   │ Target (C# exe │
                   │  + C DLL, PDB) │
                   └────────────────┘
```

### 4.1 プロセス構成

| プロセス | 役割 | 生存期間 |
|---|---|---|
| `stakeoutd.exe` | Backend 接続を保持。RPC を受ける。Trace DB / Code Index / Log を持つ | 常駐。CLI が起動時に無ければ自動起動 |
| `stakeout.exe` | RPC クライアント。1コマンド = 原則1 RPC | コマンドごと |
| `stakeout-mcp.exe` | MCP stdio サーバー。RPC への薄いマッピング | MCP クライアントの生存期間 |

### 4.2 なぜデーモンか

デバッガ接続（VS への COM 参照、DbgEng クライアント）は呼び出しをまたいで保持する必要がある。CLI プロセスごとに接続すると、セッション状態（ブレークポイント、現在スレッド、トレース）が持てない。

### 4.3 なぜ CLI をコアにするか

- MCP のツール定義はコンテキストを消費する。CLI は `--help` を Skill に書くだけ
- エージェントが1ターンで複数コマンドをシェルで連鎖できる
- 人間が同じ道具で動作確認できる
- CLI の入出力がそのままゴールデンテストになる
- エージェント実装に依存しない

MCP が必要になる場面（シェルの無いクライアント、引数のスキーマ検証）のために Phase 7 でアダプタを足す。

---

## 5. ドメインモデル（DAP 語彙）

内部の型は Debug Adapter Protocol の概念をそのまま借りる。自前の語彙を発明しない。

```csharp
record SessionInfo(string SessionId, string Backend, int Pid, string ProcessName,
                   string Bitness, SessionState State);

enum SessionState { Attached_Running, Attached_Stopped, Detached }

record ThreadInfo(int ThreadId, string Name, string? TaskName, bool IsFrozen,
                  bool IsCurrent, string TopFunction);

record StackFrame(int FrameId, int Depth, string Function, string? File, int? Line,
                  ulong Address, string Module, bool IsExternal /* [External Code] */);

record Variable(string Name, string Type, string Value, ulong? Address,
                int VariablesReference /* 0 = 子なし */, bool IsPointer, bool IsValid);

enum BreakpointKind { Line, Function, Address, Data }

record Breakpoint(int BreakpointId, BreakpointKind Kind, string Location,
                  string? Condition, int? HitCount, bool BreakWhenHit /* false = tracepoint */,
                  string[]? TraceExpressions, bool Verified, string? VerifyMessage);

enum StopReason { Breakpoint, DataBreakpoint, Exception, Step, Pause, EntryPoint, Exit, Unknown }

record StopEvent(string SessionId, StopReason Reason, int ThreadId, int? BreakpointId,
                 string? ExceptionCode, string? Description, DateTimeOffset At);

record TraceEvent(long EventId, string RunId, long TsQpc, DateTimeOffset TsWall,
                  int ThreadId, string? TaskName, string Kind /* tp|step|stop|user */,
                  string Location, string Function, string? Expr, string ValueJson,
                  string? StackJson);
```

### 5.1 状態遷移

```
Detached ──attach/launch──▶ Attached_Running ──stop event──▶ Attached_Stopped
    ▲                             ▲                               │
    │                             └────────continue/step──────────┘
    └──────────────────────────detach/target exit──────────────────┘
```

状態に対する前提条件違反（Running 中に `stack` を要求 等）は `PRECONDITION` エラーで返し、エラーメッセージに「今できること」を含める（§15）。

---

## 6. Backend インターフェース

```csharp
interface IDebuggerBackend : IAsyncDisposable
{
    string Name { get; }                       // "envdte" | "dbgeng"
    Task<SessionInfo> AttachAsync(int pid, AttachOptions opt, CancellationToken ct);
    Task<SessionInfo> LaunchAsync(LaunchOptions opt, CancellationToken ct);
    Task DetachAsync(CancellationToken ct);

    // 実行制御。すべて「要求を出したら即返る」。停止は WaitForStopAsync で待つ
    Task ContinueAsync(CancellationToken ct);
    Task PauseAsync(CancellationToken ct);
    Task StepAsync(StepKind kind /* Over|Into|Out */, int threadId, CancellationToken ct);
    Task<StopEvent?> WaitForStopAsync(TimeSpan timeout, CancellationToken ct); // null = timeout

    // 停止中のみ有効
    Task<IReadOnlyList<ThreadInfo>> GetThreadsAsync(CancellationToken ct);
    Task SetCurrentThreadAsync(int threadId, CancellationToken ct);
    Task FreezeThreadAsync(int threadId, bool freeze, CancellationToken ct);
    Task<IReadOnlyList<StackFrame>> GetStackAsync(int threadId, int maxDepth, CancellationToken ct);
    Task<IReadOnlyList<Variable>> GetScopeAsync(int frameId, ScopeKind kind /* Locals|Arguments */, CancellationToken ct);
    Task<Variable> EvaluateAsync(string expr, int? frameId, EvalOptions opt, CancellationToken ct);
    Task<IReadOnlyList<Variable>> ExpandAsync(int variablesReference, CancellationToken ct);
    Task<byte[]> ReadMemoryAsync(ulong address, int length, CancellationToken ct);

    // ブレークポイント。Running 中でも設定可能
    Task<Breakpoint> SetBreakpointAsync(BreakpointRequest req, CancellationToken ct);
    Task RemoveBreakpointAsync(int breakpointId, CancellationToken ct);
    Task<IReadOnlyList<Breakpoint>> ListBreakpointsAsync(CancellationToken ct);
    Task SetExceptionBreakAsync(string[] codes, bool breakWhenThrown, CancellationToken ct);

    // Tracepoint の出力を引き取る（Backend ごとに実装が違う。§10.6 / §11.4）
    IAsyncEnumerable<TraceEvent> DrainTraceEventsAsync(CancellationToken ct);

    Task<IReadOnlyList<ModuleInfo>> GetModulesAsync(CancellationToken ct);
    BackendCapabilities Capabilities { get; }  // DataBreakpoint, Tracepoint, ReadMemory, Dump, TTD, ParallelSessions
}
```

**式の構文は Backend 依存であり、まだ抽象化されていない（ADR 0020）。**
モジュール修飾（VS は `{,,DLL名}`、DbgEng は `モジュール!シンボル`）と書式指定子が、
Core と Rpc と Skill に VS の形のまま埋まっている。Backend が 1 つの間は実害が無いが、
Phase 5 に着手する前に ADR 0020 のチェックリストを片付けること。

`BackendCapabilities` を見て Composite 層が動作を切り替える（EnvDTE で Tracepoint が使えない場合の auto-continue フォールバック等）。

`SessionGroup`（複数セッション束ね）は Phase 1 では「セッション1つを持つだけの薄いクラス」として存在させる。付録 C を有効化するときに中身を足す。

---

## 7. RPC API（stakeout）

- トランスポート: 名前付きパイプ `\\.\pipe\stakeout-<ユーザー名>`。ACL は現在ユーザーのみ
- プロトコル: JSON-RPC 2.0（`StreamJsonRpc` を使用）
- すべてのメソッドに `timeoutMs`（既定 30000）を任意で渡せる
- 結果は必ず `{ ok: true, data: … }` または `{ ok: false, error: {code, message, hint} }`（§15）
- 大きい結果は `{ data, cursor, truncated: true }` で分割。`cursor` を次回に渡す（§8.5）

### 7.1 メソッド一覧

| メソッド | 状態要件 | 概要 |
|---|---|---|
| `daemon.ping` / `daemon.status` / `daemon.shutdown` | — | 稼働確認、セッション一覧、停止 |
| `target.list` | — | アタッチ可能プロセス一覧（allowlist 適用後） |
| `session.attach {pid|name, backend?, engines?}` | Detached | アタッチ。既定 Native のみ |
| `session.launch {exe, args, cwd, backend?}` | Detached | 起動してアタッチ |
| `session.detach` | Attached | デタッチ（BP は全削除） |
| `session.info` | — | SessionInfo |
| `exec.continue` / `exec.pause` | 各 | 実行制御 |
| `exec.step {kind, threadId?}` | Stopped | ステップ |
| `exec.wait {timeoutMs}` | — | 停止を待つ。`{stopped: bool, stop?: StopEvent}` |
| `threads.list` | Stopped | ThreadInfo[]（TaskName 付き） |
| `threads.select {threadId|taskName}` | Stopped | 現在スレッド変更 |
| `threads.freeze {threadId|taskName, freeze}` | Stopped | 凍結/解凍 |
| `stack.get {threadId?|all:true, maxDepth?}` | Stopped | スタック。`all` で全スレッド |
| `vars.scope {frameId, kind}` | Stopped | ローカル/引数 |
| `vars.eval {expr, frameId?, format?}` | Stopped | 式評価 |
| `vars.expand {ref}` | Stopped | 子要素 |
| `vars.dump {expr, depth, maxItems}` | Stopped | 再帰展開（Composite） |
| `mem.read {address, length, format}` | Stopped | メモリ |
| `bp.set {kind, location, condition?, hitCount?, tracepoint?: {exprs, stack?}}` | — | 設定 |
| `bp.remove {id}` / `bp.list` / `bp.clear` | — | 管理 |
| `bp.exceptions {codes[], breakWhenThrown}` | — | 例外ブレーク |
| `composite.runUntil {location, condition?, timeoutMs, capture: {stack, locals, exprs[]}}` | — | §9.1 |
| `composite.watchUntilChange {expr, timeoutMs, maxHits}` | — | §9.2 |
| `composite.traceExpression {exprs[], steps, kind}` | Stopped | §9.3 |
| `composite.traceCalls {function, exprs[], maxHits, timeoutMs, stack?}` | — | §9.4 |
| `composite.findCorruption {symbol, maxHits, timeoutMs}` | — | §9.5 |
| `composite.taskMap` | Stopped | §9.6 |
| `trace.runs` / `trace.query {sql}` / `trace.compare {runA, runB}` | — | §12 |
| `code.refs {symbol}` / `code.def {symbol}` / `code.writers {symbol}` | — | §13 |
| `log.tail {n}` / `log.export {sinceEventId}` | — | §14 |

### 7.2 並行性

- stakeout は RPC を並行に受けるが、**Backend への操作はセッションごとに直列化**する（`SemaphoreSlim(1,1)`）
- `exec.wait` は例外的に、他の読み取り系（`bp.list`, `session.info`）と並行できる
- 長時間操作（Composite）は内部で `exec.wait` を繰り返す。RPC 自体のタイムアウトは Composite の `timeoutMs` + 5 秒

---

## 8. CLI 仕様（stakeout）

### 8.1 起動と接続

- `stakeout` は起動時に名前付きパイプに接続を試み、失敗したら `stakeoutd.exe` を `--detach` で起動して最大 5 秒待つ
- `STAKEOUT_PIPE` 環境変数でパイプ名を上書きできる（テスト用）

### 8.2 出力

- 既定: 人間向けテキスト
- `--json` または環境変数 `DBG_JSON=1`: RPC の**封筒ごと** JSON で stdout（ADR 0015）
  - 通常: `{ "ok": true, "data": ... }`
  - 打ち切られたとき: `{ "ok": true, "data": [...], "truncated": true, "cursor": "…" }`
  - 打ち切りとカーソルは本文と同じ流れで運ぶ。標準エラーに分けると、両方をまとめて受け取る側で JSON が壊れる
- エラーは常に stderr に `{ "ok": false, "error": {...} }`（`--json` 時）または1行テキスト
- **エージェントから使うときは常に `--json`**（Skill に明記）

### 8.3 終了コード

| コード | 意味 |
|---|---|
| 0 | 成功 |
| 1 | 実行時エラー（Backend エラー等） |
| 2 | 使い方エラー |
| 3 | タイムアウト / まだ実行中（`wait` で停止しなかった） |
| 4 | 前提条件違反（停止中でないのに `stack` 等） |
| 5 | 対象なし（プロセス未発見、allowlist 外） |

### 8.4 コマンド体系

```
stakeout status
stakeout targets [--all]
stakeout attach (--pid N | --name PATTERN) [--backend envdte|dbgeng] [--engines Native]
stakeout launch EXE [-- ARGS...] [--cwd DIR]
stakeout detach

stakeout continue | stakeout pause
stakeout step (over|into|out) [--thread ID|--task NAME]
stakeout wait [--timeout SEC]          # 既定 20 秒。停止しなければ exit 3

stakeout threads
stakeout thread select (ID|--task NAME)
stakeout thread freeze (ID|--task NAME) [--thaw]
stakeout stack [--thread ID|--task NAME|--all] [--depth N]   # --all の既定深さは 5
stakeout locals [--frame N]
stakeout eval EXPR [--frame N] [--format x|d|s|...]
stakeout dump EXPR [--depth N] [--max-items N]
stakeout mem ADDR LEN [--format hex|u8|u32|ascii]

stakeout bp set (FILE:LINE | --func NAME | --addr HEX | --data EXPR [--size N])
           [--cond EXPR] [--hit N] [--trace "EXPR1,EXPR2" [--trace-stack]]
stakeout bp list | stakeout bp rm ID | stakeout bp clear
stakeout bp exceptions (--on|--off) [--codes C0000005,C0000374]

stakeout run-until FILE:LINE [--cond EXPR] [--timeout SEC] [--capture stack,locals] [--expr E]...
stakeout watch-until-change EXPR [--timeout SEC] [--max-hits N]
stakeout trace-expr EXPR... --steps N [--kind over|into]
stakeout trace-calls FUNC --expr E... [--max-hits N] [--timeout SEC] [--stack]
stakeout find-corruption SYMBOL [--max-hits N] [--timeout SEC]
stakeout task-map

stakeout trace runs
stakeout trace query "SQL" [--limit N]
stakeout trace compare RUN_A RUN_B
stakeout trace export RUN --out FILE.jsonl

stakeout code refs SYMBOL | stakeout code def SYMBOL | stakeout code writers SYMBOL

stakeout log tail [-n N]
stakeout daemon (start|stop|restart|status)
```

### 8.5 ページング

- 1レスポンスの上限: 既定 64 KiB（`--limit-bytes` で変更可）
- 超えた場合は `truncated: true, cursor: "…"` を返す。`stakeout <同じコマンド> --cursor "…"` で続き
- cursor はデーモン内で 10 分間有効

### 8.6 待ち系の設計

エージェントのシェル実行にはタイムアウトがある。したがって

- `wait` の既定は 20 秒。超えたら `{stopped:false}` で exit 3。エージェントは必要なら再度 `wait` する
- Composite の `--timeout` 既定は 60 秒。超えたら「ここまでの結果」を返し `partial: true`
- 「無限に待つ」オプションは作らない

---

## 9. Composite（複合コマンド）仕様

すべての Composite は以下を守る。

- 開始前に「現在のブレークポイント集合」を保存し、終了時に自分が追加した BP を削除する（`--keep-bp` で残す）
- 途中の各停止を JSONL ログに記録する
- 結果 JSON に `steps: [...]`（何をしたか）を含め、エージェントが検証できるようにする

### 9.1 `run-until FILE:LINE`

```
入力: location, condition?, timeout, capture{stack, locals, exprs[]}
手順:
  1. 一時 BP を location に設定（condition 付き）
  2. Stopped なら continue、Detached ならエラー
  3. wait(timeout)
  4. 停止したら: 停止理由を確認
       - 一時 BP による停止 → capture を実行（現在スレッドの stack、locals、exprs の値）
       - 別理由（例外等）→ その停止情報を返し reachedTarget:false
  5. 一時 BP を削除
出力: { reachedTarget, stop, thread, stack?, locals?, exprs?: {expr: value} }
```

### 9.2 `watch-until-change EXPR`

```
手順:
  1. Stopped であること（前提条件）
  2. addr = eval("&(EXPR)"), size = eval("sizeof(EXPR)")。size > 8 なら先頭 8 バイトに丸め warning
  3. before = eval(EXPR)
  4. データ BP を addr,size に設定
  5. loop (maxHits 回まで):
       continue → wait
       停止理由がデータ BP でなければ break（その停止を返す）
       after = eval(EXPR)
       記録 {thread, taskName, stack(top 8), before, after}
       after != before なら break
       before = after
  6. データ BP 削除
出力: { changed: bool, hits: [...], finalValue }
```

### 9.3 `trace-expr EXPR... --steps N`

停止中の現在スレッドで N 回ステップし、各ステップ後に各式を評価して配列で返す。`{step, file, line, function, values{}}`。境界跨ぎで極端に遅くなるので `--kind over` を既定にする。

1 ステップの実測コストは break/go 相当 33 ms + 式評価 6.3 ms × 式数（ADR 0008）。`--steps` の既定は 50、上限は 500 とする。

### 9.4 `trace-calls FUNC --expr E...`

```
手順:
  1. Capabilities.Tracepoint があれば: FUNC 先頭に tracepoint（exprs, stack オプション）
     無ければ: 通常 BP + auto-continue モード（停止→評価→continue を stakeout が繰り返す。遅い）
  2. Running でなければ continue
  3. maxHits または timeout まで収集
  4. 収集結果を Trace DB の新規 run に保存
出力: { runId, hits, events: [先頭 50 件], truncated }
```

### 9.5 `find-corruption SYMBOL`

```
手順:
  1. Code Index があれば writers = code.writers(SYMBOL)（静的候補）
  2. watch-until-change(SYMBOL) を maxHits 回まわし、書き換えた地点を全部収集
  3. 各 hit の停止位置が writers に含まれるかを照合（含まれなければ「想定外の書き込み」として強調）
出力: { symbol, address, size, staticWriters[], hits[{location, function, thread, taskName, before, after, expected: bool}] }
```

ハードウェアデータ BP は x64 で 4 本。1 シンボルにつき 1 本使う。8 バイト超の構造体は先頭 8 バイトのみ（warning）。

### 9.6 `task-map`

停止中の全スレッドについて `{threadId, name, taskName, entryFunction, topFunction, isFrozen}` を返す。`taskName` の決め方:

1. スレッドに名前があればそれ
2. 無ければ、スタック最下段付近のユーザーコードの関数名を設定ファイルの `taskEntryPatterns`（正規表現 → タスク名）に当てる
3. どちらも無ければ `null`

以後 `--task NAME` を受ける全コマンドはこの対応表を使う。対応表は停止のたびに再計算（スレッドは増減する）。

タスク名が複数のスレッドに一致した場合は**エラーにする**。どちらかを黙って選ぶと、凍結したつもりのスレッドが動き続ける。

### 9.7 `trace compare RUN_A RUN_B`

両 run のイベント列を `(function, location)` のキー列に落とし、先頭から比較して最初に分岐した index を求める。分岐点の前後 10 イベントを両方から返す。長さが違うだけの場合も「短い方が先に終わった」として返す。

---

## 10. EnvDTE Backend（Phase 1）

### 10.1 接続

- .NET 8 には `Marshal.GetActiveObject` が無い。`ole32!GetRunningObjectTable` / `CreateBindCtx` を P/Invoke し、ROT を列挙して表示名 `!VisualStudio.DTE.16.0:<pid>` にマッチする moniker を取る
- 表示名は版数非依存の正規表現 `^!VisualStudio\.DTE\.(\d+)\.(\d+):(\d+)$` で拾う。ProgID を固定しない（ADR 0002）
- 複数 VS がある場合の選択順: 設定 `envdte.vsPid` → 設定 `envdte.progId` → Target をデバッグ中の VS → 候補が1つならそれ → **複数残ったら `PRECONDITION` で失敗させる**。hint に候補一覧を出して `--vs-pid` を促す。間違った VS に繋いだまま調査が進むほうが高くつく（ADR 0002）
- 参照パッケージ: `Microsoft.VisualStudio.Interop`（EnvDTE / EnvDTE80 / EnvDTE90 / EnvDTE90a / EnvDTE100）

### 10.2 スレッドモデル

- DTE の全呼び出しを**専用 STA スレッド 1 本**で行う。`Thread.SetApartmentState(ApartmentState.STA)`
- STA スレッドはメッセージポンプを持つ（`System.Windows.Forms.Application.Run(ApplicationContext)` を使ってよい。UI は出さない）
- RPC ハンドラからは `Dispatcher.InvokeAsync(Func<T>)` で STA に投げる。戻りは `Task<T>`
- VS へ到達するには 3 段構えが要る（ADR 0004）。1 段では足りない
  1. **`IOleMessageFilter` を STA スレッドで `CoRegisterMessageFilter`**。`RetryRejectedCall` は `SERVERCALL_RETRYLATER` と `SERVERCALL_REJECTED` の**両方**を 100ms 間隔・最大 30 秒リトライする
  2. **呼び出し側リトライ**。フィルタの再試行枠を使い切った `RPC_E_CALL_REJECTED` / `RPC_E_SERVERCALL_RETRYLATER` を、規定時間まで待って再試行する
  3. **Win32 による準備完了判定**。VS がモーダルダイアログを表示している間は DTE 呼び出しが無期限に拒否され、リトライでは絶対に回復しない。COM で状態を問うと同じ理由でブロックされるため、対象プロセスのトップレベルウィンドウを列挙して判定する
     - メインウィンドウが無い → 「起動中、またはスタートウィンドウのまま」
     - メインウィンドウが無効で、他に有効な可視ウィンドウがある → 「モーダル表示中」。そのタイトルを hint に含めて**待たずに** `PRECONDITION` で返す
- 停止の検知は **`CurrentMode` の 200ms ポーリングを主**とし、`DebuggerEvents.OnEnterBreakMode` / `OnEnterRunMode` は「早く気付くための最適化」として併用する（ADR 0008）。実測では 20/20 で取りこぼしが無かったが、成立条件が 2 つあり、どちらかが壊れると再現困難な形で停止を取りこぼす
  - `DebuggerEvents` の参照を**フィールドで保持し続ける**こと。ローカル変数だと GC された時点で配送が止まる
  - STA スレッドがメッセージをポンプし続けること

### 10.3 昇格レベル

VS と stakeout の昇格レベルが違うと COM が無言で失敗する。`daemon.status` で自プロセスの昇格状態を返し、ROT に VS が見えない場合のエラー hint に「昇格レベルを揃えよ」を入れる。

### 10.4 アタッチ

- `Debugger2.LocalProcesses` から pid で `Process2` を取得する
- アタッチは 3 段フォールバック（ADR 0005）。検証環境では `Attach2` がエンジン指定の形式によらず `0x8971001E` で失敗し、素の `Attach()` だけが通った
  1. 呼び出し側が `engines` を明示した場合のみ `Attach2(engines)`。**失敗しても `Attach()` に落とさず `UNSUPPORTED` で返す。**エンジン指定は「マネージドを巻き込まない」という意図の表明であり、無視して繋ぐと調査の前提が崩れる
  2. `Attach2("Native")`
  3. `Attach()`
- どの方法で成功したかを `SessionInfo` に含める。`Attach()` に落ちた場合は「エンジンを明示できていない」warning を付ける
- 既に VS がその Target をデバッグ中なら、アタッチせず既存セッションを使う（`Debugger.DebuggedProcesses` に含まれるか）
- `engines` が指定されたら配列で渡す（付録 B に混合モードの手順）

### 10.5 各操作のマッピング

| IF | EnvDTE |
|---|---|
| Continue | `Debugger.Go(false)` |
| Pause | `Debugger.Break(false)` |
| Step | `StepOver/StepInto/StepOut(false)`（現在スレッドで） |
| WaitForStop | イベント待ち + `CurrentMode == dbgBreakMode` 確認 |
| Threads | `Debugger.CurrentProgram.Threads` → `Thread`（ID, Name, Location, IsFrozen, Freeze/Thaw） |
| Stack | `Thread.StackFrames` → `StackFrame2`（FunctionName, FileName, LineNumber, Depth, Language, Module）。`Language` が C/C++ 以外は IsExternal |
| Scope | `StackFrame.Locals` / `.Arguments` → `Expression` |
| Evaluate | `Debugger2.GetExpression2(expr, UseAutoExpandRules:true, TreatAsStatement:false, Timeout)` |
| Expand | `Expression.DataMembers` |
| ReadMemory | 直接 API なし。`*(unsigned char(*)[N])(ADDR)` の式評価で代替（遅い） |
| Breakpoint(Line) | `Breakpoints.Add(File:, Line:, Condition:, ConditionType:)`。**作成後に子（束縛位置）の有無で結び付きを確かめる**（ADR 0023、§10.5.2） |
| Breakpoint(Function) | `Breakpoints.Add(Function:)` |
| Breakpoint(Data) | `Breakpoints.Add(Data:"{,,<module>}&expr", DataCount:N)`。**使えることを確認済み**。ただし罠が 3 つある（ADR 0006、下記） |
| Tracepoint | `Breakpoint2.BreakWhenHit=false`, `Breakpoint2.Message="{expr1} {expr2}"`。**機能するが 2.5〜6 hits/s しか出ない**（ADR 0007、§10.6） |
| Exceptions | `Debugger3.ExceptionGroups["Win32 Exceptions"]` から code 文字列で `ExceptionSetting` を探し `SetBreakWhenThrown(true, setting)` |
| Modules | `Debugger.CurrentProgram` からは直接取れない `[要検証]`。最初は `Process2` + `Thread` の Module 名から推定。式評価の結果に `{NativeLib.dll!Shared g_shared}` の形でモジュール名が入るので、そこからも引ける |

#### 10.5.1 データブレークポイントの 3 つの罠（ADR 0006）

1. **データ式は現在のフレームのスコープで解決される。** 別モジュールのグローバルは、そのモジュールのフレームで止まっていないと `0x89711010` で拒否される。必ずモジュール修飾 `{,,<module>}&<expr>` の形で組み立てる
2. **`Breakpoints.Add` は作成しても空のコレクションを返すことがある。** 戻り値を信用せず、**追加前後の `Debugger.Breakpoints` をスナップショットして差分を取る**。信用すると「作れていない」と誤認して重複作成し、4 本しかないハードウェアデータ BP を食い潰す
3. **誤ったデータ式が、黙って別のアドレスを監視する。** `&` を忘れた `Data:"g_shared.counter"` は例外を出さず、counter の**値**をアドレスとして解釈した BP を作った。作成後に BP 名からアドレスを抜き、`&expr` の評価結果と一致するか**必ず検証**し、違えば削除して `BACKEND` エラーにする
   - 期待アドレスは `BreakpointRequest.ExpectedAddress` で渡す。`Condition` に入れると利用者の条件式と取り違える（ADR 0023）
   - **削除し忘れると、管理外の BP が同じアドレスへの以後の要求をすべて「作成できない」にする**（実際に起きた）

ハードウェアデータ BP は x64 で 4 本。stakeout は自分が張った本数を数え、超える要求は `UNSUPPORTED` + hint で断る。データ BP を張ったままデタッチしても Target は生き残ることを確認済み。

データ BP に条件は付けない。`Add(Data:)` には条件を渡しておらず、付いていれば作る前に `UNSUPPORTED` で断る（ADR 0023）。

#### 10.5.2 行ブレークポイントの結び付き（ADR 0023）

**ファイル名だけの指定は、VS が開いている同名の別ファイルに解決されることがある。** そのブレークポイントは `Enabled` のまま一度も止まらず、エラーにもならない。削除済みの作業コピーを開いたままの VS で実際に起きた。

- 結び付いた行ブレークポイントには子（束縛された位置）ができる。作成後 500 ms 待っても子が無ければ、`code.gtagsRoot` の下の同名ファイル → VS が開いているドキュメント（存在するもの）の順にフルパスで張り直す
- どれも結び付かなければ、作ったものを消して `NOT_FOUND`。hint に VS が解決した先を入れる
- フルパスで指定されたときは張り替えない。デザインモードでは確かめない
- `verified` は行・関数 BP では子の有無を返す（`Enabled` ではない）
- 未ロードの DLL に先に張っておく使い方はできない

#### 10.5.3 attach 後の COM の失敗（ADR 0023）

モーダルダイアログは attach の後にも出る（例: pause で開く「ソース ファイルの検索」）。STA で起きた COM の失敗は `StaDispatcher` で翻訳し、`RPC_E_CALL_REJECTED` のときは Win32 でダイアログを探して、あれば `PRECONDITION` とそのタイトルを返す。翻訳しないと `INTERNAL` になり、エージェントは次の一手を決められない。

### 10.6 Tracepoint の出力回収

VS の tracepoint は出力ウィンドウ（Debug ペイン）に書く。回収方法:

1. tracepoint の Message を `STAKEOUT|<bpId>|{expr1}|{expr2}|$TID|$FUNCTION` の固定書式で設定
2. 出力ペインを**名前で引かない**。ペイン名は VS の表示言語で変わる（日本語版では「デバッグ」）ため `Item("Debug")` は失敗する。`OutputWindowPanes` を列挙し、`STAKEOUT|` を含むペインを内容で特定してキャッシュする（ADR 0007）
3. そのペインの `TextDocument` を 200ms ごとに読み、前回位置以降の `STAKEOUT|` 行をパースして `TraceEvent` に変換
4. 出力ペインの肥大化対策として、回収後に一定行数を超えたら `Clear()`（ユーザー設定で無効化可）

Message 内の `{式}` が評価されること、`$TID` / `$FUNCTION` が展開されることは確認済み。ただし**スループットが 2.5〜6 hits/s しかない**（ADR 0007）。auto-continue フォールバック（通常 BP で止め、stakeout が式評価して `Go`）も break/go が 33 ms なので 30 hits/s が上限で、桁は変わらない。

このため Composite 層は `Capabilities.TracepointHitsPerSecond` から所要時間を見積もり、タイムアウトに収まらない要求は**実行前に**断る。

```
この Backend のトレース速度は約 2.5 hits/s です。500 ヒットの収集には
約 200 秒かかり、タイムアウト (60s) を超えます。
--max-hits を 100 以下にするか、--backend dbgeng を使ってください。
```

### 10.7 式の書式

ネイティブ専用アタッチなので式は常に C/C++ 構文。`--format` は VS の書式指定子にマッピング:

| format | VS 書式 |
|---|---|
| x | `expr,x` |
| d | `expr,d` |
| s | `expr,s`（char* を文字列） |
| s8 | `expr,s8` |
| N（数値） | `expr,N`（配列 N 要素） |

`dump` の再帰展開は `DataMembers` を depth まで辿り、ポインタは `IsPointer` を立てて値（アドレス）を返す。NULL ポインタは展開しない。`maxItems` で配列を切る。

結果は**木ではなく経路付きの平らな列**で返す（ADR 0014）。木は上限で切れないためページングできず、`g_ctx.inner.flags` のような絞り込みもできない。表示の都合でデータ構造を決めない。

```json
[
  { "path": "g_ctx.state",       "depth": 1, "name": "state", "type": "int",          "value": "7" },
  { "path": "g_ctx.inner.flags", "depth": 2, "name": "flags", "type": "unsigned int", "value": "2779096485" }
]
```

**式は停止しているフレームのスコープで解決される（ADR 0013）。** これはデータブレークポイントだけでなく通常の式評価にも当てはまる。別モジュールのグローバルは、そのモジュールのフレームで止まっていないと「識別子が定義されていません」になる。モジュール修飾 `{,,モジュール名}式` を付ければどのフレームからでも解決できる。

stakeout は**推測でモジュールを補わない**。同名シンボルが複数モジュールにある場合に黙って別のものを読むほうが、読めないより悪い。代わりに、評価に失敗したときの hint でモジュール修飾を案内する。

### 10.8 制限（Capabilities）

- `DataBreakpoint`: **可**（§10.5.1 の制約付き）
- `Tracepoint`: 可。ただし `TracepointHitsPerSecond` = 実測 2.5〜6。高頻度関数には使えない
- `ReadMemory`: 実装済み。式評価経由（`*(unsigned char(*)[N])(ADDR)`、遅い、上限 4 KiB）。
  **この項目だけは実機で未検証である。** 同じ表の `DataBreakpoint` と `Tracepoint` は
  スパイクで実測した結果だが、これは `tests/integration/phase1.ps1` を
  Visual Studio に対して走らせるまで裏付けが無い
- `Dump`: 不可
- `ParallelSessions`: 不可（VS は 1 つ）

---

## 11. DbgEng Backend（Phase 6）

### 11.1 方針

cdb.exe を stdin/stdout で駆動するのではなく、`dbgeng.dll` の COM インターフェースを直接呼ぶ。理由: 子プロセスの hang・モーダル・パイプのデッドロックを避ける。同じエンジンなので機能差はない。

### 11.2 依存

- `dbgeng.dll` / `dbghelp.dll` / `symsrv.dll`: Debugging Tools for Windows の Debuggers\x64（または x86）から。設定 `dbgeng.path` で指定
- COM 定義: `IDebugClient5`, `IDebugControl4`, `IDebugSymbols3`, `IDebugDataSpaces4`, `IDebugRegisters2`, `IDebugSystemObjects4`, `IDebugBreakpoint2`, `IDebugOutputCallbacks`, `IDebugEventCallbacks`。既存の定義（Microsoft.Diagnostics.Runtime の DbgEng interop 等）を参照して `[ComImport]` で定義する

### 11.3 スレッドモデル

DbgEng のクライアントはスレッド親和性が強い。**セッションごとに専用スレッド 1 本**を持ち、`DebugCreate` から `WaitForEvent` までそのスレッドで行う。RPC からはキュー経由で投げる。`ExitDispatch` / `IDebugClient.CreateClient` で別スレッドから中断要求（Pause）を出す。

### 11.4 Tracepoint

`bp` にコマンド文字列（`.echo STAKEOUT|...; dx expr; gc`）を付けるか、`IDebugEventCallbacks.Breakpoint` で止めて式評価して `GO` を返す。後者が正確で十分速い（プロセス内なので数 ms）。既定は後者。

### 11.5 追加 Capabilities

- `ReadMemory`: `IDebugDataSpaces4.ReadVirtual`（速い）
- `DataBreakpoint`: `IDebugBreakpoint2.SetDataParameters`
- `Dump`: `IDebugClient.WriteDumpFile2`（`snapshot` コマンドで使う）
- `ParallelSessions`: 可（セッションごとのスレッド）
- `TTD`: `.run` ファイルを開ける（自宅環境向け。Phase 8）

---

## 12. Trace DB

### 12.1 保存先

`%LOCALAPPDATA%\stakeout\trace\<project-hash>.sqlite`。`Microsoft.Data.Sqlite`。WAL モード。

### 12.2 スキーマ

```sql
CREATE TABLE runs (
  run_id     TEXT PRIMARY KEY,        -- ULID
  started_at TEXT NOT NULL,
  ended_at   TEXT,
  backend    TEXT NOT NULL,
  target     TEXT NOT NULL,           -- exe 名 + pid
  label      TEXT,                    -- ユーザー指定（"normal-input" 等）
  notes      TEXT
);

CREATE TABLE events (
  event_id   INTEGER PRIMARY KEY,
  run_id     TEXT NOT NULL REFERENCES runs(run_id),
  seq        INTEGER NOT NULL,        -- run 内の通し番号
  ts_qpc     INTEGER NOT NULL,        -- QueryPerformanceCounter（Target 側で取れない場合は stakeout 受信時刻）
  ts_wall    TEXT NOT NULL,
  thread_id  INTEGER NOT NULL,
  task_name  TEXT,
  kind       TEXT NOT NULL,           -- 'tp' | 'stop' | 'step' | 'user'
  location   TEXT NOT NULL,           -- file:line
  function   TEXT NOT NULL,
  bp_id      INTEGER,
  values_json TEXT NOT NULL,          -- {"expr": "value", ...}
  stack_json TEXT                     -- [{"function","file","line"}, ...] 任意
);
CREATE INDEX ix_events_run_seq ON events(run_id, seq);
CREATE INDEX ix_events_run_func ON events(run_id, function);
CREATE INDEX ix_events_run_thread ON events(run_id, thread_id);
```

### 12.3 クエリ

`stakeout trace query "SQL"` は **読み取り専用**接続で実行する（`PRAGMA query_only=1`）。`--limit` 既定 200 行。よく使うクエリは Skill に例として載せる（§17）。

---

## 13. Code Index（gtags）

- 設定 `code.gtagsRoot` に `GTAGS` のあるディレクトリを指定。無ければ `code.*` はすべて `NOT_CONFIGURED`
- `global` コマンドをサブプロセスで呼ぶ。出力形式は `--result=grep`（`file:line:text`）で統一する
  - `code def`: `global --result=grep SYMBOL`（定義索引）
  - `code refs`: `global -r`。**空だったら `global -g`（テキスト検索）に落とし、`kind: "TextSearch"` の印を付ける**（ADR 0017）
  - `code writers`: 最初から `global -g`。書き込み判定に行のテキストが要るため
- マクロを含む宣言（`NL_API extern Shared g_shared;`）は gtags の索引に入らない。変数の定義・参照索引は当てにできない（ADR 0017）
- `code.writers SYMBOL`: 参照行のうち、行テキストが代入パターン（`SYMBOL\s*(\[[^\]]*\])?\s*(\.|->)?\w*\s*[-+*/|&^]?=[^=]`、`memcpy\(\s*&?SYMBOL`、`memset\(\s*&?SYMBOL`、`&SYMBOL` を引数に渡している呼び出し）に当たるものを返す。ヒューリスティックなので `confidence: high|low` を付ける
- 結果は `{file, line, text, function?, confidence, reason}`。`function` は `global -f` で得たファイル内の定義一覧から、その行を囲むものを引く
- **静的な結果を犯人として扱わせない。** 出力には毎回「これは候補です」と添え、`find-corruption` で確かめる導線を示す
- 隣接メンバへの範囲外書き込み（`g_shared.scratch[4]` が `counter` を踏む）は、字面に対象が現れないため**静的には見つからない**。それを見つけるのが `find-corruption` の役割である

---

## 14. Session Log（JSONL）

- `%LOCALAPPDATA%\stakeout\\logs\<yyyyMMdd>-<sessionId>.jsonl`
- 1 行 = `{ts, seq, kind: "rpc.request"|"rpc.response"|"stop"|"trace"|"daemon", method?, params?, result?, error?, durationMs?}`
- `params` / `result` は 8 KiB で切り、`truncated:true` を付ける
- `stakeout log tail` と `stakeout log export` で読める
- 調査レポート生成（Phase 8 以降）はこのログを入力にする

---

## 15. エラーモデル・タイムアウト・安全

### 15.1 エラーコード

| code | 意味 | hint の例 |
|---|---|---|
| `PRECONDITION` | 状態要件違反 | "Target is running. Run `stakeout pause` or `stakeout wait` first." |
| `PRECONDITION` | VS がモーダル表示中 | 「Visual Studio (pid N) がモーダルダイアログ『…』を表示しています。閉じてから再実行してください」 |
| `PRECONDITION` | VS が複数起動 | 候補の `{version, pid, solution}` を列挙し `--vs-pid` を促す |
| `NOT_ATTACHED` | セッション無し | "Run `stakeout attach --name <exe>`." |
| `TIMEOUT` | 期限内に完了せず | "Target may be busy. Retry `stakeout wait --timeout 20`." |
| `BACKEND` | Backend 例外 | HRESULT と VS の状態。`0x89711007`（メソッドを実行できない）は `PRECONDITION` に変換して現在の状態を hint に出す |
| `NOT_FOUND` | プロセス/シンボル/BP が無い | 候補があれば列挙 |
| `DENIED` | allowlist 外 | "Add to `allowProcesses` in stakeout.json." |
| `UNSUPPORTED` | Capabilities 不足 | "Use `--backend dbgeng`." |
| `NOT_CONFIGURED` | Code Index 等未設定 | 設定キー名 |

**すべてのエラーに `hint` を付ける。** エージェントが次に何をすべきかをエラーだけで判断できるようにする。

### 15.2 タイムアウト

| 操作 | 既定 |
|---|---|
| RPC 全体 | 30 s |
| `wait` | 20 s |
| 式評価 1 回 | 5 s |
| Composite | 60 s |
| COM リトライ合計 | 30 s |

実測コスト（ADR 0008）: DTE プロパティ読み取り 0.048 ms / 式評価 6.3 ms / スタック 1 段 4.0 ms / break→go 1 サイクル 33.4 ms / トレースポイント 1 ヒット 160〜400 ms。**支配的なのは COM 越境回数ではなく「VS に何回評価させるか」**である。

### 15.3 安全

- `allowProcesses`（正規表現配列）にマッチしないプロセスにはアタッチしない。空なら全拒否
- stakeout 終了時・CLI からの `daemon stop` 時は必ずデタッチする（Target を殺さない）
- `launch` は `allowLaunch: true` のときのみ
- `trace query` は読み取り専用
- Composite の `maxHits` 既定 100、上限 10000。Trace DB は run ごとに 100 万イベントで打ち切り

---

## 16. 設定ファイル

探索順: `%APPDATA%\stakeout\stakeout.json` → `.stakeout.json`。後者で前者を上書き。

`.stakeout.json` は、デーモンの作業ディレクトリから**親へ遡って最も近いもの**を読む（git と同じ）。設定内の相対パス（`code.gtagsRoot`）は、それを書いた設定ファイルのディレクトリを基準に解決する（ADR 0023）。作業ディレクトリ直下しか見ないと、エージェントが `cd` した先で自動起動したデーモンが設定を読まず、attach が `DENIED` になる。

```jsonc
{
  "backend": "envdte",                 // 既定 backend
  "allowProcesses": ["^MyProductHost\\.exe$"],
  "allowLaunch": false,
  "envdte": { "vsPid": null, "progId": null, "clearOutputPaneAfterLines": 5000 },  // progId 既定は null = 自動検出
  "dbgeng": { "path": "C:\\Program Files (x86)\\Windows Kits\\10\\Debuggers\\x64", "symbolPath": "srv*;C:\\path\\to\\pdb" },
  "code": { "gtagsRoot": "C:\\src\\product" },
  "tasks": {
    "taskEntryPatterns": [ { "pattern": "^Task_(\\w+)_Main$", "name": "$1" } ]
  },
  "limits": {
    "responseBytes": 65536, "waitSec": 20, "compositeSec": 60, "maxHits": 100,
    "maxFramesPerRequest": 200,      // スタック取得 1 段 4 ms。超えたら truncate（ADR 0008）
    "maxEvaluationsPerRequest": 300  // 式評価 1 回 6.3 ms。超えたら cursor で分割,
    "maxMemoryReadBytes": 4096       // EnvDTE は式評価で読むので、増やすと比例して遅くなる
  },
  "log": { "dir": null }
}
```

---

## 17. Skill

`skills/stakeout/SKILL.md` に以下を書く。ツール実装と同じリポジトリで管理し、CLI が変わったら同じコミットで更新する。
エージェントに使わせるときは、クライアントのスキル置き場へコピーする。

### 17.1 内容

1. **前提**: 常に `--json`。`wait` は exit 3 なら再実行。エラーの `hint` に従う
2. **調査の型（順番厳守）**
   1. 再現手順とシンボル（壊れる変数・落ちる関数）を確定する
   2. 検出ビルド（ASan/ページヒープ）があればまずそれで走らせ、止まった場所から始める
   3. `code writers SYMBOL` で静的候補を出す
   4. `find-corruption` / `watch-until-change` で動的に犯人を特定する
   5. 順序・タイミングの問題なら `trace-calls` → `trace query` → `trace compare`。
      **ただし EnvDTE backend では 2.5〜6 hits/s しか出ないので、毎秒数回しか呼ばれない関数に限る**（ADR 0007）。
      高頻度関数を追うなら `--backend dbgeng` を使う
   6. ここまでで決まらないときだけ `run-until` → `step` → `locals`
3. **禁止事項**: いきなり `step` で追いかけない。ポインタは `dump` で展開する。`wait` に長いタイムアウトを渡さない。C# フレームは無視する。`pause` してからグローバルを読まない（どのフレームで止まるか分からず、別モジュールのシンボルが見えない。ADR 0013）。グローバルを読むときは `stakeout stack` で停止位置を確認し、別モジュールなら `{,,モジュール名}` を付ける
4. **コマンド早見表**（§8.4 の要約）
5. **Trace DB クエリ例**
   - `SELECT seq, task_name, function, values_json FROM events WHERE run_id='X' AND function='foo' ORDER BY seq`
   - 値が変わった直前: `SELECT * FROM events WHERE run_id='X' AND seq < (SELECT MIN(seq) FROM events WHERE run_id='X' AND json_extract(values_json,'$.state')='7') ORDER BY seq DESC LIMIT 20`
6. **報告フォーマット**: 再現手順 / 原因行 / 根拠（ログの seq・trace の event_id）/ 未確認事項

---

## 18. テスト戦略

### 18.1 サンプル Target（`samples/target/`）

VS2019 で開けるソリューション。

- `NativeLib/`（C DLL）: 以下の「仕込みバグ」を `#ifdef BUG_NN` で切り替えられる
  - BUG_01: 固定長バッファへのオーバーラン（`strcpy`）
  - BUG_02: 未初期化のローカル構造体
  - BUG_03: 共有グローバルへの想定外書き込み（別「タスク」スレッドから）
  - BUG_04: 状態遷移の順序違反（`state` が 3→7 に飛ぶ）
  - BUG_05: NULL ポインタ経由アクセス（アクセス違反）
  - BUG_06: 解放後使用
- `Host/`（C# exe）: DLL を P/Invoke し、`Task_A_Main` … `Task_D_Main` をスレッドで回す（タスク名パターンのテスト用）。引数でシナリオ選択
- `Harness/`（C exe）: ホスト無しで DLL を直接叩く（Phase 6 の並列・決定的実行用）

### 18.2 テスト種別

| 種別 | 対象 | 実行方法 |
|---|---|---|
| 単体 | RPC シリアライズ、ページング、Code Index のヒューリスティック、trace compare のアルゴリズム、taskName 解決 | `tests/run.ps1`（ADR 0010）。Backend はモック |
| 統合 | EnvDTE Backend の各操作 | `samples/target` を VS2019 で開いた状態で `tests/integration/*.ps1` を実行。CI 不可、手動 |
| ゴールデン | CLI の JSON 出力 | 期待 JSON と比較（アドレス等は正規化） |
| 評価 | エージェントがバグを当てられるか | §18.3 |

### 18.3 評価スイート（`eval/`）

- `eval/cases/BUG_NN.md`: 症状の説明（ユーザーが最初に言いそうな文）と、正解（原因の file:line）
- `eval/run.ps1`: 各ケースについて Claude Code を起動し Skill 付きで調査させ、出力から file:line を抽出して正解と照合。所要秒数と呼んだ RPC の回数を記録
- 結果は `eval/results/<date>.md`。ツール改修の前後で比較する

**評価スイート自体を検証する（ADR 0016）。** 計測器が別のものを測っていても、
結果だけを見ている限り気づけない。実際に 1 回目の測定は、症状が化けて届き、
かつエージェントが正解ファイルを読んだ状態で「4 件 PASS」を出していた。

- プロンプトの文字コードを明示する（`$OutputEncoding`）。既定に任せない
- 実行中は `eval/cases` を作業ツリーの外へ退避する。「見ないで」という指示に頼らない
- 出力に「文字化け」等が現れたら、その測定を無効として落とす
- 結果を報告する前に、最低 1 件は transcript を読む

**エージェントにはリポジトリを見せない（ADR 0022）。** サンプルのソースには原因がコメントで書いてあり、
docs や eval/results には正解の関数名がある。

- ソースの写しを一時ディレクトリに作り、コメントと `BUG_NN` の条件分岐を消す（`eval/sanitize.ps1`）。**行番号は保つ**
- 写しに `BUG_NN` や日本語（コメントの消し残し）があれば、測定を始めない
- 写しからビルドする。PDB にリポジトリのパスが残っていれば、測定を始めない
- stakeout 本体・設定・作業ディレクトリもリポジトリの外に置き、そこで Claude Code を起動する
- ツール呼び出しをすべて記録し（stream-json）、リポジトリのパスに触れた測定は INVALID にする
- 仕組みを変えたら `eval/run.ps1 -PrepareOnly` で、Visual Studio と API 無しに準備部分を確かめる

---

## 19. リポジトリ構成

```
stakeout/
  README.md
  CONTRIBUTING.md               # 開発の進め方
  docs/
    design.md                   # この文書
    phase-log.md
    decisions/                  # ADR
  src/
    Stakeout.Core/                  # ドメインモデル、IDebuggerBackend、Composite、TraceDb、CodeIndex、SessionLog
    Stakeout.Backend.EnvDte/
    Stakeout.Backend.DbgEng/        # Phase 6
    Stakeout.Daemon/                # stakeoutd.exe（RPC サーバー、設定、ライフサイクル）
    Stakeout.Cli/                   # stakeout.exe（System.CommandLine）
    Stakeout.Mcp/                   # Phase 7
    Stakeout.Rpc/                   # RPC の契約（メソッド名・DTO）。Daemon/Cli/Mcp が共有
  tests/
    Stakeout.Core.Tests/
    Stakeout.Cli.Tests/
    integration/                # PowerShell スクリプト
  samples/
    target/                     # §18.1
  eval/                         # §18.3
  skills/
    stakeout/SKILL.md                # §17（クライアントのスキル置き場へコピーする）
```

### 19.1 技術選定

| 用途 | 選定 |
|---|---|
| ランタイム | .NET 8, `net8.0-windows` |
| RPC | `StreamJsonRpc` + `System.IO.Pipes` |
| CLI | `System.CommandLine` |
| SQLite | `Microsoft.Data.Sqlite` |
| ログ | `Microsoft.Extensions.Logging` + 自前 JSONL シンク |
| VS Interop | `Microsoft.VisualStudio.Interop` |
| テスト | xUnit v3（アサーションライブラリは使わない。ADR 0009） |
| ID | ULID |

---

## 20. 実装フェーズと完了条件

各フェーズは独立してレビューできる粒度にする。**完了条件をすべて満たすまで次に進まない。**

Phase 4〜6 の順序は ADR 0007 で組み替えた（Trace DB を DbgEng の後ろへ、Code Index を前へ）。
実装に着手する前に Phase -1（スパイク、ADR 0003）を実施済み。結果は ADR 0004〜0008。

### Phase 0 — 骨組み

作るもの: リポジトリ構成、`Stakeout.Rpc` の DTO、`stakeoutd.exe`（`daemon.ping/status/shutdown` のみ）、`stakeout.exe`（`status`, `daemon *`、自動起動、`--json`、終了コード）、設定読み込み、JSONL ログ、`IDebuggerBackend` の空実装（`NullBackend`）。

完了条件:
- `stakeout status --json` がデーモンを自動起動して `{ok:true,...}` を返す
- `stakeout daemon stop` で終了し、ログに request/response が残る
- 単体テスト: 設定の探索順、ページング、終了コード

### Phase 1 — EnvDTE Backend

作るもの: §10 のうち attach / detach / continue / pause / step / wait / threads / stack / scope / eval / expand / bp(line, function, condition) / exceptions。`samples/target`（BUG_01, 04, 05 まで）。

完了条件（Visual Studio を起動し、スタートウィンドウを抜けてモーダルが無い状態で `tests/integration/phase1.ps1` がすべて通る）:
- `stakeout attach --name Host.exe` → Native のみでアタッチ。`Attach()` にフォールバックした場合でも、スタックに `[Managed to Native Transition]` が現れないこと（ADR 0005）
- `stakeout bp set nativelib.c:NN --cond "g_ctx.state==7"` → `stakeout continue` → `stakeout wait` → 停止し `stakeout stack --all` に全スレッドが出る
- `stakeout eval "g_ctx.name,s"` / `stakeout expand` で構造体が読める
- `stakeout bp exceptions --on --codes C0000005` で BUG_05 が発生元の C 関数（`nl_deref`）で止まる
- `stakeout detach` 後に Target が生きている
- `stakeout wait` で停止を待っている間も `stakeout status` が 5 秒以内に返る（ADR 0011）
- 停止中でない状態での読み取りが `PRECONDITION`（終了コード 4）で断られる
- `pause` / `continue` を続けて 2 回呼んでも失敗しない（冪等。ADR 0008）
- 残る `[要検証]` 項目（Modules の取得）の結果が ADR に記録されている。データ BP / tracepoint / イベントは ADR 0006〜0008 で解決済み

### Phase 2 — Composite 第1群 + タスク対応

作るもの: `run-until`, `dump`, `watch-until-change`, `trace-expr`, `task-map`, `thread select/freeze --task`, ページング、`--cursor`。

完了条件（`tests/integration/phase2.ps1` がすべて通る）:
- BUG_03 に対して `watch-until-change {,,NativeLib.dll}g_shared.counter` が書き込み元を `taskName` 付きで返し、スタックにタスクのエントリ関数が出る
- BUG_04 に対して `run-until nativelib.c:NN --cond "g_ctx.state==7" --expr ...` が 1 ターンで原因を含む情報を返す
- `dump` が経路付きの平らな列を返し、大きい結果が `truncated` + `cursor` で分割される。カーソルは 1 度しか使えない
- `task-map` が A/B/C/D の 4 タスクにタスク名を当て、`thread select --task C` が効く
- 知らないタスク名は候補を挙げて `NOT_FOUND`（終了コード 5）で断られる
- `watch-until-change` を実行中に呼ぶと `PRECONDITION` で断られる（データ BP は中断中にしか張れない）
- Composite が張った一時ブレークポイントが、終了後に残っていない

### Phase 3 — Skill と評価スイート v1

作るもの: §17 の SKILL.md、§18.3 の eval（BUG_01, 03, 04, 05）。

完了条件:
- エージェントが Skill だけを頼りに BUG_04 と BUG_05 の原因行を当てる
  （症状の文と Target の pid だけを渡し、正解ファイルは作業ツリーから外す）
- 結果・所要時間・RPC 呼び出し回数が `eval/results/` に記録されている
- transcript を読み、測定が意図したものを測っていることを確かめている（ADR 0016）

### Phase 4 — Code Index と find-corruption

作るもの: §13、`find-corruption`。

完了条件:
- `code writers g_shared.counter` が `samples/target` 内の代入行を列挙する（誤検出は `low` 付き）
- `find-corruption g_shared.counter` が BUG_03 の書き込み元を `expected:false` で返す
- データ式が `{,,<module>}&expr` で組み立てられ、作成された BP のアドレスが検証されている（§10.5.1）
- eval に BUG_03 を追加して通る

### Phase 5 — DbgEng Backend

作るもの: §11。`--backend dbgeng`、`snapshot`（ダンプ）、`Harness` を使った並列セッション。

前提条件:

- **Debugging Tools for Windows がインストールされていること。**
- **ADR 0020 のチェックリストを先に片付けること。** 式の構文が Backend から
  抽象化されておらず、そのまま DbgEng を足すと `code writers` が静かに空を返す

`dbgeng.dll` は System32 にもあるが、そちらでは侵入アタッチが成立しない（ADR 0018）。
非侵入アタッチは動くものの、それではブレークポイントも実行制御も載らない。
着手前に `spike/DbgEngProbe` を `DBGENG_PATH` 付きで走らせ、`D2 OK` / `D3 OK` を確認すること。

完了条件:
- Phase 1 の統合スクリプトが `--backend dbgeng` でも通る
- データ BP と tracepoint が DbgEng で動く
- `Harness` を 3 セッション同時にアタッチして、それぞれ独立に `run-until` できる
- eval の全ケースが dbgeng でも通る。ただし `dump` の出力は VS と一致しない前提で、ゴールデンテストの正規化を Backend ごとに分ける

### Phase 6 — Trace DB と Tracepoint

作るもの: §12、`trace-calls`、`trace runs/query/compare/export`、tracepoint 回収（§10.6）と auto-continue フォールバック。

ADR 0007 により、旧 Phase 4 からここへ移した。Trace DB は「速いトレース源」があって初めて価値が出るため、DbgEng の後ろに置く。

完了条件:
- `trace-calls update_state --expr state --expr event --max-hits 500` が **60 秒以内に** run を作り、`trace query` で引ける（DbgEng backend で計測する）
- 正常入力と BUG_04 入力の 2 run を `trace compare` して分岐点が `state` の遷移箇所に一致する
- EnvDTE backend では、所要時間の見積もりがタイムアウトを超える要求を実行前に `UNSUPPORTED` で断る

### Phase 7 — MCP アダプタ

作るもの: `Stakeout.Mcp`（stdio）。ツールは Composite 群 + attach/detach/wait/stack/eval/dump に絞る（低レベル操作は CLI に任せる）。

完了条件（`tests/integration/phase7.ps1` がすべて通る）:
- MCP のワイヤプロトコルで initialize → tools/list → attach → run-until → dump → detach が通る
- ツール定義の合計が 4 KiB 以下。**大きさの測り方を本体と検証で共有する**
  （別々に測ると片方だけが上限を守っているつもりになる）
- 道具の失敗はプロトコルのエラーではなく `isError` として返し、hint を落とさない

Claude Desktop 実機での確認は、この検証が通ったうえで別途行う。

### Phase 8 — 拡張（優先度順、必要になったら）

- `build-mcp` 相当: MSBuild 呼び出し、検出ビルド構成の切替、`bisect-svn`
- TTD（DbgEng で `.run` を開く。自宅環境）
- 調査レポート生成（JSONL + Trace DB → Markdown）と課題管理システムへの起票
- 付録 C（複数プロセス）

---

## 21. コーディング規約

- C# 12、`nullable enable`、`TreatWarningsAsErrors`
- 公開 API はすべて `CancellationToken` を受ける
- COM 呼び出しは Backend プロジェクトの外に漏らさない（`Stakeout.Core` は COM を知らない）
- 例外は Backend 境界で `BackendException(code, hint)` に変換する
- ログに Target のメモリ内容をそのまま出さない（`values_json` の値は 1 KiB で切る）
- コミット: Conventional Commits、英語、1 コミット 1 関心事
- ドキュメント: 日本語。識別子・コマンド名は英語

---

## 付録 A — 開発の進め方

リポジトリ直下の [CONTRIBUTING.md](../CONTRIBUTING.md) を見ること。
フェーズ順・ADR の書き方・触ってはいけないものを書いてある。

## 付録 B — 混合モード（必要になった場合のみ）

C# 側も止めたいとき:

- `session.attach` に `engines: ["Managed (v4.6, v4.5, v4.0)", "Native"]`（.NET Framework）または `["Managed (.NET Core, .NET 5+)", "Native"]`
- スタックに `[Managed to Native Transition]` が混ざる。`IsExternal:true` にする
- 式評価は `StackFrame.Language` を見て C# / C++ を切り替える。`vars.eval` の結果に `language` を含める
- 境界跨ぎの `step into` は遅い。`wait` の既定を 30 秒に上げる

## 付録 C — 複数プロセス対応（Q1 が「複数」だった場合）

- `SessionGroup` を実体化: `attach-all PATTERN` / `break-all` / `continue-all` / `detach-all`
- EnvDTE: VS の「1つが中断したら全プロセスを中断」設定に依存する。stakeout 起動時に設定値を確認して warning
- DbgEng: セッションごとに `DebugBreakProcess` を一斉送信。数 ms のズレは許容
- Trace DB の `events` に `process_id` 列を追加。時刻は同一マシンの QPC で揃う
- Target 側に「デバッグ時はウォッチドッグ / IPC タイムアウトを無効化」するビルドフラグを要求する。無いと stop 系コマンドが実用にならない
- 子プロセスを動的生成する場合: EnvDTE では追えない（Child Process Debugging Power Tool が必要）。DbgEng は `DEBUG_PROCESS` フラグで追える
- `snapshot-all`: 全セッションのダンプを同時取得

## 付録 D — 検出ビルド構成（ツール外だが必須）

`samples/target` と実 Target の両方に「Detect」構成を用意する:

- x64/x86, `/fsanitize=address`（VS 16.9+）、`/Zi`、最適化 `/Od`
- ASan 不可の場合: `gflags /p /enable Target.exe /full`（ページヒープ）と `/RTC1`
- Skill の手順 2 はこの構成で走らせることを前提にする

## 付録 E — 用語対応（VS ↔ DbgEng ↔ 本設計）

| 本設計 | VS (EnvDTE) | DbgEng |
|---|---|---|
| Line BP | `Breakpoints.Add(File, Line)` | `bp file.c:line` / `IDebugBreakpoint2` |
| Data BP | `Breakpoints.Add(Data, DataCount)` | `ba w4 addr` / `SetDataParameters` |
| Tracepoint | `Breakpoint2.BreakWhenHit=false` + `Message` | `bp addr ".echo; gc"` または EventCallbacks |
| 全スレッドスタック | `Threads` → 各 `StackFrames` | `~*k` / `IDebugSystemObjects4` |
| 凍結 | `Thread.Freeze()` | `~n f` |
| 例外ブレーク | `ExceptionGroups` | `sxe av` / `SetExceptionFilter` |
| メモリ | 式評価で代替 | `ReadVirtual` |
