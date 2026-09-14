using System.Diagnostics;
using StackFrame = Stakeout.Rpc.StackFrame;
using Stakeout.Rpc;

namespace Stakeout.Core;

/// <summary>
/// 複合コマンド（design.md §9）。「調査 1 手」を 1 回の呼び出しにまとめる。
///
/// 共通の約束:
/// - 自分が追加したブレークポイントは必ず後始末する
/// - 何をしたかを <see cref="CompositeStep"/> に残し、エージェントが検証できるようにする
/// - 期限を過ぎたら「ここまでの結果」を <c>Partial</c> 付きで返す。黙って待ち続けない
/// </summary>
public sealed class CompositeService
{
    private readonly IDebuggerBackend _backend;
    private readonly StakeoutConfig _config;
    private readonly TaskNameResolver _tasks;

    public CompositeService(IDebuggerBackend backend, StakeoutConfig config)
    {
        _backend = backend;
        _config = config;
        _tasks = new TaskNameResolver(config.Tasks.TaskEntryPatterns);
    }

    // ---------------------------------------------------------------- run-until

    /// <summary>
    /// 指定位置に一時ブレークポイントを張り、そこに到達するまで実行する（design.md §9.1）。
    /// </summary>
    public async Task<RunUntilResult> RunUntilAsync(RunUntilRequest request, CancellationToken ct)
    {
        var steps = new StepLog();
        var timeout = Timeout(request.TimeoutMs);

        var kind = request.Location.Contains(':') ? BreakpointKind.Line : BreakpointKind.Function;
        var breakpoint = await _backend.SetBreakpointAsync(
            new BreakpointRequest(kind, request.Location, request.Condition), ct);

        steps.Add("bp.set", $"#{breakpoint.BreakpointId} {request.Location}" +
                            (request.Condition is null ? string.Empty : $" when {request.Condition}"));

        try
        {
            await _backend.ContinueAsync(ct);
            steps.Add("continue", "実行を再開");

            var stop = await _backend.WaitForStopAsync(timeout, ct);

            if (stop is null)
            {
                steps.Add("timeout", $"{timeout.TotalSeconds:F0} 秒以内に到達しなかった");
                return new RunUntilResult(false, true, null, null, null, null, null, steps.Items);
            }

            var reached = stop.BreakpointId == breakpoint.BreakpointId;
            steps.Add("stop", reached
                ? $"目的地に到達 (thread {stop.ThreadId})"
                : $"別の理由で停止: {stop.Reason} (thread {stop.ThreadId})");

            var thread = await CurrentThreadAsync(stop.ThreadId, ct);

            IReadOnlyList<StackFrame>? stack = null;
            if (request.CaptureStack)
            {
                stack = await _backend.GetStackAsync(stop.ThreadId, request.MaxDepth, ct);
                steps.Add("stack", $"{stack.Count} フレーム");
            }

            IReadOnlyList<Variable>? locals = null;
            if (request.CaptureLocals && stack is { Count: > 0 })
            {
                locals = await _backend.GetScopeAsync(stack[0].FrameId, ScopeKind.Locals, ct);
                steps.Add("locals", $"{locals.Count} 個");
            }

            IReadOnlyDictionary<string, string>? exprs = null;
            if (request.Exprs is { Length: > 0 })
            {
                exprs = await EvaluateAllAsync(request.Exprs, stack is { Count: > 0 } ? stack[0].FrameId : null, ct);
                steps.Add("eval", string.Join(", ", request.Exprs));
            }

            return new RunUntilResult(reached, false, stop, thread, stack, locals, exprs, steps.Items);
        }
        finally
        {
            if (!request.KeepBreakpoint)
            {
                await RemoveQuietlyAsync(breakpoint.BreakpointId, steps);
            }
        }
    }

    // ---------------------------------------------------------------- watch-until-change

