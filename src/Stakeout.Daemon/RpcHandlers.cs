using System.Diagnostics;
using Stakeout.Core;
using Stakeout.Rpc;
using StreamJsonRpc;

namespace Stakeout.Daemon;

/// <summary>
/// RPC のハンドラ（design.md §7.1）。StreamJsonRpc がこのインスタンスのメソッドを公開する。
///
/// **すべてのハンドラは <see cref="RpcResult"/> を返し、例外を外に出さない。**
/// JSON-RPC のエラー機構ではなくペイロードで成否を運ぶのが design.md §7 の約束であり、
/// 例外が漏れると CLI 側でその約束が崩れる。
/// </summary>
public sealed class RpcHandlers
{
    /// <summary>停止要求を受けてから実際に落ちるまでの猶予。応答とログを書き切るため。</summary>
    private static readonly TimeSpan ShutdownGrace = TimeSpan.FromMilliseconds(250);

    /// <summary>全スレッドのスタックを取るときの既定の深さ（ADR 0008）。</summary>
    private const int DefaultAllStackDepth = 5;

    /// <summary>単一スレッドのスタックを取るときの既定の深さ。</summary>
    private const int DefaultStackDepth = 30;

    private readonly DaemonState _state;
    private readonly string _pipeName;

    public RpcHandlers(DaemonState state, string pipeName)
    {
        _state = state;
        _pipeName = pipeName;
    }

    // ---------------------------------------------------------------- daemon

    [JsonRpcMethod(RpcMethods.DaemonPing)]
    public RpcResult Ping() =>
        Handle(RpcMethods.DaemonPing, null, () =>
            new DaemonPong(StakeoutPaths.Version, Environment.ProcessId));

    [JsonRpcMethod(RpcMethods.DaemonStatus)]
    public RpcResult Status() =>
        Handle(RpcMethods.DaemonStatus, null, () =>
            _state.BuildStatus(_pipeName, DateTimeOffset.Now));

    [JsonRpcMethod(RpcMethods.DaemonShutdown)]
    public RpcResult Shutdown() =>
        Handle(RpcMethods.DaemonShutdown, null, () =>
        {
            var sessions = _state.Sessions.Count;

            // 応答とログを書き切ってから落ちる。ここで即座に停止すると、
            // クライアントが応答を受け取れず、成功したのに失敗扱いになる
            _ = Task.Run(async () =>
            {
                await Task.Delay(ShutdownGrace);
                _state.RequestShutdown();
            });

            return new DaemonShutdownResult(sessions);
        });

    // ---------------------------------------------------------------- session

    [JsonRpcMethod(RpcMethods.TargetList)]
    public RpcResult TargetList(bool all = false) =>
        HandlePaged(RpcMethods.TargetList, new { all }, () => _state.Group.ListTargets(all));

    [JsonRpcMethod(RpcMethods.SessionAttach)]
    public Task<RpcResult> Attach(AttachRequest request) =>
        HandleAsync(RpcMethods.SessionAttach, request, async ct =>
            await _state.Group.AttachAsync(request, ct));

    [JsonRpcMethod(RpcMethods.SessionDetach)]
    public Task<RpcResult> Detach(RpcRequestBase? request = null) =>
        HandleAsync(RpcMethods.SessionDetach, request, async ct =>
        {
            await _state.Group.DetachAsync(ct);
            return new { detached = true };
        });

    [JsonRpcMethod(RpcMethods.SessionInfo)]
    public RpcResult SessionInfo() =>
        Handle(RpcMethods.SessionInfo, null, () =>
            _state.Group.Session
            ?? throw BackendException.NotAttached());

    // ---------------------------------------------------------------- execution

    [JsonRpcMethod(RpcMethods.ExecContinue)]
    public Task<RpcResult> Continue(RpcRequestBase? request = null) =>
        HandleAsync(RpcMethods.ExecContinue, request, async ct =>
        {
            await _state.Group.RunAsync(async backend =>
            {
                await backend.ContinueAsync(ct);
                return true;
            }, ct);

            return new { running = true };
        });

    [JsonRpcMethod(RpcMethods.ExecPause)]
    public Task<RpcResult> Pause(RpcRequestBase? request = null) =>
        HandleAsync(RpcMethods.ExecPause, request, async ct =>
        {
            await _state.Group.RunAsync(async backend =>
            {
                await backend.PauseAsync(ct);
                return true;
            }, ct);

            return new { paused = true };
        });

