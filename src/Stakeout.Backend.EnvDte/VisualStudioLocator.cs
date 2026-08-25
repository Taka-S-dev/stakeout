using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text.RegularExpressions;
using Stakeout.Core;
using Stakeout.Rpc;

namespace Stakeout.Backend.EnvDte;

/// <param name="ProgId">"VisualStudio.DTE.17.0" など。</param>
/// <param name="Version">"17.0" など。</param>
/// <param name="Pid">devenv.exe のプロセス ID。</param>
public sealed record VisualStudioCandidate(string MonikerName, string ProgId, string Version, int Pid)
{
    /// <summary>ROT から取り出した DTE オブジェクト。COM なのでこのプロジェクトの外に出さない。</summary>
    internal object? Dte { get; init; }
}

/// <summary>
/// 起動中の Visual Studio を Running Object Table から探す（design.md §10.1 / ADR 0002）。
///
/// ProgID を固定しない。VS の版が上がるたびに設定を書き換えるのは、
/// エージェントに使わせる道具として不必要な失敗要因になる。
/// </summary>
internal static partial class VisualStudioLocator
{
    [GeneratedRegex(@"^!(VisualStudio\.DTE\.(\d+\.\d+)):(\d+)$")]
    private static partial Regex MonikerPattern();

    [DllImport("ole32.dll", PreserveSig = false)]
    private static extern void GetRunningObjectTable(int reserved, out IRunningObjectTable prot);

    [DllImport("ole32.dll", PreserveSig = false)]
    private static extern void CreateBindCtx(int reserved, out IBindCtx ppbc);

    /// <summary>ROT に登録されている Visual Studio をすべて返す。</summary>
    public static IReadOnlyList<VisualStudioCandidate> Enumerate()
    {
        var found = new List<VisualStudioCandidate>();

        GetRunningObjectTable(0, out var table);
        CreateBindCtx(0, out var bindCtx);
        table.EnumRunning(out var monikers);
        monikers.Reset();

        var buffer = new IMoniker[1];
        while (monikers.Next(1, buffer, IntPtr.Zero) == 0)
        {
            var moniker = buffer[0];
            try
            {
                moniker.GetDisplayName(bindCtx, null, out var displayName);
                var match = MonikerPattern().Match(displayName);
                if (!match.Success)
                {
                    continue;
                }

                // ROT には死んだ登録が残ることがある。取り出せなければ黙って捨てる
                object? dte;
                try
                {
                    table.GetObject(moniker, out dte);
                }
                catch (COMException)
                {
                    continue;
                }

                if (dte is null)
                {
                    continue;
                }

                found.Add(new VisualStudioCandidate(
                    displayName,
                    match.Groups[1].Value,
                    match.Groups[2].Value,
                    int.Parse(match.Groups[3].Value))
                {
                    Dte = dte,
                });
            }
            finally
            {
                Marshal.ReleaseComObject(moniker);
            }
        }

        return found;
    }

    /// <summary>
    /// 使う Visual Studio を 1 つに決める。ROT を走査してから
    /// <see cref="VisualStudioSelection.Select"/> の規則を当てる。
    /// </summary>
    public static VisualStudioCandidate Select(EnvDteConfig config, int? targetPid, Func<object, int[]> debuggedPids) =>
        VisualStudioSelection.Select(Enumerate(), config, targetPid, debuggedPids);
}

/// <summary>
/// どの Visual Studio を使うかの規則（ADR 0002）。
/// ROT 走査から切り離してあるので、COM 無しで単体テストできる。
/// </summary>
internal static class VisualStudioSelection
{
    /// <summary>
    /// 使う Visual Studio を 1 つに決める（ADR 0002）。
    ///
    /// 曖昧なまま繋がない。間違った VS に繋いだまま調査が進むと、
    /// エージェントは原因不明の矛盾した結果を延々と追うことになる。
    /// 警告して続けるより、失敗させたほうが安い。
    /// </summary>
    /// <param name="candidates">ROT に居た Visual Studio。</param>
    /// <param name="targetPid">アタッチしたい Target。これをデバッグ中の VS を優先する。</param>
    /// <param name="debuggedPids">DTE から、その VS がデバッグ中の pid を引く。</param>
    public static VisualStudioCandidate Select(
        IReadOnlyList<VisualStudioCandidate> candidates,
        EnvDteConfig config,
        int? targetPid,
        Func<object, int[]> debuggedPids)
    {
        if (candidates.Count == 0)
        {
            throw new BackendException(
                ErrorCodes.NotFound,
                "起動中の Visual Studio が見つかりません。",
                "Visual Studio を起動してください。起動しているのに見つからない場合は、" +
                "stakeout と Visual Studio の昇格レベルが揃っているか確認してください。");
        }

        if (config.VsPid is { } pinnedPid)
        {
            return candidates.FirstOrDefault(c => c.Pid == pinnedPid)
                   ?? throw new BackendException(
                       ErrorCodes.NotFound,
                       $"設定 envdte.vsPid = {pinnedPid} に一致する Visual Studio がありません。",
                       $"起動中の候補: {Describe(candidates)}");
        }

        var remaining = candidates;

        if (config.ProgId is { Length: > 0 } progId)
        {
            remaining = candidates
                .Where(c => string.Equals(c.ProgId, progId, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (remaining.Count == 0)
            {
                throw new BackendException(
                    ErrorCodes.NotFound,
                    $"設定 envdte.progId = {progId} に一致する Visual Studio がありません。",
                    $"起動中の候補: {Describe(candidates)}");
            }
        }

        if (remaining.Count == 1)
        {
            return remaining[0];
        }

        // Target を既にデバッグしている VS があればそれを選ぶ
        if (targetPid is { } pid)
        {
            var attached = remaining
                .Where(c => c.Dte is not null && debuggedPids(c.Dte).Contains(pid))
                .ToList();

            if (attached.Count == 1)
            {
                return attached[0];
            }
        }

        throw new BackendException(
            ErrorCodes.Precondition,
            $"Visual Studio が {remaining.Count} 個起動しており、どれを使うか決められません。",
            $"--vs-pid で指定してください。候補: {Describe(remaining)}");
    }

    private static string Describe(IEnumerable<VisualStudioCandidate> candidates) =>
        string.Join(", ", candidates.Select(c => $"{c.ProgId} (pid {c.Pid})"));
}
