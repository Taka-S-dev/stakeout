using System.Collections;
using System.Runtime.CompilerServices;
using Stakeout.Core;
using Stakeout.Rpc;
using EnvDTE;
using EnvDTE80;
using EnvDTE90;
using EnvDTE90a;
using DteBreakpoint = EnvDTE.Breakpoint;
using DteProcess = EnvDTE.Process;
using DteStackFrame = EnvDTE.StackFrame;
using DteStackFrame2 = EnvDTE90a.StackFrame2;
using DteThread = EnvDTE.Thread;
using StackFrame = Stakeout.Rpc.StackFrame;
using Variable = Stakeout.Rpc.Variable;

namespace Stakeout.Backend.EnvDte;

/// <summary>
/// Visual Studio に COM で繋ぐ Backend（design.md §10）。
///
/// DTE の呼び出しはすべて専用 STA スレッドで行う。到達性は 3 段構えで守る
/// （メッセージフィルタ・呼び出し側リトライ・Win32 による準備完了判定。ADR 0004）。
/// 停止の検知はポーリングを主とし、イベントは補助に使う（ADR 0008）。
/// </summary>
public sealed class EnvDteBackend : IDebuggerBackend
{
    /// <summary>停止を検知するポーリング間隔（design.md §10.2）。</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>式評価のタイムアウト（design.md §15.2）。</summary>
    private const int EvaluationTimeoutMs = 5000;

    private readonly StaDispatcher _sta;

    /// <summary>調査対象のソースツリー（code.gtagsRoot）。行ブレークポイントの張り直し先を探すのに使う。</summary>
    private readonly string? _sourceRoot;

    /// <summary>行ブレークポイントがコードに結び付くまで待つ上限。</summary>
    private static readonly TimeSpan BindTimeout = TimeSpan.FromMilliseconds(500);
    private readonly EnvDteConfig _config;
    private readonly Action<string> _log;
    private readonly StopScope _scope = new();
    private readonly BreakpointTracker _breakpoints = new();

    private VisualStudioCandidate? _vs;
    private DTE2? _dte;
    private Debugger3? _debugger;

    // DebuggerEvents は参照を保持し続けないと GC され、イベントが飛ばなくなる（ADR 0008）
    private DebuggerEvents? _debuggerEvents;
    private volatile int _breakEventCount;

    private SessionInfo? _session;
    private StopEvent? _lastStop;

    public EnvDteBackend(EnvDteConfig config, Action<string>? log = null, string? sourceRoot = null)
    {
        _config = config;
        _log = log ?? (_ => { });
        _sourceRoot = sourceRoot;
        _sta = new StaDispatcher("stakeout-envdte-sta", TranslateComFailure);
    }

    /// <summary>
    /// STA で起きた COM の失敗を、エージェントが次の一手を決められるエラーにする。
    ///
    /// 翻訳しないと RpcHandlers で INTERNAL になり、hint は「ログを見よ」だけになる。
    /// 実機では、pause で開いた「ソース ファイルの検索」ダイアログのせいで
    /// 以降の全操作が RPC_E_CALL_REJECTED になり、INTERNAL が並んだ。
    /// **ダイアログは attach の後にも出る。** attach 前の判定（ADR 0004）だけでは足りない。
    /// </summary>
    private Exception TranslateComFailure(Exception ex)
    {
        if (ex is not System.Runtime.InteropServices.COMException)
        {
            return ex;
        }

        // 拒否されたときだけ、COM を使わずに（Win32 で）ダイアログを探す
        if (ComErrors.IsBusy(ex) && _vs is { } vs)
        {
            var readiness = VisualStudioReadinessCheck.Check(vs.Pid);
            if (!readiness.Ready && readiness.BlockingWindow is { } window)
            {
                return new BackendException(
                    ErrorCodes.Precondition,
                    $"{readiness.Reason} (pid {vs.Pid})",
                    $"ダイアログ「{window}」を閉じてから再実行してください。" +
                    "stakeout からは閉じられないので、ユーザーに閉じてもらってください。",
                    ex);
            }
        }

        return ComErrors.Translate(ex, "Visual Studio の操作", "不明");
    }

    public string Name => "envdte";

    public BackendCapabilities Capabilities { get; } = new()
    {
        // ADR 0006: 使える。ただしデータ式はモジュール修飾が要る
        DataBreakpoint = true,

        // ADR 0007: 動くが 2.5〜6 hits/s しか出ない
        Tracepoint = true,
        TracepointHitsPerSecond = 4.0,

        // 直接 API は無いが、式評価で代替できる（design.md §10.8、§14）。遅い
        ReadMemory = true,

        Dump = false,
        Ttd = false,

        // Visual Studio は 1 つしか使えない
        ParallelSessions = false,

        // x64 のデバッグレジスタは 4 本
        DataBreakpointSlots = 4,
    };

    /// <summary>接続している Visual Studio。未接続なら null。</summary>
    public VisualStudioCandidate? VisualStudio => _vs;

    /// <summary>最後に観測した停止。診断用。</summary>
    public StopEvent? LastStop => _lastStop;

    /// <summary>
    /// OnEnterBreakMode が届いた回数。ポーリングが主なので機能には影響しないが、
    /// イベント配送が生きているかの確認に使う（ADR 0008）。
    /// </summary>
    public int BreakEventCount => _breakEventCount;

    /// <summary>メッセージフィルタが再試行した回数。Visual Studio のビジー度合いの目安。</summary>
    public int ComRetryCount => _sta.ComRetryCount;