    /// <summary>
    /// 式が指すメモリへの書き込みを捉え、値が変わるまで追う（design.md §9.2）。
    /// </summary>
    public async Task<WatchUntilChangeResult> WatchUntilChangeAsync(
        WatchUntilChangeRequest request, CancellationToken ct)
    {
        var steps = new StepLog();
        var deadline = Stopwatch.StartNew();
        var timeout = Timeout(request.TimeoutMs);

        // データブレークポイントは停止中にしか張れない（ADR 0006）。
        // 実行中に呼ばれたら、そう言って断る。黙って空の結果を返さない
        await RequireStoppedAsync("watch-until-change", ct);

        // 式は停止しているフレームのスコープで解決される（ADR 0006 / 0013）。
        // ここで失敗したら、その旨は Backend が hint 付きで返す
        var addressVar = await _backend.EvaluateAsync($"&({request.Expr})", null, new EvalOptions(), ct);
        var address = ExtractAddress(addressVar.Value)
            ?? throw new BackendException(
                ErrorCodes.Backend,
                $"'{request.Expr}' のアドレスを求められません: {addressVar.Value}",
                "式が正しいか、別モジュールのグローバルならモジュール修飾 {,,モジュール名} が要るか確認してください。");

        var size = await SizeOfAsync(request.Expr, ct);
        string? warning = null;

        // ハードウェアデータブレークポイントが見られるのは 8 バイトまで
        if (size > 8)
        {
            warning = $"{request.Expr} は {size} バイトですが、先頭 8 バイトだけを監視します。" +
                      "その範囲外への書き込みは検出できません。";
            size = 8;
        }

        steps.Add("resolve", $"{request.Expr} -> {address} ({size} バイト)");

        var before = await ReadAsync(request.Expr, ct);
        var dataExpr = $"{address}";

        var breakpoint = await _backend.SetBreakpointAsync(
            new BreakpointRequest(BreakpointKind.Data, dataExpr, DataSize: size, ExpectedAddress: address), ct);

        steps.Add("bp.data", $"#{breakpoint.BreakpointId} {dataExpr}");

        var hits = new List<WatchHit>();
        var changed = false;

        try
        {
            for (var i = 0; i < request.MaxHits; i++)
            {
                if (deadline.Elapsed > timeout)
                {
                    steps.Add("timeout", $"{timeout.TotalSeconds:F0} 秒で打ち切り");
                    break;
                }

                await _backend.ContinueAsync(ct);

                var remaining = timeout - deadline.Elapsed;
                var stop = await _backend.WaitForStopAsync(
                    remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining, ct);

                if (stop is null)
                {
                    steps.Add("timeout", "書き込みを待っている間に期限切れ");
                    break;
                }

                if (stop.BreakpointId != breakpoint.BreakpointId)
                {
                    steps.Add("stop", $"監視外の理由で停止: {stop.Reason}");
                    break;
                }

                var after = await ReadAsync(request.Expr, ct);
                var stack = await _backend.GetStackAsync(stop.ThreadId, request.StackDepth, ct);
                var thread = await CurrentThreadAsync(stop.ThreadId, ct);
                var top = stack.Count > 0 ? stack[0] : null;

                hits.Add(new WatchHit(
                    ThreadId: stop.ThreadId,
                    TaskName: await ResolveTaskNameAsync(thread, stack, ct),
                    Function: top?.Function ?? "(unknown)",
                    File: top?.File,
                    Line: top?.Line,
                    Before: before,
                    After: after,
                    Stack: stack));

                steps.Add("hit", $"{top?.Function} thread {stop.ThreadId}: {before} -> {after}");

                if (after != before)
                {
                    changed = true;
                    before = after;
                    break;
                }

                before = after;
            }

            return new WatchUntilChangeResult(
                changed, request.Expr, address, size, before, hits, warning, steps.Items);
        }
        finally
        {
            // データブレークポイントは 4 本しかない。必ず返す（ADR 0006）
            await RemoveQuietlyAsync(breakpoint.BreakpointId, steps);
        }
    }

    // ---------------------------------------------------------------- trace-expr