    [JsonRpcMethod(RpcMethods.ExecStep)]
    public Task<RpcResult> Step(StepRequest request) =>
        HandleAsync(RpcMethods.ExecStep, request, async ct =>
        {
            await _state.Group.RunAsync(async backend =>
            {
                await backend.StepAsync(request.Kind, request.ThreadId ?? 0, ct);
                return true;
            }, ct);

            return new { stepped = true };
        });

    [JsonRpcMethod(RpcMethods.ExecWait)]
    public Task<RpcResult> Wait(WaitRequest? request = null) =>
        HandleAsync(RpcMethods.ExecWait, request, async ct =>
        {
            var timeout = TimeSpan.FromMilliseconds(
                request?.TimeoutMs ?? _state.Config.Limits.WaitSec * 1000);

            // 待ちは直列化しない。ここを直列化すると、待っている間
            // stakeout status すら返らなくなる（design.md §7.2）
            var stop = await _state.Group.RunUnserializedAsync(
                backend => backend.WaitForStopAsync(timeout, ct));

            return new WaitResult(stop is not null, stop);
        });

    // ---------------------------------------------------------------- threads

    [JsonRpcMethod(RpcMethods.ThreadsList)]
    public Task<RpcResult> Threads(RpcRequestBase? request = null) =>
        HandlePagedAsync(RpcMethods.ThreadsList, request, ct =>
            _state.Group.RunAsync(backend => backend.GetThreadsAsync(ct), ct));

    [JsonRpcMethod(RpcMethods.ThreadsSelect)]
    public Task<RpcResult> ThreadSelect(ThreadTargetRequest request) =>
        HandleAsync(RpcMethods.ThreadsSelect, request, ct =>
            _state.Group.RunAsync(async backend =>
            {
                var threadId = await Composite(backend).ResolveThreadAsync(request, ct);
                await backend.SetCurrentThreadAsync(threadId, ct);
                return new ThreadActionResult(threadId, false);
            }, ct));

    [JsonRpcMethod(RpcMethods.ThreadsFreeze)]
    public Task<RpcResult> ThreadFreeze(ThreadTargetRequest request) =>
        HandleAsync(RpcMethods.ThreadsFreeze, request, ct =>
            _state.Group.RunAsync(async backend =>
            {
                var threadId = await Composite(backend).ResolveThreadAsync(request, ct);
                await backend.FreezeThreadAsync(threadId, request.Freeze, ct);
                return new ThreadActionResult(threadId, request.Freeze);
            }, ct));

    // ---------------------------------------------------------------- stack and variables

    [JsonRpcMethod(RpcMethods.StackGet)]
    public Task<RpcResult> Stack(StackRequest? request = null) =>
        HandlePagedAsync(RpcMethods.StackGet, request, ct =>
            _state.Group.RunAsync(async backend =>
            {
                var req = request ?? new StackRequest();
                var limits = _state.Config.Limits;

                // スタック 1 段の取得は 4 ms 前後かかる。深さを絞らないと
                // 20 スレッドで十数秒かかる（ADR 0008）
                var depth = req.MaxDepth ?? (req.All ? DefaultAllStackDepth : DefaultStackDepth);
                var result = new List<ThreadStack>();
                var budget = limits.MaxFramesPerRequest;

                if (!req.All)
                {
                    var frames = await backend.GetStackAsync(req.ThreadId ?? 0, Math.Min(depth, budget), ct);
                    result.Add(new ThreadStack(
                        req.ThreadId ?? 0, string.Empty, frames, frames.Count >= depth));
                    return (IReadOnlyList<ThreadStack>)result;
                }

                foreach (var thread in await backend.GetThreadsAsync(ct))
                {
                    if (budget <= 0)
                    {
                        break;
                    }

                    var frames = await backend.GetStackAsync(thread.ThreadId, Math.Min(depth, budget), ct);
                    budget -= frames.Count;

                    result.Add(new ThreadStack(
                        thread.ThreadId, thread.Name, frames, frames.Count >= depth));
                }

                return (IReadOnlyList<ThreadStack>)result;
            }, ct));