    // ---------------------------------------------------------------- session

    public async Task<SessionInfo> AttachAsync(int pid, AttachOptions options, CancellationToken ct)
    {
        var vs = await _sta.InvokeAsync(
            () => VisualStudioLocator.Select(_config, pid, DebuggedPids), ct);

        // COM を呼ぶ前に Win32 で確かめる。モーダル中は待っても回復しない（ADR 0004）
        VisualStudioReadinessCheck.EnsureReady(vs.Pid);

        return await _sta.InvokeAsync(() =>
        {
            _vs = vs;
            _dte = (DTE2)vs.Dte!;
            _debugger = ComRetry.Run(() => (Debugger3)_dte.Debugger, "Debugger の取得");

            SubscribeDebuggerEvents();

            var attachMethod = AttachCore(pid, options);
            var process = FindDebuggedProcess(pid);

            _session = new SessionInfo(
                SessionId: Guid.NewGuid().ToString("n")[..8],
                Backend: Name,
                Pid: pid,
                ProcessName: process is null ? $"pid {pid}" : Path.GetFileName(SafeName(process)),
                Bitness: Environment.Is64BitProcess ? "x64" : "x86",
                State: CurrentState());

            _log($"attached pid={pid} via {attachMethod} vs={vs.ProgId}({vs.Pid})");
            return _session;
        }, ct);
    }

    public Task<SessionInfo> LaunchAsync(LaunchOptions options, CancellationToken ct) =>
        throw BackendException.Unsupported(
            "プロセスの起動",
            "先に Target を起動してから stakeout attach --pid で接続してください。launch は Phase 8 で実装します。");

    public Task DetachAsync(CancellationToken ct)
    {
        if (_dte is null)
        {
            return Task.CompletedTask;
        }

        return _sta.InvokeAsync(() =>
        {
            // 自分が張ったブレークポイントを片付ける。特にデータブレークポイントは
            // 4 本しかなく、残すと次の調査が張れなくなる（ADR 0006）
            RemoveOwnBreakpoints();

            try
            {
                if (_debugger is not null && _debugger.CurrentMode != dbgDebugMode.dbgDesignMode)
                {
                    // Target を殺さない（design.md §15.3）。DetachAll で生き残ることは確認済み
                    _debugger.DetachAll();
                }
            }
            catch (Exception ex) when (ComErrors.IsDisconnected(ex))
            {
                // VS が先に終了していた。デタッチの目的は果たされている
            }

            UnsubscribeDebuggerEvents();
            _scope.Clear();
            _session = _session is null ? null : _session with { State = SessionState.Detached };
            _log("detached");
        }, ct);
    }

    /// <summary>現在のセッション。未アタッチなら null。</summary>
    public SessionInfo? Session => _session is null
        ? null
        : _session with { State = SafeState() };

    // ---------------------------------------------------------------- execution

