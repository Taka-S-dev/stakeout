using Stakeout.Rpc;

namespace Stakeout.Core;

/// <param name="Engines">
/// 明示するデバッグエンジン名。null なら Backend に任せる。
/// 指定した場合、その指定で繋げなければ <c>UNSUPPORTED</c> で失敗させる（ADR 0005）。
/// </param>
public sealed record AttachOptions(string[]? Engines = null);

public sealed record LaunchOptions(string Exe, string[]? Args = null, string? Cwd = null, string[]? Engines = null);

public sealed record EvalOptions(string? Format = null, int TimeoutMs = 5000);

/// <param name="ExpectedAddress">
/// データブレークポイントが監視するはずのアドレス（&amp;式 の評価結果）。
/// 作成後にこれと照合し、違えば消して失敗させる（ADR 0006 の罠 3）。
/// <c>Condition</c> に入れない。利用者の条件式と取り違える。
/// </param>
public sealed record BreakpointRequest(
    BreakpointKind Kind,
    string Location,
    string? Condition = null,
    int? HitCount = null,
    string[]? TraceExpressions = null,
    bool TraceStack = false,
    int DataSize = 4,
    string? ExpectedAddress = null);

/// <summary>
/// デバッガエンジンへの接続（design.md §6）。語彙は DAP から借りる。
/// 実装は COM を隠す。<c>Stakeout.Core</c> は COM を知らない（design.md §21）。
///
/// 実行制御は「要求を出したら即返る」。停止を待つのは
/// <see cref="WaitForStopAsync"/> だけである。
/// </summary>
public interface IDebuggerBackend : IAsyncDisposable
{
    /// <summary>"envdte" | "dbgeng" | "null"。</summary>
    string Name { get; }

    /// <summary>この Backend ができること。Composite 層が動作を切り替えるのに使う。</summary>
    BackendCapabilities Capabilities { get; }

    Task<SessionInfo> AttachAsync(int pid, AttachOptions options, CancellationToken ct);

    Task<SessionInfo> LaunchAsync(LaunchOptions options, CancellationToken ct);

    Task DetachAsync(CancellationToken ct);

    /// <summary>実行を再開する。既に実行中なら何もしない（冪等。ADR 0008）。</summary>
    Task ContinueAsync(CancellationToken ct);

    /// <summary>中断する。既に中断中なら何もしない（冪等。ADR 0008）。</summary>
    Task PauseAsync(CancellationToken ct);

    Task StepAsync(StepKind kind, int threadId, CancellationToken ct);

    /// <summary>停止を待つ。タイムアウトしたら null を返す。例外にはしない。</summary>
    Task<StopEvent?> WaitForStopAsync(TimeSpan timeout, CancellationToken ct);

    Task<IReadOnlyList<ThreadInfo>> GetThreadsAsync(CancellationToken ct);

    Task SetCurrentThreadAsync(int threadId, CancellationToken ct);

    Task FreezeThreadAsync(int threadId, bool freeze, CancellationToken ct);

    Task<IReadOnlyList<StackFrame>> GetStackAsync(int threadId, int maxDepth, CancellationToken ct);

    Task<IReadOnlyList<Variable>> GetScopeAsync(int frameId, ScopeKind kind, CancellationToken ct);

    Task<Variable> EvaluateAsync(string expr, int? frameId, EvalOptions options, CancellationToken ct);

    Task<IReadOnlyList<Variable>> ExpandAsync(int variablesReference, CancellationToken ct);

    Task<byte[]> ReadMemoryAsync(ulong address, int length, CancellationToken ct);

    Task<Rpc.Breakpoint> SetBreakpointAsync(BreakpointRequest request, CancellationToken ct);

    Task RemoveBreakpointAsync(int breakpointId, CancellationToken ct);

    Task<IReadOnlyList<Rpc.Breakpoint>> ListBreakpointsAsync(CancellationToken ct);

    Task SetExceptionBreakAsync(string[] codes, bool breakWhenThrown, CancellationToken ct);

    /// <summary>トレースポイントの出力を引き取る（design.md §10.6 / §11.4）。</summary>
    IAsyncEnumerable<Rpc.TraceEvent> DrainTraceEventsAsync(CancellationToken ct);

    Task<IReadOnlyList<ModuleInfo>> GetModulesAsync(CancellationToken ct);
}

/// <summary>
/// Backend 境界で投げる例外（design.md §21）。
/// COM の HRESULT はここまでで <see cref="ErrorCodes"/> に翻訳する。
/// </summary>
public sealed class BackendException : Exception
{
    public BackendException(string code, string message, string hint, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
        Hint = hint;
    }

    /// <summary><see cref="ErrorCodes"/> のいずれか。</summary>
    public string Code { get; }

    /// <summary>次に何をすればよいか。空にしない（design.md §15.1）。</summary>
    public string Hint { get; }

    public StakeoutError ToError() => new(Code, Message, Hint);

    public static BackendException NotAttached() => new(
        ErrorCodes.NotAttached,
        "デバッグセッションがありません。",
        "stakeout attach --name <exe> を実行してください。");

    public static BackendException Unsupported(string what, string hint) => new(
        ErrorCodes.Unsupported,
        $"この Backend は {what} に対応していません。",
        hint);
}
