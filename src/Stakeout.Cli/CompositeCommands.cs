using System.CommandLine;
using System.Text.Json;
using Stakeout.Client;
using Stakeout.Rpc;

namespace Stakeout.Cli;

/// <summary>
/// 複合コマンド（design.md §8.4 / §9）。
/// 「調査 1 手」を 1 コマンドにする。人間向け出力では、まず結論を出し、
/// 根拠（何をしたか）はその後に置く。
/// </summary>
internal static class CompositeCommands
{
    /// <summary>Composite は内部で待つ。RPC 側のタイムアウトはそれより長く取る。</summary>
    private static readonly TimeSpan CompositeRpcTimeout = TimeSpan.FromMinutes(5);

    public static IEnumerable<Command> Build(Option<bool> jsonOption)
    {
        yield return RunUntil(jsonOption);
        yield return WatchUntilChange(jsonOption);
        yield return TraceExpr(jsonOption);
        yield return Dump(jsonOption);
        yield return TaskMap(jsonOption);
    }

    private static Command RunUntil(Option<bool> jsonOption)
    {
        var locationArgument = new Argument<string>("location") { Description = "FILE:LINE または関数名" };
        var condOption = new Option<string?>("--cond") { Description = "条件式" };
        var timeoutOption = new Option<int?>("--timeout") { Description = "待つ秒数（既定 60）" };
        var localsOption = new Option<bool>("--locals") { Description = "停止時のローカル変数も取る" };
        var noStackOption = new Option<bool>("--no-stack") { Description = "スタックを取らない" };
        var depthOption = new Option<int>("--depth") { Description = "スタックの深さ", DefaultValueFactory = _ => 20 };
        var exprOption = new Option<string[]>("--expr")
        {
            Description = "停止時に評価する式（複数指定可）",
            Arity = ArgumentArity.ZeroOrMore,
            AllowMultipleArgumentsPerToken = false,
        };

        var command = new Command("run-until", "指定位置に到達するまで実行し、その場の情報をまとめて返す")
        {
            locationArgument, condOption, timeoutOption, localsOption, noStackOption, depthOption, exprOption,
        };

        command.SetAction((parseResult, ct) =>
        {
            var seconds = parseResult.GetValue(timeoutOption);

            var request = new RunUntilRequest
            {
                Location = parseResult.GetValue(locationArgument) ?? string.Empty,
                Condition = parseResult.GetValue(condOption),
                CaptureStack = !parseResult.GetValue(noStackOption),
                CaptureLocals = parseResult.GetValue(localsOption),
                MaxDepth = parseResult.GetValue(depthOption),
                Exprs = parseResult.GetValue(exprOption),
                TimeoutMs = seconds is { } s ? s * 1000 : null,
            };

            return Rpc(parseResult, jsonOption, ct, RpcMethods.CompositeRunUntil, request, WriteRunUntil);
        });

        return command;
    }

    private static void WriteRunUntil(JsonElement data)
    {
        var reached = Output.Get(data, "reachedTarget") == "True";
        Console.WriteLine(reached ? "到達しました" : "到達していません");

        if (data.TryGetProperty("stop", out var stop) && stop.ValueKind == JsonValueKind.Object)
        {
            Console.WriteLine(
                $"stop     {Output.Get(stop, "reason")} thread={Output.Get(stop, "threadId")} " +
                $"at {Output.Get(stop, "description")}");
        }

        WriteExprs(data);
        WriteVariables(data, "locals", "locals");
        WriteStack(data, "stack");
        WriteSteps(data);
    }

    private static Command WatchUntilChange(Option<bool> jsonOption)
    {
        var exprArgument = new Argument<string>("expr") { Description = "監視する式" };
        var timeoutOption = new Option<int?>("--timeout") { Description = "待つ秒数（既定 60）" };
        var maxHitsOption = new Option<int>("--max-hits")
        {
            Description = "見る書き込みの回数",
            DefaultValueFactory = _ => 20,
        };

        var command = new Command("watch-until-change", "式の値が変わるまで、書き込みを 1 回ずつ捕まえる")
        {
            exprArgument, timeoutOption, maxHitsOption,
        };

        command.SetAction((parseResult, ct) =>
        {
            var seconds = parseResult.GetValue(timeoutOption);

            var request = new WatchUntilChangeRequest
            {
                Expr = parseResult.GetValue(exprArgument) ?? string.Empty,
                MaxHits = parseResult.GetValue(maxHitsOption),
                TimeoutMs = seconds is { } s ? s * 1000 : null,
            };

            return Rpc(parseResult, jsonOption, ct, RpcMethods.CompositeWatchUntilChange, request, WriteWatch);
        });

        return command;
    }