    public Task ContinueAsync(CancellationToken ct) =>
        _sta.InvokeAsync(() =>
        {
            var debugger = RequireDebugger();

            // 冪等にする。既に実行中なら何もしない（ADR 0008）。
            // VS の Go は実行中に呼ぶと 0x89711007 で失敗する
            if (debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
            {
                return;
            }

            _scope.Clear();
            ComRetry.Run(() => debugger.Go(WaitForBreakOrEnd: false), "continue");
        }, ct);

    public Task PauseAsync(CancellationToken ct) =>
        _sta.InvokeAsync(() =>
        {
            var debugger = RequireDebugger();

            // 冪等にする。中断中に Break を呼ぶと 0x89711007 になる（ADR 0008）
            if (debugger.CurrentMode != dbgDebugMode.dbgRunMode)
            {
                return;
            }

            ComRetry.Run(() => debugger.Break(WaitForBreakMode: false), "pause");
        }, ct);

    public Task StepAsync(StepKind kind, int threadId, CancellationToken ct) =>
        _sta.InvokeAsync(() =>
        {
            var debugger = RequireDebugger();
            RequireStopped(debugger, "step");

            if (threadId > 0)
            {
                SelectThread(debugger, threadId);
            }

            _scope.Clear();

            ComRetry.Run(
                () =>
                {
                    switch (kind)
                    {
                        case StepKind.Over:
                            debugger.StepOver(WaitForBreakOrEnd: false);
                            break;
                        case StepKind.Into:
                            debugger.StepInto(WaitForBreakOrEnd: false);
                            break;
                        case StepKind.Out:
                            debugger.StepOut(WaitForBreakOrEnd: false);
                            break;
                        default:
                            throw new BackendException(
                                ErrorCodes.Usage,
                                $"未知のステップ種別です: {kind}",
                                "over / into / out のいずれかを指定してください。");
                    }
                },
                $"step {kind}");
        }, ct);

    public async Task<StopEvent?> WaitForStopAsync(TimeSpan timeout, CancellationToken ct)
    {
        var stopped = await _sta.WaitUntilAsync(
            () => RequireDebugger().CurrentMode == dbgDebugMode.dbgBreakMode,
            timeout,
            PollInterval,
            ct);

        if (!stopped)
        {
            return null;
        }

        return await _sta.InvokeAsync(BuildStopEvent, ct);
    }

    // ---------------------------------------------------------------- threads

    public Task<IReadOnlyList<ThreadInfo>> GetThreadsAsync(CancellationToken ct) =>
        _sta.InvokeAsync<IReadOnlyList<ThreadInfo>>(() =>
        {
            var debugger = RequireDebugger();
            RequireStopped(debugger, "threads");

            var currentId = Safe(() => debugger.CurrentThread?.ID ?? 0, 0);
            var result = new List<ThreadInfo>();

            foreach (DteThread thread in ThreadsOf(debugger))
            {
                result.Add(new ThreadInfo(
                    ThreadId: thread.ID,
                    Name: Safe(() => thread.Name, string.Empty),
                    TaskName: null, // Phase 2 の task-map で埋める
                    IsFrozen: Safe(() => thread.IsFrozen, false),
                    IsCurrent: thread.ID == currentId,
                    TopFunction: Safe(() => thread.Location, string.Empty)));
            }

            return result;
        }, ct);

    public Task SetCurrentThreadAsync(int threadId, CancellationToken ct) =>
        _sta.InvokeAsync(() =>
        {
            var debugger = RequireDebugger();
            RequireStopped(debugger, "thread select");
            SelectThread(debugger, threadId);
            _scope.Clear();
        }, ct);

    public Task FreezeThreadAsync(int threadId, bool freeze, CancellationToken ct) =>
        _sta.InvokeAsync(() =>
        {
            var debugger = RequireDebugger();
            RequireStopped(debugger, "thread freeze");

            var thread = FindThread(debugger, threadId);

            if (freeze)
            {
                thread.Freeze();
            }
            else
            {
                thread.Thaw();
            }
        }, ct);

    // ---------------------------------------------------------------- stack and variables

    public Task<IReadOnlyList<StackFrame>> GetStackAsync(int threadId, int maxDepth, CancellationToken ct) =>
        _sta.InvokeAsync<IReadOnlyList<StackFrame>>(() =>
        {
            var debugger = RequireDebugger();
            RequireStopped(debugger, "stack");

            var thread = threadId > 0
                ? FindThread(debugger, threadId)
                : Safe(() => debugger.CurrentThread)
                  ?? throw new BackendException(
                      ErrorCodes.NotFound,
                      "現在のスレッドを取得できません。",
                      "stakeout threads でスレッド一覧を確認し、--thread で指定してください。");

            var actualThreadId = thread.ID;
            var frames = new List<StackFrame>();
            var depth = 0;

            // 1 段あたり 4 ms 前後かかる。深さは呼び出し側で必ず絞る（ADR 0008）
            foreach (DteStackFrame frame in thread.StackFrames)
            {
                if (depth >= maxDepth)
                {
                    break;
                }

                var frame2 = frame as DteStackFrame2;
                var language = Safe(() => frame.Language, string.Empty);
                var isExternal = !IsNativeLanguage(language);

                frames.Add(new StackFrame(
                    FrameId: _scope.AddFrame(actualThreadId, depth),
                    Depth: depth,
                    Function: Safe(() => frame.FunctionName, "(unknown)"),
                    File: frame2 is null ? null : NullIfEmpty(Safe(() => frame2.FileName)),
                    Line: frame2 is null ? null : SafeLine(frame2),
                    Address: 0,
                    Module: Safe(() => frame.Module, string.Empty),
                    IsExternal: isExternal));

                depth++;
            }

            return frames;
        }, ct);

    public Task<IReadOnlyList<Variable>> GetScopeAsync(int frameId, ScopeKind kind, CancellationToken ct) =>
        _sta.InvokeAsync<IReadOnlyList<Variable>>(() =>
        {
            var debugger = RequireDebugger();
            RequireStopped(debugger, "locals");

            var frame = ResolveFrame(debugger, frameId);
            var expressions = kind == ScopeKind.Arguments ? frame.Arguments : frame.Locals;

            var result = new List<Variable>();
            foreach (Expression expression in expressions)
            {
                result.Add(ToVariable(expression));
            }

            return result;
        }, ct);

    public Task<Variable> EvaluateAsync(string expr, int? frameId, EvalOptions options, CancellationToken ct) =>
        _sta.InvokeAsync(() =>
        {
            var debugger = RequireDebugger();
            RequireStopped(debugger, "eval");

            if (frameId is { } id)
            {
                // 式は現在のフレームのスコープで評価される。指定されたら先に切り替える
                var frame = ResolveFrame(debugger, id);
                debugger.CurrentStackFrame = frame;
            }

            var text = ApplyFormat(expr, options.Format);
            var expression = ComRetry.Run(
                () => debugger.GetExpression2(text, true, false, options.TimeoutMs),
                $"eval {expr}");

            var variable = ToVariable(expression, expr);

            if (!variable.IsValid)
            {
                // 値が読めないまま返すと、エージェントは理由が分からず同じ式を繰り返す。
                // 一番多い原因はスコープなので、それを名指しで案内する（ADR 0013）
                throw new BackendException(
                    ErrorCodes.NotFound,
                    $"式 '{expr}' を評価できません: {variable.Value}",
                    "式は停止しているフレームのスコープで解決されます。" +
                    "別モジュールのグローバルには {,,モジュール名}式 の形でモジュールを明示してください" +
                    "（例: {,,NativeLib.dll}g_ctx）。stakeout stack でどこに止まっているか確認できます。");
            }

            return variable;
        }, ct);

    public Task<IReadOnlyList<Variable>> ExpandAsync(int variablesReference, CancellationToken ct) =>
        _sta.InvokeAsync<IReadOnlyList<Variable>>(() =>
        {
            var debugger = RequireDebugger();
            RequireStopped(debugger, "expand");

            var expression = _scope.Expression(variablesReference)
                ?? throw new BackendException(
                    ErrorCodes.NotFound,
                    $"変数参照 {variablesReference} は無効です。",
                    "参照は停止ごとに作り直されます。stakeout locals または stakeout eval を実行し直してください。");

            var result = new List<Variable>();
            foreach (Expression member in expression.DataMembers)
            {
                result.Add(ToVariable(member));
            }

            return result;
        }, ct);

    /// <summary>1 回の式評価で読むバイト数。大きくすると DataMembers の展開が重くなる。</summary>
    private const int MemoryChunkBytes = 256;

    /// <summary>
    /// メモリを読む。EnvDTE に直接読む API は無いので、design.md §14 のとおり
    /// <c>*(unsigned char(*)[N])(ADDR)</c> を式として評価して要素を拾う。
    ///
    /// **読めなかったバイトを 0 で埋めない。最初に読めなかった位置で切って返す。**
    /// 未マップのページと 0x00 が並んだ領域は別物であり、混ぜるとメモリ破壊の調査で
    /// 誤った結論を導く。返した配列の長さが「ここまでは確かに読めた」を意味する。
    /// </summary>
    public Task<byte[]> ReadMemoryAsync(ulong address, int length, CancellationToken ct) =>
        _sta.InvokeAsync(() =>
        {
            var debugger = RequireDebugger();
            RequireStopped(debugger, "mem read");

            var bytes = new List<byte>(length);

            for (var offset = 0; offset < length; offset += MemoryChunkBytes)
            {
                ct.ThrowIfCancellationRequested();

                var count = Math.Min(MemoryChunkBytes, length - offset);
                var chunkAddress = address + (ulong)offset;

                // 書式指定子 x を付けて要求する。付けないと VS は 65 'A' のように
                // 文字リテラルを添えた 10 進で見せることがあり、解釈が曖昧になる
                var expr = $"*(unsigned char(*)[{count}])0x{chunkAddress:x}";
                var expression = ComRetry.Run(
                    () => debugger.GetExpression2(ApplyFormat(expr, "x"), true, false, 5000),
                    $"mem read 0x{chunkAddress:x}");

                if (!Safe(() => expression.IsValidValue, false))
                {
                    return bytes.ToArray();
                }

                var members = Safe(() => expression.DataMembers, null);
                var read = 0;

                foreach (Expression member in members ?? (IEnumerable)Array.Empty<Expression>())
                {
                    if (read >= count
                        || VsValueParser.ParseByte(Safe(() => member.Value, string.Empty)) is not { } value)
                    {
                        return bytes.ToArray();
                    }

                    bytes.Add(value);
                    read++;
                }

                if (read < count)
                {
                    return bytes.ToArray();
                }
            }

            return bytes.ToArray();
        }, ct);

    // ---------------------------------------------------------------- breakpoints

    public Task<Rpc.Breakpoint> SetBreakpointAsync(BreakpointRequest request, CancellationToken ct) =>
        _sta.InvokeAsync(() =>
        {
            var debugger = RequireDebugger();
            var collection = debugger.Breakpoints;

            if (request.Kind == BreakpointKind.Data)
            {
                EnsureDataSlotAvailable();

                // Add(Data:) には条件を渡していない。黙って捨てると、条件どおりに止まると
                // 思い込んだまま書き込みのたびに止まる
                if (request.Condition is { Length: > 0 })
                {
                    throw new BackendException(
                        ErrorCodes.Unsupported,
                        "データブレークポイントには条件を付けられません。",
                        "値で絞るなら、書き込む行に run-until FILE:LINE --cond を使ってください。" +
                        "誰が書いたかを知りたいだけなら watch-until-change を使ってください。");
                }
            }

            // Add の戻り値は当てにならないので、前後の差分で判定する（ADR 0006）
            var before = BreakpointTracker.Snapshot(collection);

            ComRetry.Run(() => AddBreakpoint(collection, request), $"bp set {request.Location}");

            var added = BreakpointTracker.Added(collection, before);
            if (added.Count == 0)
            {
                throw new BackendException(
                    ErrorCodes.Backend,
                    $"{request.Location} にブレークポイントを作成できませんでした。",
                    request.Kind == BreakpointKind.Data
                        ? "データ式は {,,モジュール名}&式 の形にし、そのモジュールのフレームで停止しているか確認してください。"
                        : "ファイル名と行番号、または関数名が正しいか確認してください。");
            }

            if (request.Kind == BreakpointKind.Line)
            {
                added = EnsureLineBreakpointBound(debugger, collection, request, added);
            }

            if (request.Kind == BreakpointKind.Data && request.ExpectedAddress is { Length: > 0 } expected)
            {
                // & を忘れた式が別アドレスを監視するのを弾く（ADR 0006 の罠 3）。
                // **弾いたものは消す。** 残すと stakeout の管理外で 4 本しかない枠を塞ぎ、
                // 同じアドレスへの次の要求が「作成できませんでした」で失敗し続ける
                try
                {
                    foreach (var bp in added)
                    {
                        BreakpointTracker.VerifyDataAddress(bp, expected);
                    }
                }
                catch (BackendException)
                {
                    foreach (var bp in added)
                    {
                        try
                        {
                            bp.Delete();
                        }
                        catch (Exception ex) when (ex is not BackendException)
                        {
                            // 消せなくても、元の失敗を返すほうが大事
                        }
                    }

                    throw;
                }
            }

            var registered = _breakpoints.Register(added[0], request.Kind, request.Location, request.Condition);

            // Add が複数作ることがある（同名関数など）。全部を stakeout の管理下に置く
            foreach (var extra in added.Skip(1))
            {
                _breakpoints.Register(extra, request.Kind, request.Location, request.Condition);
            }

            return registered;
        }, ct);

    public Task RemoveBreakpointAsync(int breakpointId, CancellationToken ct) =>
        _sta.InvokeAsync(() =>
        {
            RequireDebugger();

            if (!_breakpoints.TryGet(breakpointId, out var bp))
            {
                throw new BackendException(
                    ErrorCodes.NotFound,
                    $"ブレークポイント {breakpointId} は存在しません。",
                    "stakeout bp list で現在のブレークポイントを確認してください。");
            }

            try
            {
                bp.Delete();
            }
            catch (Exception ex) when (ComErrors.IsDisconnected(ex))
            {
                // VS が終了していた。消えているので目的は果たされている
            }

            _breakpoints.Remove(breakpointId);
        }, ct);

    public Task<IReadOnlyList<Rpc.Breakpoint>> ListBreakpointsAsync(CancellationToken ct) =>
        _sta.InvokeAsync(() => _breakpoints.List(), ct);

    public Task SetExceptionBreakAsync(string[] codes, bool breakWhenThrown, CancellationToken ct) =>
        _sta.InvokeAsync(() =>
        {
            var debugger = RequireDebugger();
            var matched = new List<string>();

            // 例外は名前ではなくコードで引く。グループ名も例外名も VS の表示言語で
            // 変わるため、名前で引くと日本語環境で必ず失敗する
            foreach (ExceptionSettings group in debugger.ExceptionGroups)
            {
                if (!Safe(() => group.SupportsExceptionCodes, false))
                {
                    continue;
                }

                foreach (var code in codes)
                {
                    if (!TryParseExceptionCode(code, out var value))
                    {
                        throw new BackendException(
                            ErrorCodes.Usage,
                            $"例外コード '{code}' を解釈できません。",
                            "16 進で指定してください（例: C0000005 または 0xC0000005）。");
                    }

                    var setting = Safe(() => group.ItemFromCode(value));
                    if (setting is null)
                    {
                        continue;
                    }

                    group.SetBreakWhenThrown(breakWhenThrown, setting);
                    matched.Add(Safe(() => setting.Name, code));
                }
            }

            if (matched.Count == 0)
            {
                throw new BackendException(
                    ErrorCodes.NotFound,
                    $"例外コード {string.Join(", ", codes)} に一致する設定がありません。",
                    "コードは 16 進で指定してください（例: C0000005 アクセス違反、C0000374 ヒープ破損）。");
            }

            _log($"exception break {(breakWhenThrown ? "on" : "off")}: {string.Join(", ", matched)}");
        }, ct);

    // ---------------------------------------------------------------- not yet

#pragma warning disable CS1998 // Phase 6 まで空。await するものが無い
    public async IAsyncEnumerable<Rpc.TraceEvent> DrainTraceEventsAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        yield break;
    }
#pragma warning restore CS1998

