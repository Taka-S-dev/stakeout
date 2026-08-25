namespace Stakeout.Rpc;

/// <summary>
/// Composite（複合コマンド）の引数と結果（design.md §9）。
///
/// すべての Composite は <c>Steps</c> に「何をしたか」を残す。
/// エージェントが結果を検証できないと、複合コマンドは
/// 「よく分からないが何かを返す箱」になってしまう。
/// </summary>
public sealed record CompositeStep(string Action, string Detail, DateTimeOffset At);

public sealed record RunUntilRequest : RpcRequest
{
    /// <summary>FILE:LINE または関数名。</summary>
    public string Location { get; init; } = string.Empty;

    public string? Condition { get; init; }

    /// <summary>停止したときにスタックを取るか。</summary>
    public bool CaptureStack { get; init; } = true;

    /// <summary>停止したときにローカル変数を取るか。</summary>
    public bool CaptureLocals { get; init; }

    /// <summary>停止したときに評価する式。</summary>
    public string[]? Exprs { get; init; }

    /// <summary>取得するスタックの深さ。</summary>
    public int MaxDepth { get; init; } = 20;

    /// <summary>一時ブレークポイントを残すか。既定では消す。</summary>
    public bool KeepBreakpoint { get; init; }
}

/// <param name="ReachedTarget">狙った位置で止まったか。別の理由で止まった場合は false。</param>
/// <param name="Partial">タイムアウトで打ち切ったか。</param>
public sealed record RunUntilResult(
    bool ReachedTarget,
    bool Partial,
    StopEvent? Stop,
    ThreadInfo? Thread,
    IReadOnlyList<StackFrame>? Stack,
    IReadOnlyList<Variable>? Locals,
    IReadOnlyDictionary<string, string>? Exprs,
    IReadOnlyList<CompositeStep> Steps);

public sealed record WatchUntilChangeRequest : RpcRequest
{
    /// <summary>監視する式。アドレスとサイズはここから求める。</summary>
    public string Expr { get; init; } = string.Empty;

    /// <summary>何回書き込みを見たら諦めるか。</summary>
    public int MaxHits { get; init; } = 20;

    /// <summary>各ヒットで取るスタックの深さ。</summary>
    public int StackDepth { get; init; } = 8;
}

/// <param name="Before">書き込み前の値。</param>
/// <param name="After">書き込み後の値。</param>
/// <param name="TaskName">書き込んだスレッドのタスク名。分からなければ null。</param>
public sealed record WatchHit(
    int ThreadId,
    string? TaskName,
    string Function,
    string? File,
    int? Line,
    string Before,
    string After,
    IReadOnlyList<StackFrame> Stack);

/// <param name="Changed">値が実際に変わったか。</param>
/// <param name="Warning">監視範囲を丸めた等の注意。無ければ null。</param>
public sealed record WatchUntilChangeResult(
    bool Changed,
    string Expr,
    string Address,
    int Size,
    string FinalValue,
    IReadOnlyList<WatchHit> Hits,
    string? Warning,
    IReadOnlyList<CompositeStep> Steps);

public sealed record TraceExprRequest : RpcRequest
{
    public string[] Exprs { get; init; } = Array.Empty<string>();

    /// <summary>ステップ回数。1 ステップは break/go 相当 33 ms + 式評価（ADR 0008）。</summary>
    public int Steps { get; init; } = 50;

    public StepKind Kind { get; init; } = StepKind.Over;
}

public sealed record TraceExprPoint(
    int Step,
    string Function,
    string? File,
    int? Line,
    IReadOnlyDictionary<string, string> Values);

public sealed record TraceExprResult(
    IReadOnlyList<TraceExprPoint> Points,
    bool Partial,
    IReadOnlyList<CompositeStep> Steps);

public sealed record DumpRequest : RpcRequest
{
    public string Expr { get; init; } = string.Empty;

    public int? FrameId { get; init; }

    /// <summary>何段まで展開するか。</summary>
    public int Depth { get; init; } = 2;

    /// <summary>1 段あたりに展開する要素数の上限。</summary>
    public int MaxItems { get; init; } = 50;
}

/// <summary>
/// 展開した 1 要素（design.md §8.5 / ADR 0014）。
///
/// 木ではなく**平らな列**にする。理由は 2 つある。
/// - 大きい結果をカーソルで分割できる。木は途中で切れない
/// - <c>path</c> で絞り込める。エージェントは「g_ctx.inner.flags が知りたい」であって
///   「3 段目の 2 番目の子」ではない
/// 木として表示したければ <c>depth</c> で字下げすればよい。
/// </summary>
/// <param name="Path">"g_ctx.inner.flags" のような完全な経路。</param>
/// <param name="Depth">根を 0 とした段数。</param>
/// <param name="Note">展開を打ち切った理由など。無ければ null。</param>
public sealed record DumpNode(
    string Path,
    int Depth,
    string Name,
    string Type,
    string Value,
    bool IsPointer,
    bool IsValid,
    string? Note);

/// <param name="Evaluations">この dump に使った式評価の回数。1 回およそ 6 ms（ADR 0008）。</param>
public sealed record DumpResult(
    string Expr,
    IReadOnlyList<DumpNode> Nodes,
    int Evaluations,
    bool BudgetExhausted,
    IReadOnlyList<CompositeStep> Steps);

/// <param name="EntryFunction">スタック最下段付近のユーザーコード関数。</param>
public sealed record TaskInfo(
    int ThreadId,
    string Name,
    string? TaskName,
    string EntryFunction,
    string TopFunction,
    bool IsFrozen,
    bool IsCurrent);

public sealed record TaskMapResult(IReadOnlyList<TaskInfo> Tasks, IReadOnlyList<CompositeStep> Steps);

/// <summary>スレッドをタスク名でも指定できるようにする（design.md §9.6）。</summary>
public sealed record ThreadTargetRequest : RpcRequest
{
    public int? ThreadId { get; init; }

    public string? TaskName { get; init; }

    public bool Freeze { get; init; } = true;
}
