namespace Stakeout.Rpc;

/// <summary>
/// RPC の引数（design.md §7.1）。
/// すべて任意項目にして既定値を持たせる。CLI が省いた引数でデーモンが落ちないようにする。
/// </summary>
/// <param name="TimeoutMs">この操作に許す時間。省略時はデーモンの既定値。</param>
public abstract record RpcRequest
{
    public int? TimeoutMs { get; init; }
}

public sealed record AttachRequest : RpcRequest
{
    public int? Pid { get; init; }

    /// <summary>プロセス名の正規表現。<see cref="Pid"/> の代わりに使える。</summary>
    public string? Name { get; init; }

    public string? Backend { get; init; }

    /// <summary>デバッグエンジンの明示。指定して繋げなければ失敗させる（ADR 0005）。</summary>
    public string[]? Engines { get; init; }

    /// <summary>使う Visual Studio の pid（ADR 0002）。</summary>
    public int? VsPid { get; init; }
}

public sealed record StepRequest : RpcRequest
{
    public StepKind Kind { get; init; } = StepKind.Over;

    public int? ThreadId { get; init; }
}

public sealed record WaitRequest : RpcRequest;

public sealed record ThreadSelectRequest : RpcRequest
{
    public int ThreadId { get; init; }
}

public sealed record ThreadFreezeRequest : RpcRequest
{
    public int ThreadId { get; init; }

    public bool Freeze { get; init; } = true;
}

public sealed record StackRequest : RpcRequest
{
    public int? ThreadId { get; init; }

    /// <summary>全スレッドのスタックを取る。</summary>
    public bool All { get; init; }

    /// <summary>取得する深さ。省略時は all なら 5、単一スレッドなら 30（ADR 0008）。</summary>
    public int? MaxDepth { get; init; }
}

public sealed record ScopeRequest : RpcRequest
{
    public int FrameId { get; init; }

    public ScopeKind Kind { get; init; } = ScopeKind.Locals;
}

public sealed record EvalRequest : RpcRequest
{
    public string Expr { get; init; } = string.Empty;

    public int? FrameId { get; init; }

    /// <summary>VS の書式指定子（x, d, s など。design.md §10.7）。</summary>
    public string? Format { get; init; }
}

public sealed record ExpandRequest : RpcRequest
{
    public int Ref { get; init; }
}

/// <summary>
/// メモリの読み取り（design.md §10.2 の <c>mem.read</c>）。
/// </summary>
public sealed record MemReadRequest : RpcRequest
{
    /// <summary>
    /// 読む先。<c>0x7ff6a2c31040</c> のようなアドレス、または
    /// アドレスに評価される式（<c>&amp;g_ctx</c>、<c>p-&gt;buffer</c> など）。
    /// **式を渡せるようにしてある。** 生アドレスを得るためだけに
    /// eval を 1 往復させるのは、調査の手数を無駄に増やす。
    /// </summary>
    public string Address { get; init; } = string.Empty;

    /// <summary>読むバイト数。上限は limits.maxMemoryReadBytes。</summary>
    public int Length { get; init; } = 64;

    /// <summary>式として解釈する場合の評価フレーム。</summary>
    public int? FrameId { get; init; }
}

public sealed record BreakpointSetRequest : RpcRequest
{
    public BreakpointKind Kind { get; init; } = BreakpointKind.Line;

    /// <summary>FILE:LINE、関数名、アドレス、またはデータ式。</summary>
    public string Location { get; init; } = string.Empty;

    public string? Condition { get; init; }

    public int? HitCount { get; init; }

    /// <summary>データブレークポイントで監視するバイト数。</summary>
    public int DataSize { get; init; } = 4;
}

public sealed record BreakpointRemoveRequest : RpcRequest
{
    public int Id { get; init; }
}

public sealed record ExceptionBreakRequest : RpcRequest
{
    public string[] Codes { get; init; } = Array.Empty<string>();

    public bool BreakWhenThrown { get; init; } = true;
}

/// <param name="Stopped">期限内に停止したか。</param>
/// <param name="Stop">停止していればその内容。</param>
public sealed record WaitResult(bool Stopped, StopEvent? Stop);

/// <param name="ThreadId">このスタックのスレッド。</param>
/// <param name="Truncated">深さ上限で打ち切ったか。</param>
public sealed record ThreadStack(int ThreadId, string ThreadName, IReadOnlyList<StackFrame> Frames, bool Truncated);

public sealed record LogTailRequest : RpcRequest
{
    /// <summary>末尾から何行読むか。</summary>
    public int Count { get; init; } = 50;

    /// <summary>この種別だけに絞る（rpc.request / rpc.response / stop / trace / daemon）。</summary>
    public string? Kind { get; init; }
}

public sealed record CursorRequest : RpcRequest
{
    public string Cursor { get; init; } = string.Empty;
}

/// <param name="Pid">プロセス ID。</param>
/// <param name="Allowed">allowlist に一致してアタッチできるか。</param>
public sealed record TargetInfo(int Pid, string Name, bool Allowed);