    public Task<IReadOnlyList<ModuleInfo>> GetModulesAsync(CancellationToken ct) =>
        throw BackendException.Unsupported(
            "モジュール一覧の取得",
            "EnvDTE から直接は取れません。stakeout stack の module 列を使ってください。");

    // ---------------------------------------------------------------- internals

    private Debugger3 RequireDebugger() =>
        _debugger ?? throw BackendException.NotAttached();

    private static void RequireStopped(Debugger3 debugger, string operation)
    {
        var mode = debugger.CurrentMode;
        if (mode == dbgDebugMode.dbgBreakMode)
        {
            return;
        }

        throw new BackendException(
            ErrorCodes.Precondition,
            $"{operation} は停止中にしか実行できません（現在: {Describe(mode)}）。",
            mode == dbgDebugMode.dbgRunMode
                ? "stakeout pause または stakeout wait で停止させてから実行してください。"
                : "stakeout attach で接続してください。");
    }

    private SessionState CurrentState() => _debugger?.CurrentMode switch
    {
        dbgDebugMode.dbgBreakMode => SessionState.AttachedStopped,
        dbgDebugMode.dbgRunMode => SessionState.AttachedRunning,
        _ => SessionState.Detached,
    };

    /// <summary>
    /// 状態を短時間だけ問い合わせる。
    /// stakeout status は健康診断であって調査の道具ではない。VS が応答しないときに
    /// ここで待たせると、状態を知りたいときほど返らない道具になる。
    /// </summary>
    private SessionState SafeState() =>
        _sta.InvokeWithFallback(CurrentState, SessionState.AttachedRunning, TimeSpan.FromSeconds(2));

