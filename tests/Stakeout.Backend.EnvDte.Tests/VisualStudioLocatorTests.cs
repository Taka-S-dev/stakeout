using Stakeout.Core;
using Stakeout.Rpc;

namespace Stakeout.Backend.EnvDte.Tests;

/// <summary>
/// Visual Studio の選び方（ADR 0002）。
///
/// ここで検証するのは「曖昧なまま繋がない」ことである。
/// 間違った VS に繋いだまま調査が進むと、エージェントは原因不明の矛盾した結果を追い続ける。
/// 警告して続けるより、失敗させたほうが安い。
/// </summary>
public sealed class VisualStudioSelectionTests
{
    private static VisualStudioCandidate Candidate(string version, int pid) =>
        new($"!VisualStudio.DTE.{version}:{pid}", $"VisualStudio.DTE.{version}", version, pid)
        {
            // COM オブジェクトは要らない。選択規則だけを見る
            Dte = new object(),
        };

    private static BackendException Select(
        IReadOnlyList<VisualStudioCandidate> candidates,
        EnvDteConfig config,
        int? targetPid = null,
        Func<object, int[]>? debuggedPids = null)
    {
        return Assert.Throws<BackendException>(() =>
            VisualStudioSelection.Select(candidates, config, targetPid, debuggedPids ?? (_ => Array.Empty<int>())));
    }

    [Fact]
    public void 起動していなければ見つからないと言う()
    {
        var ex = Select(Array.Empty<VisualStudioCandidate>(), new EnvDteConfig());

        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        Assert.Contains("昇格", ex.Hint);
    }

    [Fact]
    public void 候補が_1_つならそれを使う()
    {
        var only = Candidate("17.0", 100);

        var selected = VisualStudioSelection.Select(
            new[] { only }, new EnvDteConfig(), null, _ => Array.Empty<int>());

        Assert.Same(only, selected);
    }

    [Fact]
    public void 版数が違っても候補として扱う()
    {
        // ProgID を固定しない（ADR 0002）。VS の版が上がるたびに設定を書き換えさせない
        var vs2026 = Candidate("18.0", 100);

        var selected = VisualStudioSelection.Select(
            new[] { vs2026 }, new EnvDteConfig(), null, _ => Array.Empty<int>());

        Assert.Equal("18.0", selected.Version);
    }

    [Fact]
    public void 複数起動していて手がかりが無ければ失敗させる()
    {
        var ex = Select(
            new[] { Candidate("17.0", 100), Candidate("18.0", 200) },
            new EnvDteConfig());

        Assert.Equal(ErrorCodes.Precondition, ex.Code);
        Assert.Contains("--vs-pid", ex.Hint);
        Assert.Contains("100", ex.Hint);
        Assert.Contains("200", ex.Hint);
    }

    [Fact]
    public void 設定の_vsPid_が最優先される()
    {
        var selected = VisualStudioSelection.Select(
            new[] { Candidate("17.0", 100), Candidate("18.0", 200) },
            new EnvDteConfig { VsPid = 200 },
            targetPid: 999,
            _ => new[] { 999 });

        Assert.Equal(200, selected.Pid);
    }

    [Fact]
    public void 設定の_vsPid_が居なければ候補を挙げて失敗する()
    {
        var ex = Select(
            new[] { Candidate("17.0", 100) },
            new EnvDteConfig { VsPid = 999 });

        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        Assert.Contains("pid 100", ex.Hint);
    }

    [Fact]
    public void progId_で候補を絞れる()
    {
        var selected = VisualStudioSelection.Select(
            new[] { Candidate("17.0", 100), Candidate("18.0", 200) },
            new EnvDteConfig { ProgId = "VisualStudio.DTE.18.0" },
            null,
            _ => Array.Empty<int>());

        Assert.Equal(200, selected.Pid);
    }

    [Fact]
    public void Target_をデバッグ中の_VS_を優先する()
    {
        var debugging = Candidate("18.0", 200);

        var selected = VisualStudioSelection.Select(
            new[] { Candidate("17.0", 100), debugging },
            new EnvDteConfig(),
            targetPid: 4242,
            dte => ReferenceEquals(dte, debugging.Dte) ? new[] { 4242 } : Array.Empty<int>());

        Assert.Equal(200, selected.Pid);
    }

    [Fact]
    public void 複数の_VS_が同じ_Target_をデバッグしていたら失敗させる()
    {
        // 実際には起こらないはずだが、起きたときに黙って片方を選ばない
        var ex = Select(
            new[] { Candidate("17.0", 100), Candidate("18.0", 200) },
            new EnvDteConfig(),
            targetPid: 4242,
            _ => new[] { 4242 });

        Assert.Equal(ErrorCodes.Precondition, ex.Code);
    }
}