    [JsonRpcMethod(RpcMethods.VarsScope)]
    public Task<RpcResult> Scope(ScopeRequest request) =>
        HandlePagedAsync(RpcMethods.VarsScope, request, ct =>
            _state.Group.RunAsync(backend => backend.GetScopeAsync(request.FrameId, request.Kind, ct), ct));

    [JsonRpcMethod(RpcMethods.VarsEval)]
    public Task<RpcResult> Eval(EvalRequest request) =>
        HandleAsync(RpcMethods.VarsEval, request, ct =>
            _state.Group.RunAsync(
                backend => backend.EvaluateAsync(
                    request.Expr,
                    request.FrameId,
                    new EvalOptions(request.Format, request.TimeoutMs ?? 5000),
                    ct),
                ct));

    [JsonRpcMethod(RpcMethods.VarsExpand)]
    public Task<RpcResult> Expand(ExpandRequest request) =>
        HandlePagedAsync(RpcMethods.VarsExpand, request, ct =>
            _state.Group.RunAsync(backend => backend.ExpandAsync(request.Ref, ct), ct));

    [JsonRpcMethod(RpcMethods.MemRead)]
    public Task<RpcResult> MemRead(MemReadRequest request) =>
        HandleAsync(RpcMethods.MemRead, request, ct =>
            _state.Group.RunAsync(
                async backend =>
                {
                    var max = _state.Config.Limits.MaxMemoryReadBytes;

                    if (request.Length is <= 0 || request.Length > max)
                    {
                        throw new BackendException(
                            ErrorCodes.Usage,
                            $"length は 1〜{max} の範囲で指定してください（要求: {request.Length}）。",
                            "大きな領域が要るなら limits.maxMemoryReadBytes を上げてください。" +
                            "EnvDTE では式評価で読むため、大きくすると比例して遅くなります。");
                    }

                    var address = await ResolveAddressAsync(backend, request, ct);
                    var bytes = await backend.ReadMemoryAsync(address, request.Length, ct);

                    return MemoryFormat.ToBlock(address, request.Length, bytes);
                },
                ct));

    /// <summary>
    /// アドレスを決める。リテラルならそのまま、そうでなければ式として評価する。
    /// **式の構文は Backend のものなので、ここでは解釈しない**（ADR 0020）。
    /// </summary>
    private static async Task<ulong> ResolveAddressAsync(
        IDebuggerBackend backend, MemReadRequest request, CancellationToken ct)
    {
        if (AddressParser.TryParse(request.Address, out var literal))
        {
            return literal;
        }

        if (string.IsNullOrWhiteSpace(request.Address))
        {
            throw new BackendException(
                ErrorCodes.Usage,
                "読む先が指定されていません。",
                "0x7ff6a2c31040 のようなアドレスか、&g_ctx のような式を渡してください。");
        }

        var variable = await backend.EvaluateAsync(
            request.Address, request.FrameId, new EvalOptions(null, request.TimeoutMs ?? 5000), ct);

        if (variable.Address is { } resolved)
        {
            return resolved;
        }

        throw new BackendException(
            ErrorCodes.Usage,
            $"{request.Address} はアドレスになりませんでした（値: {variable.Value}）。",
            "ポインタか、& を付けた式を渡してください。例: &g_ctx、p->buffer");
    }

    // ---------------------------------------------------------------- breakpoints

    [JsonRpcMethod(RpcMethods.BreakpointSet)]
    public Task<RpcResult> BreakpointSet(BreakpointSetRequest request) =>
        HandleAsync(RpcMethods.BreakpointSet, request, ct =>
            _state.Group.RunAsync(
                backend => backend.SetBreakpointAsync(
                    new BreakpointRequest(
                        request.Kind,
                        request.Location,
                        request.Condition,
                        request.HitCount,
                        DataSize: request.DataSize),
                    ct),
                ct));

    [JsonRpcMethod(RpcMethods.BreakpointRemove)]
    public Task<RpcResult> BreakpointRemove(BreakpointRemoveRequest request) =>
        HandleAsync(RpcMethods.BreakpointRemove, request, async ct =>
        {
            await _state.Group.RunAsync(async backend =>
            {
                await backend.RemoveBreakpointAsync(request.Id, ct);
                return true;
            }, ct);

            return new { removed = request.Id };
        });

