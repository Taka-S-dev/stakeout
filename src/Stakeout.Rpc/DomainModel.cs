namespace Stakeout.Rpc;

/// <summary>
/// ドメインモデル（design.md §5）。語彙は Debug Adapter Protocol から借りる。
/// これらはそのままワイヤ表現になるので、名前を変えると CLI の出力互換が壊れる。
/// </summary>
public enum SessionState
{
    /// <summary>アタッチ済みで Target が実行中。</summary>
    AttachedRunning,

    /// <summary>アタッチ済みで Target が停止中。読み取り系はこの状態でのみ有効。</summary>
    AttachedStopped,

    /// <summary>未アタッチ。</summary>
    Detached,
}

public enum StepKind
{
    Over,
    Into,
    Out,
}

public enum ScopeKind
{
    Locals,
    Arguments,
}

public enum BreakpointKind
{
    Line,
    Function,
    Address,
    Data,
}

public enum StopReason
{
    Breakpoint,
    DataBreakpoint,
    Exception,
    Step,
    Pause,
    EntryPoint,
    Exit,
    Unknown,
}

public sealed record SessionInfo(
    string SessionId,
    string Backend,
    int Pid,
    string ProcessName,
    string Bitness,
    SessionState State);

public sealed record ThreadInfo(
    int ThreadId,
    string Name,
    string? TaskName,
    bool IsFrozen,
    bool IsCurrent,
    string TopFunction);

/// <param name="IsExternal">VS の <c>[External Code]</c> 相当。C# フレームなど。</param>
public sealed record StackFrame(
    int FrameId,
    int Depth,
    string Function,
    string? File,
    int? Line,
    ulong Address,
    string Module,
    bool IsExternal);

/// <param name="VariablesReference">0 なら子を持たない。</param>
public sealed record Variable(
    string Name,
    string Type,
    string Value,
    ulong? Address,
    int VariablesReference,
    bool IsPointer,
    bool IsValid);

/// <param name="BreakWhenHit">false ならトレースポイント。</param>
/// <summary>読み取ったメモリ（design.md §10.2 の <c>mem.read</c> の結果）。</summary>
/// <param name="Address">実際に読んだ先頭アドレス。式を渡した場合はその解決結果。</param>
/// <param name="Requested">要求されたバイト数。</param>
/// <param name="Length">
/// 実際に読めたバイト数。<paramref name="Requested"/> より小さければ、
/// **そこから先は読めなかった**（未マップ等）。0 で埋めた結果ではない。
/// </param>
/// <param name="Hex">1 バイト 2 桁を空白区切りにしたもの。</param>
/// <param name="Ascii">印字できない文字を <c>.</c> にしたもの。</param>
public sealed record MemoryBlock(
    ulong Address,
    int Requested,
    int Length,
    string Hex,
    string Ascii);

public sealed record Breakpoint(
    int BreakpointId,
    BreakpointKind Kind,
    string Location,
    string? Condition,
    int? HitCount,
    bool BreakWhenHit,
    string[]? TraceExpressions,
    bool Verified,
    string? VerifyMessage);

public sealed record StopEvent(
    string SessionId,
    StopReason Reason,
    int ThreadId,
    int? BreakpointId,
    string? ExceptionCode,
    string? Description,
    DateTimeOffset At);

public sealed record TraceEvent(
    long EventId,
    string RunId,
    long TsQpc,
    DateTimeOffset TsWall,
    int ThreadId,
    string? TaskName,
    string Kind,
    string Location,
    string Function,
    string? Expr,
    string ValueJson,
    string? StackJson);

public sealed record ModuleInfo(
    string Name,
    string Path,
    ulong BaseAddress,
    bool SymbolsLoaded);

/// <summary>
/// Backend ができることの申告（design.md §6, §10.8）。
/// Composite 層はこれを見て動作を切り替える。
/// </summary>
public sealed record BackendCapabilities
{
    public required bool DataBreakpoint { get; init; }

    public required bool Tracepoint { get; init; }

    public required bool ReadMemory { get; init; }

    public required bool Dump { get; init; }

    public required bool Ttd { get; init; }

    public required bool ParallelSessions { get; init; }

    /// <summary>
    /// トレースポイントの実測スループット。0 なら未計測。
    /// Composite はこの値から所要時間を見積もり、タイムアウトに収まらない要求を
    /// 実行前に断る（ADR 0007）。
    /// </summary>
    public double TracepointHitsPerSecond { get; init; }

    /// <summary>ハードウェアデータブレークポイントの本数。x64 では 4（ADR 0006）。</summary>
    public int DataBreakpointSlots { get; init; }
}