    private static string Describe(dbgDebugMode mode) => mode switch
    {
        dbgDebugMode.dbgBreakMode => "停止中",
        dbgDebugMode.dbgRunMode => "実行中",
        _ => "未アタッチ",
    };

    /// <summary>アタッチ。3 段フォールバック（ADR 0005）。</summary>
    private string AttachCore(int pid, AttachOptions options)
    {
        var debugger = RequireDebugger();

        if (DebuggedPids(_dte!).Contains(pid))
        {
            return "already-debugged";
        }

        var target = FindLocalProcess(pid);

        if (options.Engines is { Length: > 0 } engines)
        {
            // 明示された engines で繋げないなら Attach() には落とさない。
            // エンジン指定は「マネージドを巻き込まない」という意図の表明である（ADR 0005）
            try
            {
                ((Process2)target).Attach2(engines);
                WaitForAttach(debugger);
                return $"Attach2({string.Join(",", engines)})";
            }
            catch (Exception ex)
            {
                throw new BackendException(
                    ErrorCodes.Unsupported,
                    $"指定されたエンジン ({string.Join(", ", engines)}) でアタッチできませんでした: {ex.Message}",
                    "--engines を外すと Visual Studio の自動選択でアタッチします。" +
                    "その場合、マネージドコードを巻き込む可能性があります。",
                    ex);
            }
        }

        try
        {
            ((Process2)target).Attach2("Native");
            WaitForAttach(debugger);
            return "Attach2(Native)";
        }
        catch (Exception ex) when (ex is not BackendException)
        {
            _log($"Attach2(Native) failed (hr=0x{ex.HResult:X8}), falling back to Attach()");
        }

        try
        {
            target.Attach();
            WaitForAttach(debugger);
            return "Attach()";
        }
        catch (Exception ex)
        {
            throw ComErrors.Translate(ex, $"pid {pid} へのアタッチ", Describe(debugger.CurrentMode));
        }
    }