    /// <summary>停止中のスレッドを N 回ステップし、各ステップで式を評価する（design.md §9.3）。</summary>
    public async Task<TraceExprResult> TraceExprAsync(TraceExprRequest request, CancellationToken ct)
    {
        var steps = new StepLog();
        var deadline = Stopwatch.StartNew();
        var timeout = Timeout(request.TimeoutMs);
        var points = new List<TraceExprPoint>();
        var partial = false;

        steps.Add("trace", $"{request.Steps} ステップ ({request.Kind})");

        for (var i = 0; i < request.Steps; i++)
        {
            if (deadline.Elapsed > timeout)
            {
                steps.Add("timeout", $"{i} ステップで打ち切り");
                partial = true;
                break;
            }

            await _backend.StepAsync(request.Kind, 0, ct);

            var stop = await _backend.WaitForStopAsync(timeout - deadline.Elapsed, ct);
            if (stop is null)
            {
                steps.Add("timeout", $"{i} ステップ目で停止を待てなかった");
                partial = true;
                break;
            }

            var stack = await _backend.GetStackAsync(stop.ThreadId, 1, ct);
            var frame = stack.Count > 0 ? stack[0] : null;

            points.Add(new TraceExprPoint(
                Step: i + 1,
                Function: frame?.Function ?? "(unknown)",
                File: frame?.File,
                Line: frame?.Line,
                Values: await EvaluateAllAsync(request.Exprs, frame?.FrameId, ct)));
        }

        return new TraceExprResult(points, partial, steps.Items);
    }

    // ---------------------------------------------------------------- dump

    /// <summary>
    /// 構造体を再帰的に展開する（design.md §10.7）。
    /// 結果は平らなノード列で返す。木のままだと大きい結果を分割できない（ADR 0014）。
    /// </summary>
    public async Task<DumpResult> DumpAsync(DumpRequest request, CancellationToken ct)
    {
        var steps = new StepLog();
        var budget = _config.Limits.MaxEvaluationsPerRequest;
        var used = 0;
        var nodes = new List<DumpNode>();

        var root = await _backend.EvaluateAsync(request.Expr, request.FrameId, new EvalOptions(), ct);
        used++;

        await ExpandAsync(
            root, request.Expr, 0, request.Depth, request.MaxItems, nodes, ct,
            () => used < budget, () => used++);

        steps.Add("dump", $"{request.Expr} を深さ {request.Depth} まで展開" +
                          $"（{nodes.Count} 要素 / 式評価 {used} 回）");

        return new DumpResult(request.Expr, nodes, used, used >= budget, steps.Items);
    }

    private async Task ExpandAsync(
        Variable variable, string path, int depth, int maxDepth, int maxItems,
        List<DumpNode> sink, CancellationToken ct, Func<bool> hasBudget, Action consume)
    {
        string? note = null;

        var expandable = variable.VariablesReference != 0 && variable.IsValid;

        // ヌルポインタは展開しない。展開しようとしても評価エラーが並ぶだけになる
        if (expandable && variable.IsPointer && LooksNull(variable.Value))
        {
            expandable = false;
            note = "ヌルポインタのため展開しません";
        }

        if (expandable && depth >= maxDepth)
        {
            expandable = false;
            note = "深さの上限に達しました";
        }

        if (expandable && !hasBudget())
        {
            expandable = false;
            note = "式評価の上限に達しました";
        }

        sink.Add(new DumpNode(
            path, depth, variable.Name, variable.Type, variable.Value,
            variable.IsPointer, variable.IsValid, note));

        if (!expandable)
        {
            return;
        }

        var members = await _backend.ExpandAsync(variable.VariablesReference, ct);
        consume();

        var taken = 0;
        foreach (var member in members)
        {
            if (taken >= maxItems)
            {
                sink.Add(new DumpNode(
                    $"{path}[...]", depth + 1, "...", string.Empty, string.Empty, false, true,
                    $"{members.Count} 個中 {maxItems} 個まで展開しました"));
                break;
            }

            if (!hasBudget())
            {
                sink.Add(new DumpNode(
                    $"{path}[...]", depth + 1, "...", string.Empty, string.Empty, false, true,
                    "式評価の上限に達しました"));
                break;
            }

            await ExpandAsync(
                member, ChildPath(path, member.Name), depth + 1, maxDepth, maxItems,
                sink, ct, hasBudget, consume);

            taken++;
        }
    }