    private static void WriteWatch(JsonElement data)
    {
        Console.WriteLine(
            $"{Output.Get(data, "expr")} @ {Output.Get(data, "address")} " +
            $"({Output.Get(data, "size")} バイト) -> {Output.Get(data, "finalValue")}");

        Console.WriteLine(Output.Get(data, "changed") == "True" ? "値が変わりました" : "値は変わっていません");

        if (data.TryGetProperty("warning", out var warning) && warning.ValueKind == JsonValueKind.String)
        {
            Console.WriteLine($"warning  {warning.GetString()}");
        }

        if (data.TryGetProperty("hits", out var hits) && hits.ValueKind == JsonValueKind.Array)
        {
            Console.WriteLine();
            foreach (var hit in hits.EnumerateArray())
            {
                var task = Output.Get(hit, "taskName", "-");
                var file = Output.Get(hit, "file", string.Empty);
                var where = file.Length > 0 ? $" {Path.GetFileName(file)}:{Output.Get(hit, "line", "")}" : string.Empty;

                Console.WriteLine(
                    $"  thread {Output.Get(hit, "threadId"),-7} task={task,-10} " +
                    $"{Output.Get(hit, "function")}{where}");
                Console.WriteLine(
                    $"      {Output.Get(hit, "before")} -> {Output.Get(hit, "after")}");
            }
        }

        WriteSteps(data);
    }

    private static Command TraceExpr(Option<bool> jsonOption)
    {
        var exprArgument = new Argument<string[]>("expr")
        {
            Description = "各ステップで評価する式",
            Arity = ArgumentArity.OneOrMore,
        };

        var stepsOption = new Option<int>("--steps")
        {
            Description = "ステップ数（既定 50）",
            DefaultValueFactory = _ => 50,
        };

        var kindOption = new Option<string>("--kind")
        {
            Description = "over | into | out",
            DefaultValueFactory = _ => "over",
        };

        var command = new Command("trace-expr", "ステップ実行しながら式の値を並べる")
        {
            exprArgument, stepsOption, kindOption,
        };

        command.SetAction((parseResult, ct) =>
        {
            var raw = parseResult.GetValue(kindOption) ?? "over";
            if (!Enum.TryParse<StepKind>(raw, ignoreCase: true, out var kind))
            {
                Console.Error.WriteLine($"USAGE: 未知のステップ種別です: {raw}");
                return Task.FromResult(ExitCode.UsageError);
            }

            var request = new TraceExprRequest
            {
                Exprs = parseResult.GetValue(exprArgument) ?? Array.Empty<string>(),
                Steps = parseResult.GetValue(stepsOption),
                Kind = kind,
            };

            return Rpc(parseResult, jsonOption, ct, RpcMethods.CompositeTraceExpression, request, WriteTrace);
        });

        return command;
    }

    private static void WriteTrace(JsonElement data)
    {
        if (data.TryGetProperty("points", out var points) && points.ValueKind == JsonValueKind.Array)
        {
            foreach (var point in points.EnumerateArray())
            {
                var file = Output.Get(point, "file", string.Empty);
                var where = file.Length > 0 ? $"{Path.GetFileName(file)}:{Output.Get(point, "line", "")}" : string.Empty;

                var values = point.TryGetProperty("values", out var v) && v.ValueKind == JsonValueKind.Object
                    ? string.Join("  ", v.EnumerateObject().Select(p => $"{p.Name}={p.Value}"))
                    : string.Empty;

                Console.WriteLine($"{Output.Get(point, "step"),4}  {Output.Get(point, "function"),-28} {where,-24} {values}");
            }
        }

        if (Output.Get(data, "partial") == "True")
        {
            Console.WriteLine("(期限で打ち切りました)");
        }

        WriteSteps(data);
    }

    private static Command Dump(Option<bool> jsonOption)
    {
        var exprArgument = new Argument<string>("expr") { Description = "展開する式" };
        var depthOption = new Option<int>("--depth") { Description = "展開する段数", DefaultValueFactory = _ => 2 };
        var maxItemsOption = new Option<int>("--max-items")
        {
            Description = "1 段あたりの要素数の上限",
            DefaultValueFactory = _ => 50,
        };

        var frameOption = new Option<int?>("--frame") { Description = "評価するフレーム ID" };

        var cursorOption = new Option<string?>("--cursor") { Description = "打ち切られた応答の続きを取る" };

        var command = new Command("dump", "構造体やポインタを再帰的に展開する")
        {
            exprArgument, depthOption, maxItemsOption, frameOption, cursorOption,
        };

        command.SetAction((parseResult, ct) =>
        {
            var request = new DumpRequest
            {
                Expr = parseResult.GetValue(exprArgument) ?? string.Empty,
                Depth = parseResult.GetValue(depthOption),
                MaxItems = parseResult.GetValue(maxItemsOption),
                FrameId = parseResult.GetValue(frameOption),
            };

            return Rpc(parseResult, jsonOption, ct, RpcMethods.VarsDump, request, WriteDump,
                parseResult.GetValue(cursorOption));
        });

        return command;
    }