    [JsonRpcMethod(RpcMethods.BreakpointList)]
    public Task<RpcResult> BreakpointList(RpcRequestBase? request = null) =>
        HandlePagedAsync(RpcMethods.BreakpointList, request, ct =>
            _state.Group.RunAsync(backend => backend.ListBreakpointsAsync(ct), ct));

    [JsonRpcMethod(RpcMethods.BreakpointClear)]
    public Task<RpcResult> BreakpointClear(RpcRequestBase? request = null) =>
        HandleAsync(RpcMethods.BreakpointClear, request, ct =>
            _state.Group.RunAsync(async backend =>
            {
                var removed = 0;
                foreach (var bp in await backend.ListBreakpointsAsync(ct))
                {
                    await backend.RemoveBreakpointAsync(bp.BreakpointId, ct);
                    removed++;
                }

                return new BreakpointClearResult(removed);
            }, ct));

    [JsonRpcMethod(RpcMethods.BreakpointExceptions)]
    public Task<RpcResult> BreakpointExceptions(ExceptionBreakRequest request) =>
        HandleAsync(RpcMethods.BreakpointExceptions, request, async ct =>
        {
            await _state.Group.RunAsync(async backend =>
            {
                await backend.SetExceptionBreakAsync(request.Codes, request.BreakWhenThrown, ct);
                return true;
            }, ct);

            return new { codes = request.Codes, breakWhenThrown = request.BreakWhenThrown };
        });

    // ---------------------------------------------------------------- composites

    [JsonRpcMethod(RpcMethods.VarsDump)]
    public Task<RpcResult> Dump(DumpRequest request) =>
        HandlePagedAsync(RpcMethods.VarsDump, request, ct =>
            _state.Group.RunAsync<IReadOnlyList<DumpNode>>(
                async backend => (await Composite(backend).DumpAsync(request, ct)).Nodes, ct));

    [JsonRpcMethod(RpcMethods.CompositeRunUntil)]
    public Task<RpcResult> RunUntil(RunUntilRequest request) =>
        HandleAsync(RpcMethods.CompositeRunUntil, request, ct =>
            _state.Group.RunAsync(backend => Composite(backend).RunUntilAsync(request, ct), ct));

    [JsonRpcMethod(RpcMethods.CompositeWatchUntilChange)]
    public Task<RpcResult> WatchUntilChange(WatchUntilChangeRequest request) =>
        HandleAsync(RpcMethods.CompositeWatchUntilChange, request, ct =>
            _state.Group.RunAsync(backend => Composite(backend).WatchUntilChangeAsync(request, ct), ct));

    [JsonRpcMethod(RpcMethods.CompositeTraceExpression)]
    public Task<RpcResult> TraceExpression(TraceExprRequest request) =>
        HandleAsync(RpcMethods.CompositeTraceExpression, request, ct =>
            _state.Group.RunAsync(backend => Composite(backend).TraceExprAsync(request, ct), ct));

    [JsonRpcMethod(RpcMethods.CompositeTaskMap)]
    public Task<RpcResult> TaskMap(RpcRequestBase? request = null) =>
        HandleAsync(RpcMethods.CompositeTaskMap, request, ct =>
            _state.Group.RunAsync(backend => Composite(backend).TaskMapAsync(ct), ct));

    private CompositeService Composite(IDebuggerBackend backend) => new(backend, _state.Config);

    // ---------------------------------------------------------------- code index

    [JsonRpcMethod(RpcMethods.CodeDefinitions)]
    public RpcResult CodeDefinitions(CodeQueryRequest request) =>
        HandlePaged(RpcMethods.CodeDefinitions, request, () => CodeIndex().Definitions(request.Symbol));

    [JsonRpcMethod(RpcMethods.CodeReferences)]
    public RpcResult CodeReferences(CodeQueryRequest request) =>
        HandlePaged(RpcMethods.CodeReferences, request, () => CodeIndex().References(request.Symbol));

    [JsonRpcMethod(RpcMethods.CodeWriters)]
    public RpcResult CodeWriters(CodeQueryRequest request) =>
        HandlePaged(RpcMethods.CodeWriters, request, () => CodeIndex().Writers(request.Symbol));

    [JsonRpcMethod(RpcMethods.CompositeFindCorruption)]
    public Task<RpcResult> FindCorruption(FindCorruptionRequest request) =>
        HandleAsync(RpcMethods.CompositeFindCorruption, request, ct =>
            _state.Group.RunAsync(
                backend => Composite(backend).FindCorruptionAsync(request, CodeIndex(), ct), ct));

