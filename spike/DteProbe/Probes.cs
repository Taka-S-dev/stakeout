using System.Diagnostics;
using EnvDTE;
using EnvDTE80;

namespace DteProbe;

internal sealed record ProbeResult(
    string Id,
    string Title,
    string Status,                       // ok | partial | fail | skip
    string Summary,
    Dictionary<string, object?> Details);

/// <summary>
/// design.md の [要検証] を潰すための検証群（ADR 0003 の S1〜S5）。
/// 使い捨て。抽象化しない。1 メソッド = 1 検証項目。
/// </summary>
internal sealed class Probes : IDisposable
{
    private readonly DteCandidate _vs;
    private readonly DTE2 _dte;
    private readonly Debugger2 _dbg;
    private readonly RetryMessageFilter _filter;
    private readonly int _targetPid;
    private readonly string _libSource;
    private readonly string _libSourceName;

    // DebuggerEvents は参照を保持し続けないと GC されてイベントが飛ばなくなる。
    // フィールドで持つこと自体が S5 の検証対象である。
    private DebuggerEvents? _debuggerEvents;
    private int _breakEventCount;

    private readonly List<Breakpoint> _created = new();

    public Probes(DteCandidate vs, RetryMessageFilter filter, int targetPid, string libSource)
    {
        _vs = vs;
        _dte = (DTE2)vs.Dte;

        // VS が起動直後・スタートウィンドウ表示中は COM 呼び出しを拒否し続ける。
        // メッセージフィルタだけでは足りないので、呼び出し側でも待つ（ADR 0004）
        _dbg = Com.Retry(() => (Debugger2)_dte.Debugger, TimeSpan.FromSeconds(60));
        _filter = filter;
        _targetPid = targetPid;
        _libSource = libSource;
        _libSourceName = Path.GetFileName(libSource);
    }

    // ---------------------------------------------------------------- S1

    public ProbeResult S1_ConnectAndMessageFilter()
    {
        var details = new Dictionary<string, object?>
        {
            ["moniker"] = _vs.MonikerName,
            ["progId"] = _vs.ProgId,
            ["vsPid"] = _vs.Pid,
        };

        try
        {
            details["version"] = _dte.Version;
            details["edition"] = _dte.Edition;
            details["solution"] = string.IsNullOrEmpty(_dte.Solution?.FullName) ? "(none)" : _dte.Solution!.FullName;
            details["debugMode"] = _dte.Debugger.CurrentMode.ToString();

            // COM 越境 1 回あたりのコストを測る。stack --all の実現性に直結する
            const int iterations = 200;
            var sw = Stopwatch.StartNew();
            for (var i = 0; i < iterations; i++)
            {
                _ = _dte.Version;
            }

            sw.Stop();
            var perCallMs = sw.Elapsed.TotalMilliseconds / iterations;
            details["comCallMs"] = Math.Round(perCallMs, 4);
            details["retriesDuringProbe"] = _filter.RetryCount;

            return new ProbeResult("S1", "ROT 走査 / DTE 取得 / メッセージフィルタ", "ok",
                $"VS {_dte.Version} (pid {_vs.Pid}) に接続。COM 呼び出し 1 回 {perCallMs:F3} ms", details);
        }
        catch (Exception ex)
        {
            details["exception"] = Describe(ex);
            return new ProbeResult("S1", "ROT 走査 / DTE 取得 / メッセージフィルタ", "fail", ex.Message, details);
        }
    }

    // ---------------------------------------------------------------- S2

