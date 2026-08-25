namespace Stakeout.Rpc;

/// <summary>Code Index の検索結果の種類。</summary>
public enum CodeMatchKind
{
    /// <summary>gtags の定義索引から。</summary>
    Definition,

    /// <summary>gtags の参照索引から。</summary>
    Reference,

    /// <summary>
    /// 索引済みファイルへのテキスト検索から。
    /// 参照索引が空だったときの代替であり、精度は落ちる（ADR 0017）。
    /// </summary>
    TextSearch,
}

/// <param name="File">gtags のルートからの相対パス。</param>
public sealed record CodeLocation(string File, int Line, string Text, string Symbol, CodeMatchKind Kind);

/// <param name="Function">その行を囲む関数。分からなければ null。</param>
/// <param name="Confidence">"high" か "low"。**これは候補であって断定ではない。**</param>
/// <param name="Reason">なぜ書き込みと判定したか。</param>
public sealed record WriteSite(
    string File,
    int Line,
    string Text,
    string? Function,
    string Confidence,
    string Reason);

public sealed record CodeQueryRequest : RpcRequest
{
    public string Symbol { get; init; } = string.Empty;
}

public sealed record FindCorruptionRequest : RpcRequest
{
    /// <summary>監視する式。モジュール修飾を付けてよい。</summary>
    public string Symbol { get; init; } = string.Empty;

    /// <summary>何回書き込みを捕まえるか。</summary>
    public int MaxHits { get; init; } = 10;
}

/// <param name="Expected">静的に見つけた書き込み候補に含まれていたか。</param>
public sealed record CorruptionHit(
    int ThreadId,
    string? TaskName,
    string Function,
    string? File,
    int? Line,
    string Before,
    string After,
    bool Expected,
    string Note);

/// <param name="StaticWriters">gtags から見つけた書き込み候補。</param>
/// <param name="Hits">実際に捕まえた書き込み。</param>
/// <param name="IndexAvailable">Code Index が使えたか。使えないと Expected の判定ができない。</param>
public sealed record FindCorruptionResult(
    string Symbol,
    string Address,
    int Size,
    bool IndexAvailable,
    IReadOnlyList<WriteSite> StaticWriters,
    IReadOnlyList<CorruptionHit> Hits,
    string? Warning,
    IReadOnlyList<CompositeStep> Steps);