    private CodeIndex CodeIndex() => new(_state.Config.Code);

    // ---------------------------------------------------------------- doctor

    /// <summary>
    /// design.md §3.2 の確認項目に、実機を見て答える。
    ///
    /// 表を人が埋めるものにしておくと、埋まらないまま実装が進む。
    /// 道具が答えられるものは道具が答える。答えられないものは
    /// **答えられないと言って、次に何を見ればよいかを書く。**
    /// </summary>
    [JsonRpcMethod(RpcMethods.DaemonDoctor)]
    public Task<RpcResult> Doctor(DoctorRequest? request = null) =>
        HandleAsync(RpcMethods.DaemonDoctor, request, async ct =>
        {
            var session = _state.Group.Session;
            var findings = new List<Diagnosis>
            {
                EnvironmentDiagnostics.ProcessCount(_state.Group.ListTargets(includeDenied: false)),
            };

            // Q2〜Q4 はスレッドを見ないと答えられない。停止中でなければ飛ばす
            IReadOnlyList<TaskInfo> tasks = Array.Empty<TaskInfo>();
            if (session is { State: SessionState.AttachedStopped })
            {
                try
                {
                    tasks = (await _state.Group.RunAsync(
                        backend => Composite(backend).TaskMapAsync(ct), ct)).Tasks;
                }
                catch (BackendException)
                {
                    // 取れなくても他の項目は答えられる
                }
            }

            findings.Add(ThreadDiagnostics.TaskMapping(tasks));
            findings.Add(ThreadDiagnostics.ConcurrentExecution(tasks));
            findings.Add(ThreadDiagnostics.ThreadNames(tasks));
            findings.Add(EnvironmentDiagnostics.Bitness(session?.Pid));
            findings.Add(EnvironmentDiagnostics.DebuggingTools(_state.Config.DbgEng, _state.Config.Backend));
            findings.Add(EnvironmentDiagnostics.Elevation(StakeoutPaths.IsElevated(), VisualStudioPid()));
            findings.Add(EnvironmentDiagnostics.AddressSanitizer());

            return new DoctorReport(session?.Pid, session?.ProcessName, findings);
        });

    /// <summary>接続している Visual Studio の pid。EnvDTE 以外の Backend では null。</summary>
    private int? VisualStudioPid() =>
        (_state.Group.Backend as Backend.EnvDte.EnvDteBackend)?.VisualStudio?.Pid;

    // ---------------------------------------------------------------- log

    [JsonRpcMethod(RpcMethods.LogTail)]
    public RpcResult LogTail(LogTailRequest? request = null) =>
        HandlePaged(RpcMethods.LogTail, request, () =>
        {
            var count = Math.Clamp(request?.Count ?? 50, 1, 1000);

            // 行は JSON 文字列のまま返す。デーモンで parse し直すと、
            // 切り詰めた params / result を二重に加工することになる
            return _state.Log.Tail(count, request?.Kind);
        });

    // ---------------------------------------------------------------- paging

    [JsonRpcMethod(RpcMethods.CursorNext)]
    public RpcResult CursorNext(CursorRequest request) =>
        HandlePaged(RpcMethods.CursorNext, request, () =>
        {
            if (!_state.Cursors.TryTake(request.Cursor, out var rest) || rest is not IEnumerable<object> items)
            {
                throw new BackendException(
                    ErrorCodes.NotFound,
                    "そのカーソルは無効か、期限切れです。",
                    "カーソルは 10 分で切れます。同じコマンドを最初から実行し直してください。");
            }

            return items.ToArray();
        });

    /// <summary>
    /// 配列の応答を上限バイト数で切り、残りをカーソルに預ける（design.md §8.5）。
    /// 切らずに返すと、大きなスタックや dump がそのままエージェントの文脈を食い潰す。
    /// </summary>
    private RpcResult PageOf<T>(IReadOnlyList<T> items)
    {
        var page = Pager.Split(items, _state.Config.Limits.ResponseBytes, RpcJson.Options);

        if (!page.Truncated)
        {
            return RpcResult.Success(page.Items);
        }

        var cursor = _state.Cursors.Create(page.Rest.Cast<object>().ToArray());
        return RpcResult.Partial(page.Items, cursor);
    }

