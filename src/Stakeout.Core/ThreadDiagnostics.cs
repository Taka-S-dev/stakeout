using System.Text.RegularExpressions;
using Stakeout.Rpc;

namespace Stakeout.Core;

/// <summary>
/// スレッドの様子から、design.md §3.2 の Q2〜Q4 を判定する。
///
/// **判定できないことを「判定できない」と返すのが仕事の半分である。**
/// とくに Q3（同時に実行中のスレッドは常に 1 本か）は、停止中のスタックからは
/// 厳密には分からない。分かったふりをすると、その前提の上に設計が積み上がる。
///
/// プロセスにも COM にも触れないので単体テストできる。
/// </summary>
public static partial class ThreadDiagnostics
{
    /// <summary>待ちに入っている関数。ここに居るスレッドは実行していない。</summary>
    [GeneratedRegex(
        @"(?i)\b(Sleep|SleepEx|WaitForSingleObject(Ex)?|WaitForMultipleObjects(Ex)?|NtWaitFor\w+|NtDelayExecution|ZwDelayExecution|SignalObjectAndWait|GetQueuedCompletionStatus|GetMessage|MsgWaitFor\w+)\b")]
    private static partial Regex WaitFunction();

    /// <summary>Q2: タスクとスレッドが対応しているか。</summary>
    public static Diagnosis TaskMapping(IReadOnlyList<TaskInfo> tasks)
    {
        if (tasks.Count == 0)
        {
            return new Diagnosis("Q2", "Task はスレッドに対応しているか",
                DiagnosisStatus.Skipped, "スレッドが取れていません。",
                null, "停止中に stakeout doctor を実行してください。");
        }

        var named = tasks.Where(t => t.TaskName is { Length: > 0 }).ToArray();
        var detail = $"{tasks.Count} スレッド中 {named.Length} 個にタスク名が付きました" +
                     (named.Length > 0 ? $": {string.Join(", ", named.Select(t => t.TaskName))}" : string.Empty);

        if (named.Length == 0)
        {
            return new Diagnosis("Q2", "Task はスレッドに対応しているか",
                DiagnosisStatus.Caution, "タスク名が 1 つも付きませんでした。",
                detail,
                "stakeout.json の tasks.taskEntryPatterns を、この Target のエントリ関数名に合わせてください。" +
                "エントリ関数は stakeout task-map の entryFunction 列で確認できます。");
        }

        return new Diagnosis("Q2", "Task はスレッドに対応しているか",
            DiagnosisStatus.Answered,
            $"対応しています（{named.Length} タスク）。", detail, null);
    }

    /// <summary>
    /// Q3: 同時に「実行中」のスレッドは常に 1 本か。
    ///
    /// **停止中の観測 1 回では判定できない。** 手がかりとして各スレッドの最上段を
    /// 分類するが、シンボルが無ければ待ちかどうかも分からない。
    /// 分からないものを「実行中」に数えると、実態より多く見える。
    /// </summary>
    public static Diagnosis ConcurrentExecution(IReadOnlyList<TaskInfo> tasks)
    {
        if (tasks.Count == 0)
        {
            return new Diagnosis("Q3", "同時に実行中のスレッドは常に 1 本か",
                DiagnosisStatus.Skipped, "スレッドが取れていません。", null, null);
        }

        var waiting = new List<TaskInfo>();
        var running = new List<TaskInfo>();
        var unknown = new List<TaskInfo>();

        foreach (var task in tasks)
        {
            var bucket = Classify(task.TopFunction) switch
            {
                ThreadActivity.Waiting => waiting,
                ThreadActivity.Running => running,
                _ => unknown,
            };

            bucket.Add(task);
        }

        var detail = $"停止した時点: 待ち {waiting.Count} / 実行中 {running.Count} / 判定不能 {unknown.Count}";

        if (running.Count > 0)
        {
            detail += $"。実行中: {string.Join(", ", running.Select(Label))}";
        }

        if (unknown.Count > 0)
        {
            detail += $"。判定不能のスレッドは、最上段にシンボルが無いか、" +
                      $"デバッガの表示文字列が入っています（{unknown.Count} 本）";
        }

        return new Diagnosis("Q3", "同時に実行中のスレッドは常に 1 本か",
            DiagnosisStatus.Unknown,
            "この道具では判定できません。1 回の観測では「常に」は言えないためです。",
            detail,
            "何度か stakeout pause して同じ結果になるかを見るか、Target の設計（スケジューラの有無）を確認してください。" +
            "1 本しか動かないなら、stakeout thread freeze でタスクを止める調査が使えます。");
    }

    /// <summary>スレッドの様子。</summary>
    public enum ThreadActivity
    {
        /// <summary>待ち関数に居る。</summary>
        Waiting,

        /// <summary>ユーザーコードに居る。</summary>
        Running,

        /// <summary>シンボルが無い等で判定できない。</summary>
        Unknown,
    }

    /// <summary>最上段の関数名から、そのスレッドの様子を分類する。</summary>
    public static ThreadActivity Classify(string topFunction)
    {
        if (string.IsNullOrWhiteSpace(topFunction) || BareAddress().IsMatch(topFunction))
        {
            return ThreadActivity.Unknown;
        }

        // Visual Studio はスタックが取れないとき、表示用の文字列を返してくる
        if (topFunction.StartsWith('[') || topFunction.Contains("スレッド", StringComparison.Ordinal))
        {
            return ThreadActivity.Unknown;
        }

        return WaitFunction().IsMatch(topFunction) ? ThreadActivity.Waiting : ThreadActivity.Running;
    }

    private static string Label(TaskInfo task) =>
        $"{task.TaskName ?? task.ThreadId.ToString()}({Short(task.TopFunction)})";

    /// <summary>Q4: スレッドに名前が付いているか。</summary>
    public static Diagnosis ThreadNames(IReadOnlyList<TaskInfo> tasks)
    {
        if (tasks.Count == 0)
        {
            return new Diagnosis("Q4", "スレッドに名前が付いているか",
                DiagnosisStatus.Skipped, "スレッドが取れていません。", null, null);
        }

        var named = tasks.Where(t => t.Name is { Length: > 0 }).ToArray();

        if (named.Length == 0)
        {
            return new Diagnosis("Q4", "スレッドに名前が付いているか",
                DiagnosisStatus.Caution,
                "付いていません。",
                $"{tasks.Count} スレッドすべてで名前が空でした。",
                "タスク名はエントリ関数のパターンから決めることになります（tasks.taskEntryPatterns）。" +
                "Target 側で SetThreadDescription を呼べるなら、そちらのほうが確実です。");
        }

        return new Diagnosis("Q4", "スレッドに名前が付いているか",
            DiagnosisStatus.Answered,
            $"付いています（{named.Length}/{tasks.Count}）。",
            string.Join(", ", named.Take(8).Select(t => $"{t.ThreadId}:{t.Name}")),
            null);
    }

    [GeneratedRegex(@"^(0x)?[0-9a-fA-F`]{6,}$")]
    private static partial Regex BareAddress();

    private static string Short(string function)
    {
        var name = TaskNameResolver.StripSignature(function);
        return name.Length == 0 ? function : name;
    }
}