    private void WaitForAttach(Debugger3 debugger)
    {
        var deadline = Environment.TickCount64 + 20_000;
        while (debugger.CurrentMode == dbgDebugMode.dbgDesignMode && Environment.TickCount64 < deadline)
        {
            System.Threading.Thread.Sleep(50);
        }

        if (debugger.CurrentMode == dbgDebugMode.dbgDesignMode)
        {
            throw new BackendException(
                ErrorCodes.Timeout,
                "アタッチ要求は通りましたが、20 秒待ってもデバッグが開始されませんでした。",
                "Visual Studio の状態を確認してください。");
        }
    }

    private DteProcess FindLocalProcess(int pid)
    {
        var debugger = RequireDebugger();

        foreach (DteProcess process in debugger.LocalProcesses)
        {
            if (process.ProcessID == pid)
            {
                return process;
            }
        }

        throw new BackendException(
            ErrorCodes.NotFound,
            $"pid {pid} が Visual Studio から見えません。",
            "プロセスが起動しているか、stakeout と Visual Studio の昇格レベルが揃っているか確認してください。");
    }

    private DteProcess? FindDebuggedProcess(int pid)
    {
        foreach (DteProcess process in RequireDebugger().DebuggedProcesses)
        {
            if (process.ProcessID == pid)
            {
                return process;
            }
        }

        return null;
    }

    private static int[] DebuggedPids(object dte)
    {
        try
        {
            var debugger = ((DTE2)dte).Debugger;
            var pids = new List<int>();
            foreach (DteProcess process in debugger.DebuggedProcesses)
            {
                pids.Add(process.ProcessID);
            }

            return pids.ToArray();
        }
        catch (Exception)
        {
            return Array.Empty<int>();
        }
    }

    private static string SafeName(DteProcess process) => Safe(() => process.Name, string.Empty);

    private static void SelectThread(Debugger3 debugger, int threadId) =>
        debugger.CurrentThread = FindThread(debugger, threadId);

    private static DteThread FindThread(Debugger3 debugger, int threadId)
    {
        foreach (DteThread thread in ThreadsOf(debugger))
        {
            if (thread.ID == threadId)
            {
                return thread;
            }
        }

        throw new BackendException(
            ErrorCodes.NotFound,
            $"スレッド {threadId} が見つかりません。",
            "stakeout threads でスレッド一覧を確認してください。");
    }

    private static IEnumerable<DteThread> ThreadsOf(Debugger3 debugger)
    {
        var program = debugger.CurrentProgram
            ?? throw new BackendException(
                ErrorCodes.NotAttached,
                "デバッグ中のプログラムがありません。",
                "stakeout attach で接続してください。");

        foreach (DteThread thread in program.Threads)
        {
            yield return thread;
        }
    }

    private DteStackFrame ResolveFrame(Debugger3 debugger, int frameId)
    {
        var reference = _scope.Frame(frameId)
            ?? throw new BackendException(
                ErrorCodes.NotFound,
                $"フレーム {frameId} は無効です。",
                "フレーム ID は停止ごとに作り直されます。stakeout stack を実行し直してください。");

        var thread = FindThread(debugger, reference.ThreadId);
        var depth = 0;

        foreach (DteStackFrame frame in thread.StackFrames)
        {
            if (depth == reference.Depth)
            {
                return frame;
            }

            depth++;
        }

        throw new BackendException(
            ErrorCodes.NotFound,
            $"フレーム {frameId} は現在のスタックにありません。",
            "stakeout stack を実行し直してください。");
    }

    private Variable ToVariable(Expression expression, string? nameOverride = null)
    {
        var type = Safe(() => expression.Type, string.Empty);
        var value = Safe(() => expression.Value, string.Empty);
        var valid = Safe(() => expression.IsValidValue, false);

        var childCount = Safe(() => expression.DataMembers?.Count, 0);

        return new Variable(
            Name: nameOverride ?? Safe(() => expression.Name, string.Empty),
            Type: type,
            Value: value,
            // ポインタや &expr は 0x... 付きで返る。ここで数値に戻しておくと、
            // mem.read がアドレスを得るためだけに再評価しなくて済む
            Address: VsValueParser.TryExtractAddress(value, out var addr) ? addr : null,
            VariablesReference: _scope.AddExpression(expression, childCount > 0),
            IsPointer: type.Contains('*', StringComparison.Ordinal),
            IsValid: valid);
    }

