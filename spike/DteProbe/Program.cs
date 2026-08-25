using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace DteProbe;

/// <summary>
/// 使い捨ての検証用スパイク（ADR 0003）。製品コードから参照しないこと。
///
///   dteprobe list
///   dteprobe probe --pid &lt;target pid&gt; [--vs-pid N] [--lib-source PATH]
///                  [--trace-seconds N] [--event-rounds N] [--out FILE.json]
/// </summary>
internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        // 日本語をエスケープせずそのまま出す（結果をそのまま ADR に貼るため）
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    // COM のメッセージフィルタは STA スレッドにしか登録できない
    [STAThread]
    private static int Main(string[] args)
    {
        var command = args.FirstOrDefault();
        return command switch
        {
            "list" => CommandList(),
            "probe" => CommandProbe(args),
            "detach-test" => CommandDetachTest(args),
            _ => Usage(),
        };
    }

    private static int Usage()
    {
        Console.Error.WriteLine("""
            usage:
              dteprobe list
              dteprobe probe --pid <target pid> [options]

            options:
              --vs-pid N          使う Visual Studio の pid（複数起動しているとき必須）
              --lib-source PATH   nativelib.c のパス（既定: samples/target/NativeLib/nativelib.c を探す）
              --trace-seconds N   S4 の計測秒数（既定 10）
              --event-rounds N    S5 の break/go 回数（既定 30）
              --out FILE.json     結果 JSON の出力先
            """);
        return 2;
    }

    private static int CommandList()
    {
        var candidates = Rot.Enumerate();
        if (candidates.Count == 0)
        {
            Console.Error.WriteLine("起動中の Visual Studio が ROT に見つからない。");
            Console.Error.WriteLine("hint: VS を起動しているか、昇格レベルがこのプロセスと揃っているか確認する");
            return 5;
        }

        // どの DTE 呼び出しが通るかを切り分ける。ROT に居ても応答するとは限らない
        var filter = RetryMessageFilter.Register(retryWindowMs: 5_000);
        try
        {
            foreach (var c in candidates)
            {
                Console.WriteLine($"{c.ProgId}  pid={c.Pid}  version={c.Version}");
                Probe(c, "DTE.Version", d => ((EnvDTE80.DTE2)d).Version);
                Probe(c, "DTE.Name", d => ((EnvDTE80.DTE2)d).Name);
                Probe(c, "DTE.Mode", d => ((EnvDTE80.DTE2)d).Mode.ToString());
                Probe(c, "DTE.MainWindow.Caption", d => ((EnvDTE80.DTE2)d).MainWindow.Caption);
                Probe(c, "DTE.Debugger", d => ((EnvDTE80.DTE2)d).Debugger.CurrentMode.ToString());
            }
        }
        finally
        {
            Console.WriteLine($"  (filter: retries={filter.RetryCount} rejected={filter.RejectedCount} retryLater={filter.RetryLaterCount})");
            RetryMessageFilter.Revoke();
        }

        return 0;
    }

    private static void Probe(DteCandidate c, string label, Func<object, object?> call)
    {
        try
        {
            Console.WriteLine($"    {label,-24} = {call(c.Dte)}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    {label,-24} ! {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
        }
    }

    private static int CommandProbe(string[] args)
    {
        var targetPid = GetIntArg(args, "--pid");
        if (targetPid is null)
        {
            Console.Error.WriteLine("--pid が要る（検証対象プロセスの pid）");
            return 2;
        }

        var libSource = GetArg(args, "--lib-source") ?? FindLibSource();
        if (libSource is null || !File.Exists(libSource))
        {
            Console.Error.WriteLine($"nativelib.c が見つからない: {libSource ?? "(自動検出失敗)"}");
            return 5;
        }

        var vs = SelectVisualStudio(GetIntArg(args, "--vs-pid"));
        if (vs is null)
        {
            return 5;
        }

        var traceSeconds = GetIntArg(args, "--trace-seconds") ?? 10;
        var eventRounds = GetIntArg(args, "--event-rounds") ?? 30;

        var filter = RetryMessageFilter.Register();
        var results = new List<ProbeResult>();

        try
        {
            using var probes = new Probes(vs, filter, targetPid.Value, Path.GetFullPath(libSource));

            // 順序に意味がある。S2 でアタッチし、S3 は中断中であることを前提にする
            Emit(results, probes.S1_ConnectAndMessageFilter());
            Emit(results, probes.S2_AttachAndEvaluate());
            Emit(results, probes.S3_DataBreakpoint());
            Emit(results, probes.S4_TracepointThroughput(traceSeconds));
            Emit(results, probes.S5_EventDelivery(eventRounds));
        }
        finally
        {
            RetryMessageFilter.Revoke();
        }

        var report = new
        {
            vs = new { vs.ProgId, vs.Version, vs.Pid },
            targetPid = targetPid.Value,
            libSource = Path.GetFullPath(libSource),
            comRetries = filter.RetryCount,
            results,
        };

        var json = JsonSerializer.Serialize(report, JsonOptions);
        var outPath = GetArg(args, "--out");
        if (outPath is not null)
        {
            var full = Path.GetFullPath(outPath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, json);
            Console.WriteLine();
            Console.WriteLine($"結果を書き出した: {full}");
        }
        else
        {
            Console.WriteLine();
            Console.WriteLine(json);
        }

        // 1 つでも fail があれば非ゼロ
        return results.Any(r => r.Status == "fail") ? 1 : 0;
    }

    /// <summary>
    /// アタッチ → デタッチで Target が生き残るかだけを見る（design.md §15.3）。
    /// Attach2 が使えず Attach() しか通らない環境で、デタッチが Target を殺さないかを確認する。
    /// </summary>
    private static int CommandDetachTest(string[] args)
    {
        var targetPid = GetIntArg(args, "--pid");
        var vs = SelectVisualStudio(GetIntArg(args, "--vs-pid"));
        if (targetPid is null || vs is null)
        {
            return 2;
        }

        var filter = RetryMessageFilter.Register();
        try
        {
            var dte = (EnvDTE80.DTE2)vs.Dte;
            var stakeout = Com.Retry(() => dte.Debugger, TimeSpan.FromSeconds(60));

            EnvDTE.Process? target = null;
            foreach (EnvDTE.Process p in stakeout.LocalProcesses)
            {
                if (p.ProcessID == targetPid.Value)
                {
                    target = p;
                    break;
                }
            }

            if (target is null)
            {
                Console.Error.WriteLine($"pid {targetPid} が LocalProcesses に無い");
                return 5;
            }

            var already = false;
            foreach (EnvDTE.Process p in stakeout.DebuggedProcesses)
            {
                already |= p.ProcessID == targetPid.Value;
            }

            if (!already)
            {
                target.Attach();
            }

            Pump.Until(() => dte.Debugger.CurrentMode != EnvDTE.dbgDebugMode.dbgDesignMode, TimeSpan.FromSeconds(20));
            Console.WriteLine($"attached: mode={dte.Debugger.CurrentMode}, alive={IsAlive(targetPid.Value)}");

            // --leave-data-bp: データ BP を張ったままデタッチする。
            // ハードウェアデバッグレジスタが解除されないと Target が落ちる可能性がある
            if (args.Contains("--leave-data-bp"))
            {
                // Break() は中断中に呼ぶと 0x89711007 で失敗する。冪等ではない
                if (dte.Debugger.CurrentMode == EnvDTE.dbgDebugMode.dbgRunMode)
                {
                    dte.Debugger.Break(WaitForBreakMode: false);
                    Pump.Until(() => dte.Debugger.CurrentMode == EnvDTE.dbgDebugMode.dbgBreakMode, TimeSpan.FromSeconds(20));
                }

                // 任意のフレームで止まった状態から、どのデータ式なら通るかを試す。
                // グローバルでもフレームのスコープで解決されるため、素の式は通らないことがある
                Console.WriteLine($"stopped at: {dte.Debugger.CurrentStackFrame?.FunctionName ?? "(none)"}");

                var forms = new[]
                {
                    "&g_shared.counter",
                    "{,,NativeLib.dll}&g_shared.counter",
                    "&{,,NativeLib.dll}g_shared.counter",
                };

                EnvDTE.Breakpoints? bps = null;
                foreach (var form in forms)
                {
                    try
                    {
                        bps = dte.Debugger.Breakpoints.Add(Data: form, DataCount: 4);
                        Console.WriteLine($"  data bp OK   {form}  -> {bps?.Count ?? 0}");
                        break;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"  data bp FAIL {form}  -> 0x{ex.HResult:X8} {ex.Message}");
                    }
                }

                if (bps is null || bps.Count == 0)
                {
                    Console.WriteLine("  どの形式も通らなかった");
                }

                dte.Debugger.Go(WaitForBreakOrEnd: false);
                Pump.Until(() => dte.Debugger.CurrentMode == EnvDTE.dbgDebugMode.dbgBreakMode, TimeSpan.FromSeconds(15));
                Console.WriteLine($"after data bp hit: mode={dte.Debugger.CurrentMode}, alive={IsAlive(targetPid.Value)}");
            }

            Pump.For(TimeSpan.FromSeconds(2));

            dte.Debugger.DetachAll();
            Pump.Until(() => dte.Debugger.CurrentMode == EnvDTE.dbgDebugMode.dbgDesignMode, TimeSpan.FromSeconds(20));
            Pump.For(TimeSpan.FromSeconds(2));

            var alive = IsAlive(targetPid.Value);
            Console.WriteLine($"after DetachAll: mode={dte.Debugger.CurrentMode}, alive={alive}");
            return alive ? 0 : 1;
        }
        finally
        {
            RetryMessageFilter.Revoke();
        }
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void Emit(List<ProbeResult> sink, ProbeResult r)
    {
        sink.Add(r);
        var mark = r.Status switch
        {
            "ok" => "OK  ",
            "partial" => "PART",
            "skip" => "SKIP",
            _ => "FAIL",
        };
        Console.WriteLine($"[{mark}] {r.Id} {r.Title}: {r.Summary}");
    }

    private static DteCandidate? SelectVisualStudio(int? requestedPid)
    {
        var candidates = Rot.Enumerate();
        if (candidates.Count == 0)
        {
            Console.Error.WriteLine("起動中の Visual Studio が ROT に見つからない");
            return null;
        }

        if (requestedPid is not null)
        {
            var picked = candidates.FirstOrDefault(c => c.Pid == requestedPid.Value);
            if (picked is null)
            {
                Console.Error.WriteLine($"--vs-pid {requestedPid} に一致する VS が無い。候補:");
                foreach (var c in candidates)
                {
                    Console.Error.WriteLine($"  {c.ProgId} pid={c.Pid}");
                }
            }

            return picked;
        }

        if (candidates.Count > 1)
        {
            // ADR 0002: 曖昧なまま繋がず、失敗させる
            Console.Error.WriteLine("Visual Studio が複数起動している。--vs-pid で指定すること。候補:");
            foreach (var c in candidates)
            {
                Console.Error.WriteLine($"  {c.ProgId} pid={c.Pid}");
            }

            return null;
        }

        return candidates[0];
    }

    /// <summary>実行ファイルの位置から上に辿って samples/target/NativeLib/nativelib.c を探す。</summary>
    private static string? FindLibSource()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
        {
            var candidate = Path.Combine(dir, "samples", "target", "NativeLib", "nativelib.c");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }

        return null;
    }

    private static string? GetArg(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static int? GetIntArg(string[] args, string name)
        => int.TryParse(GetArg(args, name), out var v) ? v : null;
}