    public ProbeResult S2_AttachAndEvaluate()
    {
        var details = new Dictionary<string, object?>();

        try
        {
            try
            {
                details["attach"] = Attach();
            }
            catch (Exception attachEx)
            {
                details["attachError"] = Describe(attachEx);
                details["attachMessage"] = attachEx.Message;
                throw;
            }

            var line = FindLine(_libSource, "g_ctx.tick++;");
            details["bpLocation"] = $"{_libSourceName}:{line}";

            var bp = AddBreakpoint(file: _libSource, line: line);
            details["bpEnabled"] = SafeGet(() => bp.Enabled);
            details["bpFileLine"] = SafeGet(() => $"{Path.GetFileName(bp.File)}:{bp.FileLine}");

            if (!ContinueAndWaitForBreak(TimeSpan.FromSeconds(20), out var mode))
            {
                details["mode"] = mode.ToString();
                return new ProbeResult("S2", "アタッチ / 行ブレークポイント / 式評価", "fail",
                    "20 秒待っても中断モードにならなかった", details);
            }

            details["stoppedAt"] = SafeGet(() => _dbg.CurrentStackFrame?.FunctionName);
            details["stoppedThread"] = SafeGet(() => _dbg.CurrentThread?.ID);
            details["threadCount"] = SafeGet(() => _dbg.CurrentProgram?.Threads?.Count);

            var exprs = new[]
            {
                "g_ctx.state",
                "g_ctx.tick",
                "g_ctx.name",
                "g_ctx.inner.flags",
                "g_ctx.self->inner.id",
                "&g_shared.counter",
                "g_shared.counter",
            };

            var values = new Dictionary<string, object?>();
            var evalSw = Stopwatch.StartNew();
            foreach (var e in exprs)
            {
                values[e] = Evaluate(e);
            }

            evalSw.Stop();
            details["values"] = values;
            details["evalMsPerExpr"] = Math.Round(evalSw.Elapsed.TotalMilliseconds / exprs.Length, 2);

            // 構造体の子要素展開（dump の基礎）
            details["gCtxMembers"] = ExpandMembers("g_ctx");

            // 全スレッドのスタック取得コスト（stack --all の実現性）
            details["allStacks"] = MeasureAllStacks();

            // VS の式評価は既定で 10 進数を返す（0xA5A5A5A5 = 2779096485）
            var ok = values["g_ctx.inner.flags"] is string f
                     && (f.Contains("A5A5A5A5", StringComparison.OrdinalIgnoreCase)
                         || f.Contains("2779096485", StringComparison.Ordinal));
            return new ProbeResult("S2", "アタッチ / 行ブレークポイント / 式評価", ok ? "ok" : "partial",
                ok ? "C 構造体のメンバを読めた" : "停止はしたが期待値が読めなかった", details);
        }
        catch (Exception ex)
        {
            details["exception"] = Describe(ex);
            return new ProbeResult("S2", "アタッチ / 行ブレークポイント / 式評価", "fail", ex.Message, details);
        }
    }

    // ---------------------------------------------------------------- S3