    /// <summary>書式指定子を式に付ける（design.md §10.7）。</summary>
    private static string ApplyFormat(string expr, string? format) =>
        string.IsNullOrWhiteSpace(format) ? expr : $"{expr},{format}";

    private void EnsureDataSlotAvailable()
    {
        if (_breakpoints.DataBreakpointCount < Capabilities.DataBreakpointSlots)
        {
            return;
        }

        throw new BackendException(
            ErrorCodes.Unsupported,
            $"ハードウェアデータブレークポイントを使い切っています " +
            $"({Capabilities.DataBreakpointSlots} 本)。",
            "stakeout bp list で確認し、不要なデータブレークポイントを stakeout bp rm で削除してください。");
    }

    /// <summary>
    /// 行ブレークポイントがコードに結び付いたかを確かめ、結び付かなければ別の候補で張り直す（ADR 0023）。
    ///
    /// ファイル名だけの指定は、VS が開いている同名の別ファイルに解決されることがある。
    /// そのブレークポイントは Enabled のまま一度も止まらず、エラーにもならない。
    /// 結び付いたかは、子（束縛された位置）の有無で分かる。
    /// </summary>
    private List<DteBreakpoint> EnsureLineBreakpointBound(
        Debugger3 debugger, Breakpoints collection, BreakpointRequest request, List<DteBreakpoint> added)
    {
        // デバッグしていなければ、何にも結び付かないのが正常
        if (debugger.CurrentMode == dbgDebugMode.dbgDesignMode || WaitBound(added))
        {
            return added;
        }

        var (file, line) = SplitFileLine(request.Location);
        var resolved = added.Select(bp => ReadQuietly(() => bp.File)).FirstOrDefault(f => f is { Length: > 0 });

        foreach (var candidate in SourceFileCandidates.For(file, _sourceRoot, OpenDocumentPaths()))
        {
            if (string.Equals(candidate, resolved, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var before = BreakpointTracker.Snapshot(collection);
            ComRetry.Run(
                () => collection.Add(
                    File: candidate,
                    Line: line,
                    Condition: request.Condition ?? string.Empty,
                    ConditionType: dbgBreakpointConditionType.dbgBreakpointConditionTypeWhenTrue),
                $"bp set {candidate}:{line}");
            var retry = BreakpointTracker.Added(collection, before);

            if (WaitBound(retry))
            {
                _log($"bp {request.Location}: {resolved ?? "(不明)"} に結び付かず、{candidate} で張り直した");
                DeleteQuietly(added);
                return retry;
            }

            DeleteQuietly(retry);
        }

        // 結び付かないブレークポイントを残さない。残すと run-until が時間切れまで黙って待つ
        DeleteQuietly(added);

        throw new BackendException(
            ErrorCodes.NotFound,
            $"{request.Location} はどのモジュールのコードにも結び付きませんでした" +
            (resolved is null ? "。" : $"（Visual Studio は {resolved} に解決しました）。"),
            "ファイルをフルパスで指定してください。ファイル名だけだと、Visual Studio が開いている同名の別ファイルに" +
            "解決されることがあります。対象の DLL がまだ読み込まれていないなら、読み込まれてから張り直してください。");
    }

    private static bool WaitBound(List<DteBreakpoint> breakpoints)
    {
        var deadline = Environment.TickCount64 + (long)BindTimeout.TotalMilliseconds;

        while (true)
        {
            if (breakpoints.Any(bp => ReadQuietly(() => bp.Children.Count, 0) > 0))
            {
                return true;
            }

            if (Environment.TickCount64 >= deadline)
            {
                return false;
            }

            System.Threading.Thread.Sleep(50);
        }
    }

    private List<string> OpenDocumentPaths()
    {
        var paths = new List<string>();

        try
        {
            foreach (Document document in _dte!.Documents)
            {
                if (ReadQuietly(() => document.FullName) is { Length: > 0 } path)
                {
                    paths.Add(path);
                }
            }
        }
        catch (Exception ex) when (ex is not BackendException)
        {
            // 開いているドキュメントが読めなくても、索引ルートからは探せる
        }

        return paths;
    }

    private static void DeleteQuietly(IEnumerable<DteBreakpoint> breakpoints)
    {
        foreach (var bp in breakpoints)
        {
            try
            {
                bp.Delete();
            }
            catch (Exception ex) when (ex is not BackendException)
            {
                // 消せなくても、呼び出し側に返す結果のほうが大事
            }
        }
    }

    private static T ReadQuietly<T>(Func<T> read, T fallback = default!)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is not BackendException)
        {
            return fallback;
        }
    }

    private static void AddBreakpoint(Breakpoints collection, BreakpointRequest request)
    {
        switch (request.Kind)
        {
            case BreakpointKind.Line:
            {
                var (file, line) = SplitFileLine(request.Location);
                collection.Add(
                    File: file,
                    Line: line,
                    Condition: request.Condition ?? string.Empty,
                    ConditionType: dbgBreakpointConditionType.dbgBreakpointConditionTypeWhenTrue);
                break;
            }

            case BreakpointKind.Function:
                collection.Add(
                    Function: request.Location,
                    Condition: request.Condition ?? string.Empty,
                    ConditionType: dbgBreakpointConditionType.dbgBreakpointConditionTypeWhenTrue);
                break;

            case BreakpointKind.Address:
                collection.Add(
                    Address: request.Location,
                    Condition: request.Condition ?? string.Empty,
                    ConditionType: dbgBreakpointConditionType.dbgBreakpointConditionTypeWhenTrue);
                break;

            case BreakpointKind.Data:
                collection.Add(Data: request.Location, DataCount: request.DataSize);
                break;

            default:
                throw new BackendException(
                    ErrorCodes.Usage,
                    $"未知のブレークポイント種別です: {request.Kind}",
                    "line / function / address / data のいずれかを指定してください。");
        }
    }

