namespace Stakeout.Rpc;

/// <summary>確認項目の判定。</summary>
public enum DiagnosisStatus
{
    /// <summary>答えが出た。</summary>
    Answered,

    /// <summary>答えは出たが、注意が要る。</summary>
    Caution,

    /// <summary>この道具では判定できない。人が見る必要がある。</summary>
    Unknown,

    /// <summary>調べるのに条件が足りない（アタッチしていない等）。</summary>
    Skipped,
}

/// <summary>
/// 実装前に確認すべき項目 1 件（design.md §3.2 の Q1〜Q8）。
/// </summary>
/// <param name="Id">"Q1" など。design.md §3.2 の番号に対応する。</param>
/// <param name="Question">確認したいこと。</param>
/// <param name="Answer">分かったこと。**分からないなら分からないと書く。**</param>
/// <param name="Detail">その根拠。何を見てそう言えるのか。</param>
/// <param name="NextStep">状況によって、次に何をすべきか。無ければ null。</param>
public sealed record Diagnosis(
    string Id,
    string Question,
    DiagnosisStatus Status,
    string Answer,
    string? Detail,
    string? NextStep);

/// <param name="TargetPid">調べた Target。アタッチしていなければ null。</param>
/// <param name="Findings">確認項目。design.md §3.2 の順。</param>
public sealed record DoctorReport(
    int? TargetPid,
    string? TargetName,
    IReadOnlyList<Diagnosis> Findings);

public sealed record DoctorRequest : RpcRequest;
