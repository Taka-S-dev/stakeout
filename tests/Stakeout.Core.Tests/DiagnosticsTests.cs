using Stakeout.Core;
using Stakeout.Rpc;

namespace Stakeout.Core.Tests;

/// <summary>
/// design.md §3.2 の確認項目に道具が答える部分（stakeout doctor）。
///
/// ここで一番大事なのは、**分からないものを分からないと言えること**である。
/// 分かったふりをした答えは、その前提の上に設計が積み上がるので、
/// 「答えが無い」より悪い。
/// </summary>
public sealed class ThreadDiagnosticsTests
{
    private static TaskInfo Task(string? taskName, string top, string name = "") =>
        new(ThreadId: 100, Name: name, TaskName: taskName, EntryFunction: "entry",
            TopFunction: top, IsFrozen: false, IsCurrent: false);

    [Fact]
    public void Q2_タスク名が付けば対応していると答える()
    {
        var result = ThreadDiagnostics.TaskMapping(new[] { Task("A", "f"), Task(null, "g") });

        Assert.Equal(DiagnosisStatus.Answered, result.Status);
        Assert.Contains("A", result.Detail);
    }

    [Fact]
    public void Q2_1_つも付かなければ設定の直し方を示す()
    {
        var result = ThreadDiagnostics.TaskMapping(new[] { Task(null, "f") });

        Assert.Equal(DiagnosisStatus.Caution, result.Status);
        Assert.Contains("taskEntryPatterns", result.NextStep);
    }

    [Fact]
    public void Q3_は常に判定できないと答える()
    {
        // 1 回の観測で「常に 1 本」は言えない。回数を増やしても言えない
        var result = ThreadDiagnostics.ConcurrentExecution(new[] { Task("A", "Sleep") });

        Assert.Equal(DiagnosisStatus.Unknown, result.Status);
        Assert.NotNull(result.NextStep);
    }

    [Fact]
    public void Q3_シンボルの無いスレッドを実行中に数えない()
    {
        // 生アドレスしか無いスレッドを「実行中」に数えると、実態より多く見える
        var result = ThreadDiagnostics.ConcurrentExecution(new[]
        {
            Task("A", "00007ffa70220a04"),
            Task("B", "00007ffa`70224034"),
            Task("C", "[アプリケーションの実行が一時停止しました]"),
        });

        Assert.Contains("判定不能 3", result.Detail);
        Assert.Contains("実行中 0", result.Detail);
    }

    [Theory]
    [InlineData("Sleep", ThreadDiagnostics.ThreadActivity.Waiting)]
    [InlineData("ntdll!NtWaitForSingleObject", ThreadDiagnostics.ThreadActivity.Waiting)]
    [InlineData("WaitForMultipleObjects", ThreadDiagnostics.ThreadActivity.Waiting)]
    [InlineData("nl_update_state(int)", ThreadDiagnostics.ThreadActivity.Running)]
    [InlineData("00007ffa70220a04", ThreadDiagnostics.ThreadActivity.Unknown)]
    [InlineData("", ThreadDiagnostics.ThreadActivity.Unknown)]
    [InlineData("[すべてのスレッド スタックが表示されます]", ThreadDiagnostics.ThreadActivity.Unknown)]
    public void スレッドの様子を分類する(string top, ThreadDiagnostics.ThreadActivity expected)
    {
        Assert.Equal(expected, ThreadDiagnostics.Classify(top));
    }

    [Fact]
    public void Q4_名前が無ければ代わりの手を示す()
    {
        var result = ThreadDiagnostics.ThreadNames(new[] { Task("A", "f") });

        Assert.Equal(DiagnosisStatus.Caution, result.Status);
        Assert.Contains("taskEntryPatterns", result.NextStep);
    }

    [Fact]
    public void スレッドが取れていなければ飛ばす()
    {
        var empty = Array.Empty<TaskInfo>();

        Assert.Equal(DiagnosisStatus.Skipped, ThreadDiagnostics.TaskMapping(empty).Status);
        Assert.Equal(DiagnosisStatus.Skipped, ThreadDiagnostics.ConcurrentExecution(empty).Status);
        Assert.Equal(DiagnosisStatus.Skipped, ThreadDiagnostics.ThreadNames(empty).Status);
    }
}

public sealed class EnvironmentDiagnosticsTests
{
    [Fact]
    public void Q1_単一プロセスなら単一と答える()
    {
        var result = EnvironmentDiagnostics.ProcessCount(new[] { new TargetInfo(1, "Host.exe", true) });

        Assert.Equal(DiagnosisStatus.Answered, result.Status);
    }

    [Fact]
    public void Q1_複数なら付録_C_の検討を促す()
    {
        var result = EnvironmentDiagnostics.ProcessCount(new[]
        {
            new TargetInfo(1, "Host.exe", true),
            new TargetInfo(2, "Host.exe", true),
        });

        Assert.Equal(DiagnosisStatus.Caution, result.Status);
        Assert.Contains("付録 C", result.NextStep);
    }

    [Fact]
    public void Q5_アタッチしていなければ飛ばす()
    {
        Assert.Equal(DiagnosisStatus.Skipped, EnvironmentDiagnostics.Bitness(null).Status);
    }

    [Fact]
    public void Q5_自分自身の_bitness_を判定できる()
    {
        // デバッガ自身の bitness で代用しないことの確認。実プロセスを見る
        var bitness = EnvironmentDiagnostics.TargetBitness(Environment.ProcessId);

        Assert.Equal(Environment.Is64BitProcess ? "x64" : "x86", bitness);
    }

    [Fact]
    public void Q7_Visual_Studio_に繋いでいなければ飛ばす()
    {
        Assert.Equal(DiagnosisStatus.Skipped, EnvironmentDiagnostics.Elevation(false, null).Status);
    }

    private const string SystemDbgEng = @"C:\Windows\System32\dbgeng.dll";

    [Fact]
    public void Q6_dbgeng_Backend_で_System32_版しか無ければ注意する()
    {
        var result = EnvironmentDiagnostics.DebuggingTools(new[] { SystemDbgEng }, SystemDbgEng, "dbgeng");

        Assert.Equal(DiagnosisStatus.Caution, result.Status);
    }

    [Fact]
    public void Q6_envdte_Backend_なら_System32_版の制約を注意に上げない()
    {
        var result = EnvironmentDiagnostics.DebuggingTools(new[] { SystemDbgEng }, SystemDbgEng, "envdte");

        Assert.Equal(DiagnosisStatus.Answered, result.Status);
        Assert.Contains("envdte", result.NextStep);
    }

    [Fact]
    public void Q6_Debugging_Tools_の版があれば注意しない()
    {
        var tools = @"C:\Program Files (x86)\Windows Kits\10\Debuggers\x64\dbgeng.dll";
        var result = EnvironmentDiagnostics.DebuggingTools(new[] { tools, SystemDbgEng }, SystemDbgEng, "dbgeng");

        Assert.Equal(DiagnosisStatus.Answered, result.Status);
    }

    [Theory]
    [InlineData("14.44.35207", "14.28", 1)]
    [InlineData("14.28.29910", "14.28", 0)]
    [InlineData("14.16.27023", "14.28", -1)]
    public void ツールセットの版を数値で比べる(string left, string right, int expected)
    {
        Assert.Equal(expected, Math.Sign(EnvironmentDiagnostics.CompareVersions(left, right)));
    }
}