    private static (string File, int Line) SplitFileLine(string location)
    {
        var separator = location.LastIndexOf(':');
        if (separator > 0 && int.TryParse(location[(separator + 1)..], out var line))
        {
            return (location[..separator], line);
        }

        throw new BackendException(
            ErrorCodes.Usage,
            $"位置 '{location}' を FILE:LINE として解釈できません。",
            "nativelib.c:42 のように指定してください。");
    }

    private void RemoveOwnBreakpoints()
    {
        foreach (var id in _breakpoints.Ids)
        {
            if (!_breakpoints.TryGet(id, out var bp))
            {
                continue;
            }

            try
            {
                bp.Delete();
            }
            catch (Exception)
            {
                // 消せないものは諦める。デタッチ自体は続ける
            }

            _breakpoints.Remove(id);
        }
    }

    private StopEvent BuildStopEvent()
    {
        var debugger = RequireDebugger();

        var lastHit = Safe(() => debugger.BreakpointLastHit);
        var breakpointId = _breakpoints.IdOf(lastHit);
        var reason = MapReason(Safe(() => debugger.LastBreakReason, dbgEventReason.dbgEventReasonNone), breakpointId);

        var stop = new StopEvent(
            SessionId: _session?.SessionId ?? string.Empty,
            Reason: reason,
            ThreadId: Safe(() => debugger.CurrentThread?.ID ?? 0, 0),
            BreakpointId: breakpointId,
            ExceptionCode: null,
            Description: Safe(() => debugger.CurrentStackFrame?.FunctionName),
            At: DateTimeOffset.Now);

        _lastStop = stop;
        return stop;
    }

    private StopReason MapReason(dbgEventReason reason, int? breakpointId) => reason switch
    {
        dbgEventReason.dbgEventReasonBreakpoint =>
            breakpointId is { } id && _breakpoints.KindOf(id) == BreakpointKind.Data
                ? StopReason.DataBreakpoint
                : StopReason.Breakpoint,
        dbgEventReason.dbgEventReasonStep => StopReason.Step,
        dbgEventReason.dbgEventReasonAttachProgram => StopReason.EntryPoint,
        dbgEventReason.dbgEventReasonUserBreak => StopReason.Pause,
        dbgEventReason.dbgEventReasonExceptionThrown => StopReason.Exception,
        dbgEventReason.dbgEventReasonExceptionNotHandled => StopReason.Exception,
        dbgEventReason.dbgEventReasonEndProgram => StopReason.Exit,
        _ => StopReason.Unknown,
    };

    /// <summary>
    /// 停止イベントを購読する。**参照をフィールドで保持する**こと（ADR 0008）。
    /// ローカル変数だと GC された時点で配送が止まり、再現困難な取りこぼしになる。
    /// ポーリングが主なので、これが止まっても機能は失われず遅くなるだけである。
    /// </summary>
    private void SubscribeDebuggerEvents()
    {
        if (_dte is null || _debuggerEvents is not null)
        {
            return;
        }

        _debuggerEvents = _dte.Events.DebuggerEvents;
        _debuggerEvents.OnEnterBreakMode += OnEnterBreakMode;
    }

    private void UnsubscribeDebuggerEvents()
    {
        if (_debuggerEvents is null)
        {
            return;
        }

        try
        {
            _debuggerEvents.OnEnterBreakMode -= OnEnterBreakMode;
        }
        catch (Exception)
        {
            // VS が終了していた
        }

        _debuggerEvents = null;
    }

    private void OnEnterBreakMode(dbgEventReason reason, ref dbgExecutionAction action) =>
        Interlocked.Increment(ref _breakEventCount);

    private static bool IsNativeLanguage(string language) =>
        language.Length == 0
        || language.Contains("C++", StringComparison.OrdinalIgnoreCase)
        || language.Equals("C", StringComparison.OrdinalIgnoreCase);

    private static int? SafeLine(DteStackFrame2 frame)
    {
        var line = Safe(() => (int)frame.LineNumber, 0);
        return line > 0 ? line : null;
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>C0000005 / 0xC0000005 のどちらの書き方も受ける。</summary>
    private static bool TryParseExceptionCode(string text, out uint code)
    {
        var digits = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text;
        return uint.TryParse(digits, System.Globalization.NumberStyles.HexNumber, null, out code);
    }

    /// <summary>
    /// COM の取得系は、状態によっては例外を投げる。診断のための付加情報のために
    /// 操作全体を失敗させたくないので、取れなければ既定値で続ける。
    /// </summary>
    private static T Safe<T>(Func<T> get, T fallback = default!)
    {
        try
        {
            return get();
        }
        catch (Exception ex) when (ex is not BackendException)
        {
            return fallback;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await DetachAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _log($"detach on dispose failed: {ex.Message}");
        }

        _sta.Dispose();
    }
}