    /// <summary>"g_ctx" + "inner" -> "g_ctx.inner"、"name" + "[0]" -> "name[0]"。</summary>
    private static string ChildPath(string parent, string name) =>
        name.StartsWith('[') ? parent + name : $"{parent}.{name}";

    // ---------------------------------------------------------------- find-corruption

    /// <summary>
    /// 静的な書き込み候補と、実際に捕まえた書き込みを突き合わせる（design.md §9.5）。
    ///
    /// **候補に無い書き込みを強調する**のがこの機能の要点である。
    /// 「誰が書いたか」だけなら watch-until-change で足りる。
    /// 知りたいのは「書くはずのない場所が書いていないか」である。
    /// </summary>
    public async Task<FindCorruptionResult> FindCorruptionAsync(
        FindCorruptionRequest request, CodeIndex index, CancellationToken ct)
    {
        var steps = new StepLog();
        var deadline = Stopwatch.StartNew();
        var timeout = Timeout(request.TimeoutMs);

        await RequireStoppedAsync("find-corruption", ct);

        IReadOnlyList<WriteSite> writers = Array.Empty<WriteSite>();
        var indexAvailable = index.IsConfigured;

        if (indexAvailable)
        {
            writers = index.Writers(request.Symbol);
            steps.Add("code.writers", $"静的な候補 {writers.Count} 件");
        }
        else
        {
            // 索引が無くても動的な観測はできる。ただし「想定外か」は判定できない
            steps.Add("code.writers", "Code Index が未設定のため、静的な候補は取れない");
        }

        var addressVar = await _backend.EvaluateAsync($"&({request.Symbol})", null, new EvalOptions(), ct);
        var address = ExtractAddress(addressVar.Value)
            ?? throw new BackendException(
                ErrorCodes.Backend,
                $"'{request.Symbol}' のアドレスを求められません: {addressVar.Value}",
                "別モジュールのグローバルならモジュール修飾 {,,モジュール名} を付けてください。");

        var size = await SizeOfAsync(request.Symbol, ct);
        string? warning = null;

        if (size > 8)
        {
            warning = $"{request.Symbol} は {size} バイトですが、先頭 8 バイトだけを監視します。" +
                      "その範囲外への書き込みは検出できません。";
            size = 8;
        }

        steps.Add("resolve", $"{request.Symbol} -> {address} ({size} バイト)");

        var breakpoint = await _backend.SetBreakpointAsync(
            new BreakpointRequest(BreakpointKind.Data, address, DataSize: size, ExpectedAddress: address), ct);

        steps.Add("bp.data", $"#{breakpoint.BreakpointId} {address}");

        var hits = new List<CorruptionHit>();
        var before = await ReadAsync(request.Symbol, ct);

        try
        {
            for (var i = 0; i < request.MaxHits; i++)
            {
                if (deadline.Elapsed > timeout)
                {
                    steps.Add("timeout", $"{timeout.TotalSeconds:F0} 秒で打ち切り（{hits.Count} 件収集）");
                    break;
                }

                await _backend.ContinueAsync(ct);

                var remaining = timeout - deadline.Elapsed;
                var stop = await _backend.WaitForStopAsync(
                    remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining, ct);

                if (stop is null)
                {
                    steps.Add("timeout", "書き込みを待っている間に期限切れ");
                    break;
                }

                if (stop.BreakpointId != breakpoint.BreakpointId)
                {
                    steps.Add("stop", $"監視外の理由で停止: {stop.Reason}");
                    break;
                }

                var after = await ReadAsync(request.Symbol, ct);
                var stack = await _backend.GetStackAsync(stop.ThreadId, 8, ct);
                var thread = await CurrentThreadAsync(stop.ThreadId, ct);
                var top = stack.Count > 0 ? stack[0] : null;

                var (expected, note) = Judge(writers, indexAvailable, top);

                hits.Add(new CorruptionHit(
                    ThreadId: stop.ThreadId,
                    TaskName: await ResolveTaskNameAsync(thread, stack, ct),
                    Function: top?.Function ?? "(unknown)",
                    File: top?.File,
                    Line: top?.Line,
                    Before: before,
                    After: after,
                    Expected: expected,
                    Note: note));

                steps.Add("hit", $"{top?.Function} ({(expected ? "候補内" : "候補外")}): {before} -> {after}");
                before = after;
            }

            return new FindCorruptionResult(
                request.Symbol, address, size, indexAvailable, writers, hits, warning, steps.Items);
        }
        finally
        {
            await RemoveQuietlyAsync(breakpoint.BreakpointId, steps);
        }
    }

