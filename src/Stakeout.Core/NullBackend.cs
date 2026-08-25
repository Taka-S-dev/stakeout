using System.Runtime.CompilerServices;
using Stakeout.Rpc;

namespace Stakeout.Core;

/// <summary>
/// 何にも繋がない Backend（design.md §20 Phase 0）。
///
/// Phase 0 でデーモンと CLI の往復を組み立てるための土台であり、
/// 単体テストで Composite 層を検証するための土台でもある。
/// すべての操作は <c>NOT_ATTACHED</c> で失敗する。**黙って成功を返さない。**
/// 空実装が成功を装うと、上位層のバグが検出されずに通ってしまう。
/// </summary>
public sealed class NullBackend : IDebuggerBackend
{
    public string Name => "null";

    public BackendCapabilities Capabilities { get; } = new()
    {
        DataBreakpoint = false,
        Tracepoint = false,
        ReadMemory = false,
        Dump = false,
        Ttd = false,
        ParallelSessions = false,
        TracepointHitsPerSecond = 0,
        DataBreakpointSlots = 0,
    };

    public Task<SessionInfo> AttachAsync(int pid, AttachOptions options, CancellationToken ct) =>
        throw NotConfigured();

    public Task<SessionInfo> LaunchAsync(LaunchOptions options, CancellationToken ct) =>
        throw NotConfigured();

    public Task DetachAsync(CancellationToken ct) => Task.CompletedTask;

    public Task ContinueAsync(CancellationToken ct) => throw BackendException.NotAttached();

    public Task PauseAsync(CancellationToken ct) => throw BackendException.NotAttached();

    public Task StepAsync(StepKind kind, int threadId, CancellationToken ct) =>
        throw BackendException.NotAttached();

    public Task<StopEvent?> WaitForStopAsync(TimeSpan timeout, CancellationToken ct) =>
        throw BackendException.NotAttached();

    public Task<IReadOnlyList<ThreadInfo>> GetThreadsAsync(CancellationToken ct) =>
        throw BackendException.NotAttached();

    public Task SetCurrentThreadAsync(int threadId, CancellationToken ct) =>
        throw BackendException.NotAttached();

    public Task FreezeThreadAsync(int threadId, bool freeze, CancellationToken ct) =>
        throw BackendException.NotAttached();

    public Task<IReadOnlyList<StackFrame>> GetStackAsync(int threadId, int maxDepth, CancellationToken ct) =>
        throw BackendException.NotAttached();

    public Task<IReadOnlyList<Variable>> GetScopeAsync(int frameId, ScopeKind kind, CancellationToken ct) =>
        throw BackendException.NotAttached();

    public Task<Variable> EvaluateAsync(string expr, int? frameId, EvalOptions options, CancellationToken ct) =>
        throw BackendException.NotAttached();

    public Task<IReadOnlyList<Variable>> ExpandAsync(int variablesReference, CancellationToken ct) =>
        throw BackendException.NotAttached();

    public Task<byte[]> ReadMemoryAsync(ulong address, int length, CancellationToken ct) =>
        throw BackendException.NotAttached();

    public Task<Rpc.Breakpoint> SetBreakpointAsync(BreakpointRequest request, CancellationToken ct) =>
        throw BackendException.NotAttached();

    public Task RemoveBreakpointAsync(int breakpointId, CancellationToken ct) =>
        throw BackendException.NotAttached();

    public Task<IReadOnlyList<Rpc.Breakpoint>> ListBreakpointsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Rpc.Breakpoint>>(Array.Empty<Rpc.Breakpoint>());

    public Task SetExceptionBreakAsync(string[] codes, bool breakWhenThrown, CancellationToken ct) =>
        throw BackendException.NotAttached();

#pragma warning disable CS1998 // 非同期メソッドに await が無い。空の列挙なので不要
    public async IAsyncEnumerable<Rpc.TraceEvent> DrainTraceEventsAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        yield break;
    }
#pragma warning restore CS1998

    public Task<IReadOnlyList<ModuleInfo>> GetModulesAsync(CancellationToken ct) =>
        throw BackendException.NotAttached();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static BackendException NotConfigured() => new(
        ErrorCodes.NotConfigured,
        "Backend が設定されていません（null backend で動作しています）。",
        "stakeout.json の backend に envdte を設定してください。EnvDTE Backend は Phase 1 で実装します。");
}