    public ProbeResult S3_DataBreakpoint()
    {
        var details = new Dictionary<string, object?>();
        var attempts = new List<Dictionary<string, object?>>();
        details["attempts"] = attempts;

        try
        {
            if (_dte.Debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
            {
                return new ProbeResult("S3", "データブレークポイント", "skip",
                    "中断モードでないため試行できない（データ BP は中断中にしか張れない）", details);
            }

            var address = Evaluate("&g_shared.counter") as string;
            details["address"] = address;
            var bareAddress = ExtractHexAddress(address);
            details["bareAddress"] = bareAddress;

            // VS のデータ BP は指定方法が複数あり、どれが EnvDTE 経由で通るか分からない。
            // 通るものが 1 つでもあれば良いので順に試す。
            // データ式は現在のフレームのスコープで解決される。別モジュールのグローバルは
            // モジュール修飾 {,,DLL名} を付けないと 0x89711010 で拒否される
            var candidates = new List<(string Label, string Data, int Count)>
            {
                ("module-qualified", "{,,NativeLib.dll}&g_shared.counter", 4),
                ("&expr", "&g_shared.counter", 4),
                ("expr", "g_shared.counter", 4),
            };
            if (bareAddress is not null)
            {
                candidates.Add(("address", bareAddress, 4));
                candidates.Add(("cast-deref", $"*(long*){bareAddress}", 4));
            }

            Breakpoint? added = null;
            foreach (var (label, data, count) in candidates)
            {
                var attempt = new Dictionary<string, object?> { ["form"] = label, ["data"] = data, ["count"] = count };
                attempts.Add(attempt);
                try
                {
                    var bps = _dte.Debugger.Breakpoints.Add(Data: data, DataCount: count);
                    if (bps is null || bps.Count == 0)
                    {
                        attempt["result"] = "追加されなかった（例外なし・件数 0）";
                        continue;
                    }

                    var bp = bps.Item(1);
                    _created.Add(bp);
                    attempt["result"] = "added";
                    attempt["bpName"] = SafeGet(() => bp.Name);
                    attempt["bpEnabled"] = SafeGet(() => bp.Enabled);
                    added = bp;
                    break;
                }
                catch (Exception ex)
                {
                    attempt["result"] = "throw";
                    attempt["exception"] = Describe(ex);
                }
            }

            if (added is null)
            {
                return new ProbeResult("S3", "データブレークポイント", "fail",
                    "EnvDTE 経由ではどの形式でもデータ BP を張れなかった", details);
            }

            // 実際に書き込みで止まるか。Task_C_Main は 2 秒ごとに書く
            RemoveLineBreakpoints();
            var hit = ContinueAndWaitForBreak(TimeSpan.FromSeconds(15), out _);
            details["hit"] = hit;
            if (hit)
            {
                details["hitFunction"] = SafeGet(() => _dbg.CurrentStackFrame?.FunctionName);
                details["hitThread"] = SafeGet(() => _dbg.CurrentThread?.ID);
                details["counterAfter"] = Evaluate("g_shared.counter");
                details["lastHitName"] = SafeGet(() => _dte.Debugger.BreakpointLastHit?.Name);
            }

            return new ProbeResult("S3", "データブレークポイント", hit ? "ok" : "partial",
                hit ? "データ BP を張れて、書き込みで停止した" : "張れたが 15 秒以内に停止しなかった", details);
        }
        catch (Exception ex)
        {
            details["exception"] = Describe(ex);
            return new ProbeResult("S3", "データブレークポイント", "fail", ex.Message, details);
        }
    }

    // ---------------------------------------------------------------- S4

    public ProbeResult S4_TracepointThroughput(int measureSeconds)
    {
        var details = new Dictionary<string, object?>();

        try
        {
            RemoveAllCreatedBreakpoints();

            var bps = _dte.Debugger.Breakpoints.Add(Function: "nl_update_state");
            if (bps is null || bps.Count == 0)
            {
                return new ProbeResult("S4", "トレースポイントと出力回収", "fail",
                    "関数ブレークポイントを追加できなかった", details);
            }

            var bp = bps.Item(1);
            _created.Add(bp);

            if (bp is not Breakpoint2 bp2)
            {
                details["bpType"] = bp.GetType().FullName;
                return new ProbeResult("S4", "トレースポイントと出力回収", "fail",
                    "Breakpoint2 にキャストできない（BreakWhenHit / Message が使えない）", details);
            }

            const string marker = "STAKEOUT|";
            try
            {
                bp2.Message = marker + "{g_ctx.state}|{g_ctx.tick}|$TID|$FUNCTION";
                bp2.BreakWhenHit = false;
                details["messageSet"] = bp2.Message;
                details["breakWhenHit"] = bp2.BreakWhenHit;
            }
            catch (Exception ex)
            {
                details["exception"] = Describe(ex);
                return new ProbeResult("S4", "トレースポイントと出力回収", "fail",
                    "BreakWhenHit / Message を設定できない", details);
            }

            details["outputPanes"] = ListOutputPanes();

            _dte.Debugger.Go(WaitForBreakOrEnd: false);
            Pump.For(TimeSpan.FromMilliseconds(500));

            var sw = Stopwatch.StartNew();
            Pump.For(TimeSpan.FromSeconds(measureSeconds));
            sw.Stop();

            var (paneName, text) = FindPaneContaining(marker);
            details["tracePane"] = paneName ?? "(見つからない)";

            if (text is null)
            {
                StopTarget();
                return new ProbeResult("S4", "トレースポイントと出力回収", "fail",
                    "どの出力ペインにも STAKEOUT| 行が出なかった。式が評価されていないか、出力先が違う", details);
            }

            var lines = text.Split('\n')
                            .Where(l => l.Contains(marker, StringComparison.Ordinal))
                            .ToArray();
            details["hitCount"] = lines.Length;
            details["hitsPerSecond"] = Math.Round(lines.Length / sw.Elapsed.TotalSeconds, 1);
            details["sampleLines"] = lines.Take(3).Select(l => l.TrimEnd('\r')).ToArray();
            details["measuredSeconds"] = Math.Round(sw.Elapsed.TotalSeconds, 2);
            details["paneTextChars"] = text.Length;

            // 式が実際に評価されたか（リテラルのまま出ていないか）
            var evaluated = lines.Length > 0 && !lines[0].Contains("{g_ctx.state}", StringComparison.Ordinal);
            details["expressionsEvaluated"] = evaluated;

            StopTarget();

            var status = lines.Length == 0 ? "fail" : evaluated ? "ok" : "partial";
            return new ProbeResult("S4", "トレースポイントと出力回収", status,
                $"{sw.Elapsed.TotalSeconds:F1} 秒で {lines.Length} ヒット " +
                $"({lines.Length / sw.Elapsed.TotalSeconds:F0} hits/s), 式評価={evaluated}", details);
        }
        catch (Exception ex)
        {
            details["exception"] = Describe(ex);
            return new ProbeResult("S4", "トレースポイントと出力回収", "fail", ex.Message, details);
        }
    }

    // ---------------------------------------------------------------- S5

    public ProbeResult S5_EventDelivery(int rounds)
    {
        var details = new Dictionary<string, object?> { ["rounds"] = rounds };

        var step = "start";
        try
        {
            step = "clear-breakpoints";
            // トレースポイントが残っていると Go/Break が競合するので全部消す
            RemoveAllCreatedBreakpoints();
            foreach (Breakpoint existing in _dte.Debugger.Breakpoints.Cast<Breakpoint>().ToArray())
            {
                try
                {
                    existing.Delete();
                }
                catch
                {
                    // 消せないものは無視する
                }
            }

            Pump.For(TimeSpan.FromMilliseconds(500));

            step = "subscribe";
            // 参照をフィールドで保持する。ローカル変数だと GC された時点で配送が止まる
            _debuggerEvents = _dte.Events.DebuggerEvents;
            _breakEventCount = 0;
            _debuggerEvents.OnEnterBreakMode += OnEnterBreakMode;

            step = "ensure-running";
            if (_dte.Debugger.CurrentMode == dbgDebugMode.dbgBreakMode)
            {
                _dte.Debugger.Go(WaitForBreakOrEnd: false);
                Pump.Until(() => _dte.Debugger.CurrentMode == dbgDebugMode.dbgRunMode, TimeSpan.FromSeconds(10));
            }

            step = "loop";

            var modeReachedButNoEvent = 0;
            var modeNotReached = 0;
            var sw = Stopwatch.StartNew();

            for (var i = 0; i < rounds; i++)
            {
                var before = Volatile.Read(ref _breakEventCount);

                if (_dte.Debugger.CurrentMode == dbgDebugMode.dbgBreakMode)
                {
                    _dte.Debugger.Go(WaitForBreakOrEnd: false);
                    Pump.Until(() => _dte.Debugger.CurrentMode == dbgDebugMode.dbgRunMode, TimeSpan.FromSeconds(5));
                }

                // Break() は中断中に呼ぶと 0x89711007 になる。冪等ではない
                if (_dte.Debugger.CurrentMode == dbgDebugMode.dbgRunMode)
                {
                    _dte.Debugger.Break(WaitForBreakMode: false);
                }

                var modeReached = Pump.Until(
                    () => _dte.Debugger.CurrentMode == dbgDebugMode.dbgBreakMode,
                    TimeSpan.FromSeconds(5));

                // イベントが少し遅れて来ることがあるので猶予を与える
                var eventReceived = Pump.Until(
                    () => Volatile.Read(ref _breakEventCount) > before,
                    TimeSpan.FromMilliseconds(500));

                if (!modeReached)
                {
                    modeNotReached++;
                }
                else if (!eventReceived)
                {
                    modeReachedButNoEvent++;
                }
            }

            sw.Stop();
            details["breakEventsReceived"] = Volatile.Read(ref _breakEventCount);
            details["modeReachedButNoEvent"] = modeReachedButNoEvent;
            details["modeNotReached"] = modeNotReached;
            details["msPerBreakGoCycle"] = Math.Round(sw.Elapsed.TotalMilliseconds / rounds, 1);

            return new ProbeResult("S5", "OnEnterBreakMode の配送信頼性",
                modeReachedButNoEvent == 0 && modeNotReached == 0 ? "ok" : "partial",
                $"{rounds} 回の break/go でイベント取りこぼし {modeReachedButNoEvent} 回、" +
                $"中断できず {modeNotReached} 回", details);
        }
        catch (Exception ex)
        {
            details["failedStep"] = step;
            details["debugMode"] = SafeGet(() => _dte.Debugger.CurrentMode.ToString());
            details["exception"] = Describe(ex);
            return new ProbeResult("S5", "OnEnterBreakMode の配送信頼性", "fail",
                $"{step} で失敗: {ex.Message}", details);
        }
    }

    private void OnEnterBreakMode(dbgEventReason reason, ref dbgExecutionAction action)
        => Interlocked.Increment(ref _breakEventCount);

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// アタッチ。どのエンジン指定が通るかは VS の版で変わりうるので順に試し、
    /// 失敗した理由をすべて記録して返す（検証が目的なので黙って諦めない）。
    /// </summary>
    private object Attach()
    {
        var log = new Dictionary<string, object?>();

        foreach (EnvDTE.Process p in _dte.Debugger.DebuggedProcesses)
        {
            if (p.ProcessID == _targetPid)
            {
                log["result"] = "already-debugged";
                return log;
            }
        }

        var local = _dte.Debugger.LocalProcesses;
        log["localProcessCount"] = local.Count;

        EnvDTE.Process? target = null;
        foreach (EnvDTE.Process p in local)
        {
            if (p.ProcessID == _targetPid)
            {
                target = p;
                break;
            }
        }

        if (target is null)
        {
            throw new InvalidOperationException(
                $"pid {_targetPid} が Debugger.LocalProcesses ({local.Count} 件) に見つからない。" +
                "昇格レベルが VS と揃っているか確認すること");
        }

        log["targetName"] = SafeGet(() => target.Name);
        log["isProcess2"] = target is Process2;

        var attempts = new List<Dictionary<string, object?>>();
        log["attempts"] = attempts;

        var strategies = new List<(string Label, Action Run)>();
        if (target is Process2 p2)
        {
            strategies.Add(("Attach2(\"Native\")", () => p2.Attach2("Native")));
            strategies.Add(("Attach2(string[]{\"Native\"})", () => p2.Attach2(new[] { "Native" })));
            strategies.Add(("Attach2(\"Native Code\")", () => p2.Attach2("Native Code")));
        }

        strategies.Add(("Attach()", target.Attach));

        foreach (var (label, run) in strategies)
        {
            var attempt = new Dictionary<string, object?> { ["strategy"] = label };
            attempts.Add(attempt);
            try
            {
                run();
                var entered = Pump.Until(
                    () => _dte.Debugger.CurrentMode != dbgDebugMode.dbgDesignMode,
                    TimeSpan.FromSeconds(20));
                attempt["result"] = entered ? "ok" : "呼び出しは通ったが設計モードのままだった";
                if (entered)
                {
                    log["result"] = label;
                    return log;
                }
            }
            catch (Exception ex)
            {
                attempt["result"] = "throw";
                attempt["exception"] = Describe(ex);
            }
        }

        throw new InvalidOperationException(
            "どのアタッチ方法も失敗した: " +
            string.Join(" / ", attempts.Select(a => $"{a["strategy"]}={a["result"]}")));
    }

    private Breakpoint AddBreakpoint(string file, int line)
    {
        var bps = _dte.Debugger.Breakpoints.Add(File: file, Line: line);
        if (bps is null || bps.Count == 0)
        {
            throw new InvalidOperationException($"{file}:{line} にブレークポイントを追加できなかった");
        }

        var bp = bps.Item(1);
        _created.Add(bp);
        return bp;
    }

    private bool ContinueAndWaitForBreak(TimeSpan timeout, out dbgDebugMode mode)
    {
        if (_dte.Debugger.CurrentMode == dbgDebugMode.dbgBreakMode)
        {
            _dte.Debugger.Go(WaitForBreakOrEnd: false);
            Pump.For(TimeSpan.FromMilliseconds(200));
        }

        var ok = Pump.Until(() => _dte.Debugger.CurrentMode == dbgDebugMode.dbgBreakMode, timeout);
        mode = _dte.Debugger.CurrentMode;
        return ok;
    }

    private void StopTarget()
    {
        try
        {
            if (_dte.Debugger.CurrentMode == dbgDebugMode.dbgRunMode)
            {
                _dte.Debugger.Break(WaitForBreakMode: false);
                Pump.Until(() => _dte.Debugger.CurrentMode == dbgDebugMode.dbgBreakMode, TimeSpan.FromSeconds(10));
            }
        }
        catch
        {
            // 計測結果を失わないため握りつぶす
        }
    }

    private object? Evaluate(string expr)
    {
        try
        {
            var e = _dbg.GetExpression2(expr, UseAutoExpandRules: true, TreatAsStatement: false, Timeout: 5000);
            return e.IsValidValue ? e.Value : $"(invalid: {e.Value})";
        }
        catch (Exception ex)
        {
            return $"(throw: {ex.GetType().Name}: {ex.Message})";
        }
    }

    private object? ExpandMembers(string expr)
    {
        try
        {
            var e = _dbg.GetExpression2(expr, UseAutoExpandRules: true, TreatAsStatement: false, Timeout: 5000);
            var members = new Dictionary<string, object?>();
            foreach (Expression m in e.DataMembers)
            {
                members[m.Name] = new { type = m.Type, value = m.Value, children = m.DataMembers?.Count ?? 0 };
            }

            return members;
        }
        catch (Exception ex)
        {
            return $"(throw: {ex.GetType().Name}: {ex.Message})";
        }
    }

    private object MeasureAllStacks()
    {
        var sw = Stopwatch.StartNew();
        var threads = 0;
        var frames = 0;

        try
        {
            var program = _dbg.CurrentProgram;
            if (program is null)
            {
                return "(CurrentProgram が null)";
            }

            foreach (EnvDTE.Thread t in program.Threads)
            {
                threads++;
                foreach (EnvDTE.StackFrame f in t.StackFrames)
                {
                    frames++;
                    _ = f.FunctionName;
                    if (frames > 400)
                    {
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            return $"(throw after {threads} threads / {frames} frames: {ex.Message})";
        }

        sw.Stop();
        return new
        {
            threads,
            frames,
            elapsedMs = Math.Round(sw.Elapsed.TotalMilliseconds, 1),
            msPerFrame = frames == 0 ? 0 : Math.Round(sw.Elapsed.TotalMilliseconds / frames, 3),
        };
    }

    private string[] ListOutputPanes()
    {
        try
        {
            var ow = _dte.ToolWindows.OutputWindow;
            var names = new List<string>();
            foreach (OutputWindowPane pane in ow.OutputWindowPanes)
            {
                names.Add(pane.Name);
            }

            return names.ToArray();
        }
        catch (Exception ex)
        {
            return new[] { $"(throw: {ex.Message})" };
        }
    }

    /// <summary>
    /// マーカーを含む出力ペインを名前ではなく中身で探す。
    /// ペイン名は VS の表示言語で変わる（日本語版では「デバッグ」）ので、名前で引くと壊れる。
    /// </summary>
    private (string? Name, string? Text) FindPaneContaining(string marker)
    {
        try
        {
            var ow = _dte.ToolWindows.OutputWindow;
            foreach (OutputWindowPane pane in ow.OutputWindowPanes)
            {
                string text;
                try
                {
                    var doc = pane.TextDocument;
                    var start = doc.StartPoint.CreateEditPoint();
                    text = start.GetText(doc.EndPoint);
                }
                catch
                {
                    continue;
                }

                if (text.Contains(marker, StringComparison.Ordinal))
                {
                    return (pane.Name, text);
                }
            }
        }
        catch
        {
            // 呼び出し側で「見つからない」として扱う
        }

        return (null, null);
    }

    private void RemoveLineBreakpoints()
    {
        foreach (var bp in _created.ToArray())
        {
            try
            {
                if (!string.IsNullOrEmpty(bp.File))
                {
                    bp.Delete();
                    _created.Remove(bp);
                }
            }
            catch
            {
                // 消せないものは Dispose でまとめて試す
            }
        }
    }

    private void RemoveAllCreatedBreakpoints()
    {
        foreach (var bp in _created.ToArray())
        {
            try
            {
                bp.Delete();
            }
            catch
            {
                // 同上
            }
        }

        _created.Clear();
    }

    private static int FindLine(string file, string needle)
    {
        var lines = File.ReadAllLines(file);
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains(needle, StringComparison.Ordinal))
            {
                return i + 1;
            }
        }

        throw new InvalidOperationException($"{file} に '{needle}' を含む行が無い");
    }

    private static string? ExtractHexAddress(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var i = value.IndexOf("0x", StringComparison.OrdinalIgnoreCase);
        if (i < 0)
        {
            return null;
        }

        var end = i + 2;
        while (end < value.Length && Uri.IsHexDigit(value[end]))
        {
            end++;
        }

        return end > i + 2 ? value[i..end] : null;
    }

    private static object? SafeGet<T>(Func<T> get)
    {
        try
        {
            return get();
        }
        catch (Exception ex)
        {
            return $"(throw: {ex.GetType().Name}: {ex.Message})";
        }
    }

    private static object Describe(Exception ex) => new
    {
        type = ex.GetType().FullName,
        message = ex.Message,
        hresult = $"0x{ex.HResult:X8}",
    };

    public void Dispose()
    {
        try
        {
            if (_debuggerEvents is not null)
            {
                _debuggerEvents.OnEnterBreakMode -= OnEnterBreakMode;
                _debuggerEvents = null;
            }

            RemoveAllCreatedBreakpoints();

            // デタッチする。Target は殺さない（design.md §15.3）
            if (_dte.Debugger.CurrentMode != dbgDebugMode.dbgDesignMode)
            {
                _dte.Debugger.DetachAll();
                Pump.Until(() => _dte.Debugger.CurrentMode == dbgDebugMode.dbgDesignMode, TimeSpan.FromSeconds(10));
            }
        }
        catch
        {
            // 後始末の失敗で検証結果を失わない
        }
    }
}