    /// <summary>捕まえた書き込みが静的な候補に含まれるかを判定する。</summary>
    private static (bool Expected, string Note) Judge(
        IReadOnlyList<WriteSite> writers, bool indexAvailable, StackFrame? frame)
    {
        if (!indexAvailable)
        {
            return (true, "Code Index が無いため、想定内かどうかは判定できません");
        }

        if (frame is null)
        {
            return (false, "停止位置を特定できませんでした");
        }

        var function = TaskNameResolver.StripSignature(frame.Function);

        var match = writers.FirstOrDefault(w =>
            string.Equals(w.Function, function, StringComparison.Ordinal)
            || (frame.File is not null
                && frame.Line is { } line
                && w.Line == line
                && frame.File.EndsWith(w.File.Replace('/', Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)));

        return match is not null
            ? (true, $"静的な候補に一致（{match.File}:{match.Line} {match.Reason}）")
            : (false, "静的な候補に無い書き込みです。ここを疑ってください");
    }

    // ---------------------------------------------------------------- task-map

    /// <summary>全スレッドにタスク名を当てる（design.md §9.6）。</summary>
    public async Task<TaskMapResult> TaskMapAsync(CancellationToken ct)
    {
        var steps = new StepLog();
        var threads = await _backend.GetThreadsAsync(ct);
        var tasks = new List<TaskInfo>();

        foreach (var thread in threads)
        {
            // エントリ関数はスタックの下のほうにある。深すぎると高くつくので上限を切る
            var stack = await _backend.GetStackAsync(thread.ThreadId, 32, ct);
            var functions = stack.Select(f => f.Function).ToArray();

            tasks.Add(new TaskInfo(
                ThreadId: thread.ThreadId,
                Name: thread.Name,
                TaskName: _tasks.Resolve(thread.Name, functions),
                EntryFunction: functions.Length > 0 ? functions[^1] : string.Empty,
                TopFunction: functions.Length > 0 ? functions[0] : thread.TopFunction,
                IsFrozen: thread.IsFrozen,
                IsCurrent: thread.IsCurrent));
        }

        steps.Add("task-map", $"{tasks.Count} スレッド中 {tasks.Count(t => t.TaskName is not null)} 個にタスク名が付いた");
        return new TaskMapResult(tasks, steps.Items);
    }

    /// <summary>タスク名またはスレッド ID から、対象スレッドを 1 つに決める。</summary>
    public async Task<int> ResolveThreadAsync(ThreadTargetRequest request, CancellationToken ct)
    {
        if (request.ThreadId is { } id)
        {
            return id;
        }

        if (request.TaskName is not { Length: > 0 } taskName)
        {
            throw new BackendException(
                ErrorCodes.Usage,
                "対象スレッドが指定されていません。",
                "スレッド ID か --task のどちらかを指定してください。");
        }

        var map = await TaskMapAsync(ct);
        var matches = map.Tasks
            .Where(t => string.Equals(t.TaskName, taskName, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return matches.Length switch
        {
            1 => matches[0].ThreadId,
            0 => throw new BackendException(
                ErrorCodes.NotFound,
                $"タスク '{taskName}' に対応するスレッドがありません。",
                $"stakeout task-map で確認してください。付いているタスク名: " +
                string.Join(", ", map.Tasks.Where(t => t.TaskName is not null).Select(t => t.TaskName))),
            _ => throw new BackendException(
                ErrorCodes.Precondition,
                $"タスク '{taskName}' に {matches.Length} 本のスレッドが一致します。",
                $"--thread で指定してください。候補: {string.Join(", ", matches.Select(m => m.ThreadId))}"),
        };
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>停止中でなければ、何をすればよいかを添えて断る。</summary>
    private async Task RequireStoppedAsync(string operation, CancellationToken ct)
    {
        try
        {
            await _backend.GetThreadsAsync(ct);
        }
        catch (BackendException ex) when (ex.Code == ErrorCodes.Precondition)
        {
            throw new BackendException(
                ErrorCodes.Precondition,
                $"{operation} は停止中にしか実行できません。",
                "stakeout pause で止めてから実行してください。" +
                "狙った場所で止めたい場合は stakeout run-until を使ってください。",
                ex);
        }
    }

    private TimeSpan Timeout(int? requested) =>
        TimeSpan.FromMilliseconds(requested ?? _config.Limits.CompositeSec * 1000);

    private async Task<ThreadInfo?> CurrentThreadAsync(int threadId, CancellationToken ct)
    {
        var threads = await _backend.GetThreadsAsync(ct);
        return threads.FirstOrDefault(t => t.ThreadId == threadId);
    }

    private async Task<string?> ResolveTaskNameAsync(
        ThreadInfo? thread, IReadOnlyList<StackFrame> stack, CancellationToken ct)
    {
        if (thread is null)
        {
            return null;
        }

        var functions = stack.Select(f => f.Function).ToList();

        // 浅いスタックしか無いとエントリ関数に届かない。足りなければ取り直す
        if (_tasks.HasPatterns && _tasks.Resolve(thread.Name, functions) is null && stack.Count >= 8)
        {
            var deeper = await _backend.GetStackAsync(thread.ThreadId, 32, ct);
            functions = deeper.Select(f => f.Function).ToList();
        }

        return _tasks.Resolve(thread.Name, functions);
    }

    private async Task<IReadOnlyDictionary<string, string>> EvaluateAllAsync(
        IReadOnlyList<string> exprs, int? frameId, CancellationToken ct)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var expr in exprs)
        {
            try
            {
                var variable = await _backend.EvaluateAsync(expr, frameId, new EvalOptions(), ct);
                values[expr] = variable.Value;
            }
            catch (BackendException ex)
            {
                // 1 つ評価できなくても他の結果は返す。調査の手がかりを全部捨てない
                values[expr] = $"(評価できません: {ex.Message})";
            }
        }

        return values;
    }

    private async Task<string> ReadAsync(string expr, CancellationToken ct)
    {
        var variable = await _backend.EvaluateAsync(expr, null, new EvalOptions(), ct);
        return variable.Value;
    }

    private async Task<int> SizeOfAsync(string expr, CancellationToken ct)
    {
        try
        {
            var variable = await _backend.EvaluateAsync($"sizeof({expr})", null, new EvalOptions(), ct);
            return int.TryParse(variable.Value.Trim(), out var size) && size > 0 ? size : 4;
        }
        catch (BackendException)
        {
            // 求まらなければ 4 バイトとして続ける。監視できないよりましである
            return 4;
        }
    }

    private async Task RemoveQuietlyAsync(int breakpointId, StepLog steps)
    {
        try
        {
            await _backend.RemoveBreakpointAsync(breakpointId, CancellationToken.None);
            steps.Add("bp.remove", $"#{breakpointId}");
        }
        catch (BackendException ex)
        {
            steps.Add("bp.remove", $"#{breakpointId} を削除できませんでした: {ex.Message}");
        }
    }

    /// <summary>"0x00007ff9..." の形のアドレスを取り出す。</summary>
    public static string? ExtractAddress(string value)
    {
        var index = value.IndexOf("0x", StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        var end = index + 2;
        while (end < value.Length && Uri.IsHexDigit(value[end]))
        {
            end++;
        }

        return end > index + 2 ? value[index..end] : null;
    }

    private static bool LooksNull(string value) =>
        ExtractAddress(value) is { } address && address.TrimStart('0', 'x', 'X').Length == 0;

    private sealed class StepLog
    {
        private readonly List<CompositeStep> _items = new();

        public IReadOnlyList<CompositeStep> Items => _items;

        public void Add(string action, string detail) =>
            _items.Add(new CompositeStep(action, detail, DateTimeOffset.Now));
    }
}