    private RpcResult HandlePaged<T>(string method, object? parameters, Func<IReadOnlyList<T>> body)
    {
        _state.Log.RpcRequest(method, parameters);
        var sw = Stopwatch.StartNew();

        try
        {
            var result = PageOf(body());
            sw.Stop();
            _state.Log.RpcResponse(method, result.Data, error: null, sw.Elapsed.TotalMilliseconds);
            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            return Fail(method, ex, sw.Elapsed.TotalMilliseconds);
        }
    }

    private async Task<RpcResult> HandlePagedAsync<T>(
        string method, object? parameters, Func<CancellationToken, Task<IReadOnlyList<T>>> body)
    {
        _state.Log.RpcRequest(method, parameters);
        var sw = Stopwatch.StartNew();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_state.ShutdownToken);
        cts.CancelAfter(TimeSpan.FromSeconds(_state.Config.Limits.CompositeSec + 30));

        try
        {
            var result = PageOf(await body(cts.Token));
            sw.Stop();
            _state.Log.RpcResponse(method, result.Data, error: null, sw.Elapsed.TotalMilliseconds);
            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            return Fail(method, ex, sw.Elapsed.TotalMilliseconds);
        }
    }

    // ---------------------------------------------------------------- plumbing

    /// <summary>
    /// 記録・計測・エラー変換をまとめる。ハンドラ本体は成功時の値を返すことだけに集中する。
    /// </summary>
    private RpcResult Handle(string method, object? parameters, Func<object?> body)
    {
        _state.Log.RpcRequest(method, parameters);
        var sw = Stopwatch.StartNew();

        try
        {
            var data = body();
            sw.Stop();
            _state.Log.RpcResponse(method, data, error: null, sw.Elapsed.TotalMilliseconds);
            return RpcResult.Success(data);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return Fail(method, ex, sw.Elapsed.TotalMilliseconds);
        }
    }

    private async Task<RpcResult> HandleAsync(string method, object? parameters, Func<CancellationToken, Task<object?>> body)
    {
        _state.Log.RpcRequest(method, parameters);
        var sw = Stopwatch.StartNew();

        // RPC 全体のタイムアウト（design.md §15.2）。有限時間で必ず返す
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_state.ShutdownToken);
        cts.CancelAfter(TimeSpan.FromSeconds(_state.Config.Limits.CompositeSec + 30));

        try
        {
            var data = await body(cts.Token);
            sw.Stop();
            _state.Log.RpcResponse(method, data, error: null, sw.Elapsed.TotalMilliseconds);
            return RpcResult.Success(data);
        }
        catch (OperationCanceledException ex)
        {
            sw.Stop();
            return Fail(method, new BackendException(
                ErrorCodes.Timeout,
                $"{method} が時間内に完了しませんでした。",
                "Visual Studio が応答しているか確認し、必要なら stakeout daemon restart してください。",
                ex), sw.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return Fail(method, ex, sw.Elapsed.TotalMilliseconds);
        }
    }

    private Task<RpcResult> HandleAsync<T>(string method, object? parameters, Func<CancellationToken, Task<T>> body) =>
        HandleAsync(method, parameters, async ct => (object?)await body(ct));

    private RpcResult Fail(string method, Exception ex, double durationMs)
    {
        if (ex is BackendException backend)
        {
            _state.Log.RpcResponse(method, null, $"{backend.Code}: {backend.Message}", durationMs);
            return RpcResult.Failure(backend.ToError());
        }

        _state.Log.RpcResponse(method, null, $"{ex.GetType().Name}: {ex.Message}", durationMs);
        return RpcResult.Failure(
            ErrorCodes.Internal,
            $"{ex.GetType().Name}: {ex.Message}",
            $"デーモンのログを確認してください: {_state.Log.Path}");
    }
}

/// <summary>引数を取らないメソッド用の空の要求。タイムアウトだけ渡せる。</summary>
public sealed record RpcRequestBase : RpcRequest;

/// <param name="Removed">削除したブレークポイントの数。</param>
public sealed record BreakpointClearResult(int Removed);

/// <param name="ThreadId">実際に操作したスレッド。タスク名で指定された場合はその解決結果。</param>
public sealed record ThreadActionResult(int ThreadId, bool Frozen);