    /// <summary>
    /// 平らなノード列を depth で字下げして表示する（ADR 0014）。
    /// 木として読みたい人間のために整形するだけで、データは列のまま扱う。
    /// </summary>
    private static void WriteDump(JsonElement data)
    {
        foreach (var node in data.EnumerateArray())
        {
            var depth = int.TryParse(Output.Get(node, "depth", "0"), out var d) ? d : 0;
            var pad = new string(' ', depth * 2);

            Console.WriteLine(
                $"{pad}{Output.Get(node, "name"),-20} {Output.Get(node, "type"),-18} {Output.Get(node, "value")}");

            if (node.TryGetProperty("note", out var note) && note.ValueKind == JsonValueKind.String)
            {
                Console.WriteLine($"{pad}  ... {note.GetString()}");
            }
        }
    }

    private static Command TaskMap(Option<bool> jsonOption)
    {
        var command = new Command("task-map", "スレッドとタスク名の対応を表示する");

        command.SetAction((parseResult, ct) =>
            Rpc(parseResult, jsonOption, ct, RpcMethods.CompositeTaskMap, null, data =>
            {
                if (!data.TryGetProperty("tasks", out var tasks) || tasks.ValueKind != JsonValueKind.Array)
                {
                    return;
                }

                foreach (var task in tasks.EnumerateArray())
                {
                    var current = Output.Get(task, "isCurrent") == "True" ? ">" : " ";
                    var frozen = Output.Get(task, "isFrozen") == "True" ? " [frozen]" : string.Empty;

                    Console.WriteLine(
                        $"{current} {Output.Get(task, "threadId"),7}  {Output.Get(task, "taskName", "-"),-10} " +
                        $"{Output.Get(task, "entryFunction"),-28} {Output.Get(task, "topFunction")}{frozen}");
                }
            }));

        return command;
    }

    // ---------------------------------------------------------------- shared output

    private static void WriteExprs(JsonElement data)
    {
        if (!data.TryGetProperty("exprs", out var exprs) || exprs.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var pair in exprs.EnumerateObject())
        {
            Console.WriteLine($"  {pair.Name,-28} {pair.Value}");
        }
    }

    private static void WriteVariables(JsonElement data, string property, string label)
    {
        if (!data.TryGetProperty(property, out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        Console.WriteLine();
        Console.WriteLine(label);
        foreach (var variable in list.EnumerateArray())
        {
            Console.WriteLine(
                $"  {Output.Get(variable, "name"),-24} {Output.Get(variable, "type"),-18} " +
                $"{Output.Get(variable, "value")}");
        }
    }

    private static void WriteStack(JsonElement data, string property)
    {
        if (!data.TryGetProperty(property, out var frames) || frames.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        Console.WriteLine();
        Console.WriteLine("stack");
        foreach (var frame in frames.EnumerateArray())
        {
            var file = Output.Get(frame, "file", string.Empty);
            var where = file.Length > 0 ? $"  {Path.GetFileName(file)}:{Output.Get(frame, "line", "")}" : string.Empty;

            Console.WriteLine($"  #{Output.Get(frame, "depth"),-3} {Output.Get(frame, "function")}{where}");
        }
    }

    /// <summary>
    /// 何をしたかを最後に出す（design.md §9）。
    /// 結論より前に置くと、読む側が毎回それを読み飛ばすことになる。
    /// </summary>
    private static void WriteSteps(JsonElement data)
    {
        if (!data.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        Console.WriteLine();
        Console.WriteLine("steps");
        foreach (var step in steps.EnumerateArray())
        {
            Console.WriteLine($"  {Output.Get(step, "action"),-12} {Output.Get(step, "detail")}");
        }
    }

    private static async Task<int> Rpc(
        System.CommandLine.ParseResult parseResult,
        Option<bool> jsonOption,
        CancellationToken ct,
        string method,
        object? parameters,
        Action<JsonElement> writeText,
        string? cursor = null)
    {
        var output = new Output(parseResult.GetValue(jsonOption));
        var client = new DaemonClient(RpcTransport.PipeName);

        // 続きを取るときは、元のコマンドではなくカーソルを引き換える
        if (cursor is { Length: > 0 })
        {
            var page = await client.InvokeAsync(
                RpcMethods.CursorNext, new CursorRequest { Cursor = cursor }, false, CompositeRpcTimeout, ct);

            return output.Write(page, writeText);
        }

        var result = await client.InvokeAsync(method, parameters, autoStart: false, CompositeRpcTimeout, ct);
        return output.Write(result, writeText);
    }
}
