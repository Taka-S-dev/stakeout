using System.CommandLine;
using System.Text.Json;
using Stakeout.Client;
using Stakeout.Rpc;

namespace Stakeout.Cli;

/// <summary>
/// デバッグ操作のコマンド群（design.md §8.4）。
/// 1 コマンド = 原則 1 RPC。人間向けの整形は各コマンドが持ち、
/// <c>--json</c> のときは RPC の data をそのまま出す。
/// </summary>
internal static class DebugCommands
{
    private static readonly TimeSpan RpcTimeout = TimeSpan.FromSeconds(30);

    /// <summary>待ち系は RPC のタイムアウトより長く待つ必要がある。</summary>
    private static readonly TimeSpan WaitRpcTimeout = TimeSpan.FromSeconds(120);

    public static IEnumerable<Command> Build(Option<bool> jsonOption)
    {
        yield return Targets(jsonOption);
        yield return Attach(jsonOption);
        yield return Detach(jsonOption);
        yield return Simple(jsonOption, "continue", "実行を再開する", RpcMethods.ExecContinue, "再開しました");
        yield return Simple(jsonOption, "pause", "実行を中断する", RpcMethods.ExecPause, "中断しました");
        yield return Step(jsonOption);
        yield return Wait(jsonOption);
        yield return Threads(jsonOption);
        yield return Thread(jsonOption);
        yield return Stack(jsonOption);
        yield return Locals(jsonOption);
        yield return Eval(jsonOption);
        yield return Expand(jsonOption);
        yield return Mem(jsonOption);
        yield return Breakpoints(jsonOption);
    }

    // ---------------------------------------------------------------- session

    private static Command Targets(Option<bool> jsonOption)
    {
        var allOption = new Option<bool>("--all") { Description = "allowlist 外のプロセスも表示する" };
        var command = new Command("targets", "アタッチ可能なプロセスを一覧する") { allOption };

        // targets はセッションを必要としないので、デーモンが無ければ起こしてよい
        command.SetAction((parseResult, ct) => Run(parseResult, jsonOption, ct,
            RpcMethods.TargetList,
            parseResult.GetValue(allOption) ? (object)true : false,
            data =>
            {
                foreach (var target in data.EnumerateArray())
                {
                    var allowed = Output.Get(target, "allowed") == "True" ? " " : "*";
                    Console.WriteLine($"{allowed} {Output.Get(target, "pid"),8}  {Output.Get(target, "name")}");
                }

                Console.WriteLine();
                Console.WriteLine("* は allowlist 外（アタッチできません）");
            },
            autoStart: true));

        return command;
    }

    private static Command Attach(Option<bool> jsonOption)
    {
        var pidOption = new Option<int?>("--pid") { Description = "アタッチするプロセス ID" };
        var nameOption = new Option<string?>("--name") { Description = "プロセス名の正規表現" };
        var vsPidOption = new Option<int?>("--vs-pid") { Description = "使う Visual Studio の pid" };
        var backendOption = new Option<string?>("--backend") { Description = "envdte" };
        var enginesOption = new Option<string?>("--engines")
        {
            Description = "デバッグエンジンを明示する（カンマ区切り）。指定して繋げなければ失敗します",
        };

        var command = new Command("attach", "実行中のプロセスにアタッチする")
        {
            pidOption, nameOption, vsPidOption, backendOption, enginesOption,
        };

        command.SetAction((parseResult, ct) =>
        {
            var engines = parseResult.GetValue(enginesOption);

            var request = new AttachRequest
            {
                Pid = parseResult.GetValue(pidOption),
                Name = parseResult.GetValue(nameOption),
                VsPid = parseResult.GetValue(vsPidOption),
                Backend = parseResult.GetValue(backendOption),
                Engines = string.IsNullOrWhiteSpace(engines)
                    ? null
                    : engines.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            };

            return Run(parseResult, jsonOption, ct, RpcMethods.SessionAttach, request, data =>
                Console.WriteLine(
                    $"attached {Output.Get(data, "processName")} (pid {Output.Get(data, "pid")}) " +
                    $"backend={Output.Get(data, "backend")} state={Output.Get(data, "state")}"));
        });

        return command;
    }

    private static Command Detach(Option<bool> jsonOption)
    {
        var command = new Command("detach", "デタッチする（Target は生かしたまま）");

        command.SetAction((parseResult, ct) => Run(parseResult, jsonOption, ct,
            RpcMethods.SessionDetach, null, _ => Console.WriteLine("detached")));

        return command;
    }

    private static Command Simple(
        Option<bool> jsonOption, string name, string description, string method, string message)
    {
        var command = new Command(name, description);

        command.SetAction((parseResult, ct) =>
            Run(parseResult, jsonOption, ct, method, null, _ => Console.WriteLine(message)));

        return command;
    }

    // ---------------------------------------------------------------- execution

    private static Command Step(Option<bool> jsonOption)
    {
        var kindArgument = new Argument<string>("kind")
        {
            Description = "over | into | out",
            DefaultValueFactory = _ => "over",
        };

        var threadOption = new Option<int?>("--thread") { Description = "対象スレッド ID" };
        var command = new Command("step", "1 行ステップ実行する") { kindArgument, threadOption };

        command.SetAction((parseResult, ct) =>
        {
            var raw = parseResult.GetValue(kindArgument) ?? "over";
            if (!Enum.TryParse<StepKind>(raw, ignoreCase: true, out var kind))
            {
                Console.Error.WriteLine($"USAGE: 未知のステップ種別です: {raw}");
                Console.Error.WriteLine("hint: over / into / out のいずれかを指定してください。");
                return Task.FromResult(ExitCode.UsageError);
            }

            var request = new StepRequest { Kind = kind, ThreadId = parseResult.GetValue(threadOption) };

            return Run(parseResult, jsonOption, ct, RpcMethods.ExecStep, request,
                _ => Console.WriteLine($"stepped ({kind})"));
        });

        return command;
    }

    private static Command Wait(Option<bool> jsonOption)
    {
        var timeoutOption = new Option<int?>("--timeout") { Description = "待つ秒数（既定 20）" };
        var command = new Command("wait", "停止するまで待つ。停止しなければ exit 3") { timeoutOption };

        command.SetAction(async (parseResult, ct) =>
        {
            var seconds = parseResult.GetValue(timeoutOption);
            var request = new WaitRequest { TimeoutMs = seconds is { } s ? s * 1000 : null };

            var output = new Output(parseResult.GetValue(jsonOption));
            var client = new DaemonClient(RpcTransport.PipeName);

            var result = await client.InvokeAsync(
                RpcMethods.ExecWait, request, autoStart: false, WaitRpcTimeout, ct);

            if (!result.Ok)
            {
                return output.WriteError(result.Error!);
            }

            var stopped = result.Data.TryGetProperty("stopped", out var s2) && s2.GetBoolean();

            if (output.Json)
            {
                output.WriteSuccess(result.Data, _ => { });
            }
            else if (stopped && result.Data.TryGetProperty("stop", out var stop))
            {
                Console.WriteLine(
                    $"stopped: {Output.Get(stop, "reason")} thread={Output.Get(stop, "threadId")} " +
                    $"at {Output.Get(stop, "description")}");
            }
            else
            {
                Console.WriteLine("まだ実行中です");
            }

            // 停止しなかったことは実行時エラーではない。エージェントは再度 wait する
            return stopped ? ExitCode.Ok : ExitCode.Timeout;
        });

        return command;
    }

    // ---------------------------------------------------------------- threads

    private static Command Threads(Option<bool> jsonOption)
    {
        var cursorOption = CursorOption();
        var command = new Command("threads", "全スレッドを一覧する") { cursorOption };

        command.SetAction((parseResult, ct) => Run(parseResult, jsonOption, ct,
            RpcMethods.ThreadsList, null,
            data =>
            {
                foreach (var thread in data.EnumerateArray())
                {
                    var current = Output.Get(thread, "isCurrent") == "True" ? ">" : " ";
                    var frozen = Output.Get(thread, "isFrozen") == "True" ? " [frozen]" : string.Empty;

                    Console.WriteLine(
                        $"{current} {Output.Get(thread, "threadId"),6}  " +
                        $"{Output.Get(thread, "name", ""),-20} {Output.Get(thread, "topFunction")}{frozen}");
                }
            },
            cursor: parseResult.GetValue(cursorOption)));

        return command;
    }

    private static Command Thread(Option<bool> jsonOption)
    {
        // スレッド ID かタスク名のどちらでも指定できる（design.md §9.6）。
        // 調査中に覚えていられるのはタスク名のほうである
        var idArgument = new Argument<int?>("id")
        {
            Description = "スレッド ID",
            Arity = ArgumentArity.ZeroOrOne,
        };

        var taskOption = new Option<string?>("--task") { Description = "タスク名で指定する" };

        var select = new Command("select", "現在のスレッドを変える") { idArgument, taskOption };
        select.SetAction((parseResult, ct) => Run(parseResult, jsonOption, ct,
            RpcMethods.ThreadsSelect,
            new ThreadTargetRequest
            {
                ThreadId = parseResult.GetValue(idArgument),
                TaskName = parseResult.GetValue(taskOption),
            },
            data => Console.WriteLine($"current thread = {Output.Get(data, "threadId")}")));

        var thawOption = new Option<bool>("--thaw") { Description = "凍結を解除する" };
        var freeze = new Command("freeze", "スレッドを凍結する") { idArgument, taskOption, thawOption };
        freeze.SetAction((parseResult, ct) => Run(parseResult, jsonOption, ct,
            RpcMethods.ThreadsFreeze,
            new ThreadTargetRequest
            {
                ThreadId = parseResult.GetValue(idArgument),
                TaskName = parseResult.GetValue(taskOption),
                Freeze = !parseResult.GetValue(thawOption),
            },
            data => Console.WriteLine(
                $"thread {Output.Get(data, "threadId")} frozen={Output.Get(data, "frozen")}")));

        var command = new Command("thread", "スレッド操作") { select, freeze };
        command.SetAction(_ =>
        {
            Console.Error.WriteLine("stakeout thread (select|freeze) を指定してください。");
            return ExitCode.UsageError;
        });

        return command;
    }

    // ---------------------------------------------------------------- stack and variables

    private static Command Stack(Option<bool> jsonOption)
    {
        var threadOption = new Option<int?>("--thread") { Description = "対象スレッド ID" };
        var allOption = new Option<bool>("--all") { Description = "全スレッドのスタックを取る（既定の深さ 5）" };
        var depthOption = new Option<int?>("--depth") { Description = "取得する深さ" };

        var cursorOption = CursorOption();
        var command = new Command("stack", "コールスタックを表示する")
        {
            threadOption, allOption, depthOption, cursorOption,
        };

        command.SetAction((parseResult, ct) => Run(parseResult, jsonOption, ct,
            RpcMethods.StackGet,
            new StackRequest
            {
                ThreadId = parseResult.GetValue(threadOption),
                All = parseResult.GetValue(allOption),
                MaxDepth = parseResult.GetValue(depthOption),
            },
            data =>
            {
                foreach (var thread in data.EnumerateArray())
                {
                    Console.WriteLine($"thread {Output.Get(thread, "threadId")} {Output.Get(thread, "threadName", "")}");

                    if (!thread.TryGetProperty("frames", out var frames))
                    {
                        continue;
                    }

                    foreach (var frame in frames.EnumerateArray())
                    {
                        var external = Output.Get(frame, "isExternal") == "True" ? " [external]" : string.Empty;
                        var file = Output.Get(frame, "file", string.Empty);
                        var line = Output.Get(frame, "line", string.Empty);
                        var where = file.Length > 0 ? $"  {Path.GetFileName(file)}:{line}" : string.Empty;

                        Console.WriteLine(
                            $"  #{Output.Get(frame, "depth"),-3} {Output.Get(frame, "frameId"),4}  " +
                            $"{Output.Get(frame, "function")}{where}{external}");
                    }

                    if (Output.Get(thread, "truncated") == "True")
                    {
                        Console.WriteLine("  ... (--depth で深さを増やせます)");
                    }
                }
            },
            cursor: parseResult.GetValue(cursorOption)));

        return command;
    }

    private static Command Locals(Option<bool> jsonOption)
    {
        var frameOption = new Option<int?>("--frame") { Description = "フレーム ID（stakeout stack で確認）" };
        var argsOption = new Option<bool>("--args") { Description = "ローカル変数ではなく引数を表示する" };

        var cursorOption = CursorOption();
        var command = new Command("locals", "ローカル変数を表示する") { frameOption, argsOption, cursorOption };

        command.SetAction((parseResult, ct) => Run(parseResult, jsonOption, ct,
            RpcMethods.VarsScope,
            new ScopeRequest
            {
                FrameId = parseResult.GetValue(frameOption) ?? 1,
                Kind = parseResult.GetValue(argsOption) ? ScopeKind.Arguments : ScopeKind.Locals,
            },
            WriteVariables,
            cursor: parseResult.GetValue(cursorOption)));

        return command;
    }

    private static Command Eval(Option<bool> jsonOption)
    {
        var exprArgument = new Argument<string>("expr") { Description = "評価する C/C++ 式" };
        var frameOption = new Option<int?>("--frame") { Description = "評価するフレーム ID" };
        var formatOption = new Option<string?>("--format") { Description = "VS の書式指定子 (x, d, s ...)" };

        var command = new Command("eval", "式を評価する") { exprArgument, frameOption, formatOption };

        command.SetAction((parseResult, ct) => Run(parseResult, jsonOption, ct,
            RpcMethods.VarsEval,
            new EvalRequest
            {
                Expr = parseResult.GetValue(exprArgument) ?? string.Empty,
                FrameId = parseResult.GetValue(frameOption),
                Format = parseResult.GetValue(formatOption),
            },
            data => WriteVariable(data)));

        return command;
    }

    private static Command Expand(Option<bool> jsonOption)
    {
        var refArgument = new Argument<int>("ref") { Description = "変数参照番号（stakeout eval / locals の出力）" };
        var command = new Command("expand", "構造体やポインタの子要素を表示する") { refArgument };

        command.SetAction((parseResult, ct) => Run(parseResult, jsonOption, ct,
            RpcMethods.VarsExpand,
            new ExpandRequest { Ref = parseResult.GetValue(refArgument) },
            WriteVariables));

        return command;
    }

    private static Command Mem(Option<bool> jsonOption)
    {
        var addressArgument = new Argument<string>("address")
        {
            Description = "アドレス（0x...）またはアドレスになる式（&g_ctx, p->buffer）",
        };

        var lengthOption = new Option<int>("--length", "-n")
        {
            Description = "読むバイト数",
            DefaultValueFactory = _ => 64,
        };

        var frameOption = new Option<int?>("--frame") { Description = "式を評価するフレーム ID" };

        var command = new Command("mem", "メモリを読む")
        {
            addressArgument, lengthOption, frameOption,
        };

        command.SetAction((parseResult, ct) => Run(parseResult, jsonOption, ct,
            RpcMethods.MemRead,
            new MemReadRequest
            {
                Address = parseResult.GetValue(addressArgument) ?? string.Empty,
                Length = parseResult.GetValue(lengthOption),
                FrameId = parseResult.GetValue(frameOption),
            },
            WriteMemory));

        return command;
    }

    /// <summary>1 行 16 バイトの hexdump にして出す。</summary>
    private static void WriteMemory(JsonElement data)
    {
        var address = ulong.Parse(Output.Get(data, "address", "0"));
        var requested = int.Parse(Output.Get(data, "requested", "0"));
        var length = int.Parse(Output.Get(data, "length", "0"));

        var bytes = Output.Get(data, "hex").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var ascii = Output.Get(data, "ascii");

        for (var offset = 0; offset < bytes.Length; offset += 16)
        {
            var row = bytes.Skip(offset).Take(16).ToArray();
            var text = ascii.Length >= offset ? ascii[offset..Math.Min(offset + 16, ascii.Length)] : string.Empty;

            Console.WriteLine($"{address + (ulong)offset:x16}  {string.Join(' ', row).PadRight(47)}  {text}");
        }

        if (length < requested)
        {
            // **0 で埋めていないことを言う。** 短い応答を「全部読めた」と読まれると、
            // 未マップの領域をゼロ埋めされた領域と取り違える
            Console.Error.WriteLine(
                $"{requested} バイト要求しましたが、{length} バイトで読めなくなりました。" +
                $"0x{address + (ulong)length:x} から先は読めません。");
        }
    }

    private static Option<string?> CursorOption() =>
        new("--cursor") { Description = "打ち切られた応答の続きを取る" };

    private static void WriteVariables(JsonElement data)
    {
        foreach (var variable in data.EnumerateArray())
        {
            WriteVariable(variable);
        }
    }

    private static void WriteVariable(JsonElement variable)
    {
        var reference = Output.Get(variable, "variablesReference", "0");
        var expandable = reference == "0" ? string.Empty : $"  (expand {reference})";

        Console.WriteLine(
            $"{Output.Get(variable, "name"),-24} {Output.Get(variable, "type"),-20} " +
            $"{Output.Get(variable, "value")}{expandable}");
    }

    // ---------------------------------------------------------------- breakpoints

    private static Command Breakpoints(Option<bool> jsonOption)
    {
        var command = new Command("bp", "ブレークポイントの管理")
        {
            BreakpointSet(jsonOption),
            BreakpointList(jsonOption),
            BreakpointRemove(jsonOption),
            BreakpointClear(jsonOption),
            BreakpointExceptions(jsonOption),
        };

        command.SetAction(_ =>
        {
            Console.Error.WriteLine("stakeout bp (set|list|rm|clear|exceptions) を指定してください。");
            return ExitCode.UsageError;
        });

        return command;
    }

    private static Command BreakpointSet(Option<bool> jsonOption)
    {
        var locationArgument = new Argument<string?>("location")
        {
            Description = "FILE:LINE",
            Arity = ArgumentArity.ZeroOrOne,
        };

        var funcOption = new Option<string?>("--func") { Description = "関数名で張る" };
        var addrOption = new Option<string?>("--addr") { Description = "アドレスで張る" };
        var dataOption = new Option<string?>("--data")
        {
            Description = "データブレークポイント。式は {,,モジュール名}&式 の形にしてください",
        };

        var sizeOption = new Option<int>("--size")
        {
            Description = "データブレークポイントで監視するバイト数",
            DefaultValueFactory = _ => 4,
        };

        var condOption = new Option<string?>("--cond") { Description = "条件式" };

        var command = new Command("set", "ブレークポイントを張る")
        {
            locationArgument, funcOption, addrOption, dataOption, sizeOption, condOption,
        };

        command.SetAction((parseResult, ct) =>
        {
            var location = parseResult.GetValue(locationArgument);
            var func = parseResult.GetValue(funcOption);
            var addr = parseResult.GetValue(addrOption);
            var data = parseResult.GetValue(dataOption);

            var (kind, target) = (location, func, addr, data) switch
            {
                (not null, _, _, _) => (BreakpointKind.Line, location),
                (_, not null, _, _) => (BreakpointKind.Function, func),
                (_, _, not null, _) => (BreakpointKind.Address, addr),
                (_, _, _, not null) => (BreakpointKind.Data, data),
                _ => (BreakpointKind.Line, null),
            };

            if (target is null)
            {
                Console.Error.WriteLine("USAGE: 位置を指定してください。");
                Console.Error.WriteLine("hint: stakeout bp set FILE:LINE / --func NAME / --addr HEX / --data EXPR");
                return Task.FromResult(ExitCode.UsageError);
            }

            var request = new BreakpointSetRequest
            {
                Kind = kind,
                Location = target,
                Condition = parseResult.GetValue(condOption),
                DataSize = parseResult.GetValue(sizeOption),
            };

            return Run(parseResult, jsonOption, ct, RpcMethods.BreakpointSet, request, WriteBreakpoint);
        });

        return command;
    }

    private static Command BreakpointList(Option<bool> jsonOption)
    {
        var command = new Command("list", "張っているブレークポイントを一覧する");

        command.SetAction((parseResult, ct) => Run(parseResult, jsonOption, ct,
            RpcMethods.BreakpointList, null,
            data =>
            {
                foreach (var bp in data.EnumerateArray())
                {
                    WriteBreakpoint(bp);
                }
            }));

        return command;
    }

    private static Command BreakpointRemove(Option<bool> jsonOption)
    {
        var idArgument = new Argument<int>("id") { Description = "ブレークポイント ID" };
        var command = new Command("rm", "ブレークポイントを削除する") { idArgument };

        command.SetAction((parseResult, ct) => Run(parseResult, jsonOption, ct,
            RpcMethods.BreakpointRemove,
            new BreakpointRemoveRequest { Id = parseResult.GetValue(idArgument) },
            data => Console.WriteLine($"removed {Output.Get(data, "removed")}")));

        return command;
    }

    private static Command BreakpointClear(Option<bool> jsonOption)
    {
        var command = new Command("clear", "stakeout が張ったブレークポイントを全部削除する");

        command.SetAction((parseResult, ct) => Run(parseResult, jsonOption, ct,
            RpcMethods.BreakpointClear, null,
            data => Console.WriteLine($"removed {Output.Get(data, "removed")}")));

        return command;
    }

    private static Command BreakpointExceptions(Option<bool> jsonOption)
    {
        var onOption = new Option<bool>("--on") { Description = "発生時に停止する" };
        var offOption = new Option<bool>("--off") { Description = "停止しない" };
        var codesOption = new Option<string>("--codes")
        {
            Description = "例外コード（カンマ区切り）",
            DefaultValueFactory = _ => "C0000005,C0000374",
        };

        var command = new Command("exceptions", "例外で停止するかを設定する") { onOption, offOption, codesOption };

        command.SetAction((parseResult, ct) =>
        {
            var on = parseResult.GetValue(onOption);
            var off = parseResult.GetValue(offOption);

            if (on == off)
            {
                Console.Error.WriteLine("USAGE: --on か --off のどちらか一方を指定してください。");
                return Task.FromResult(ExitCode.UsageError);
            }

            var request = new ExceptionBreakRequest
            {
                Codes = (parseResult.GetValue(codesOption) ?? string.Empty)
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                BreakWhenThrown = on,
            };

            return Run(parseResult, jsonOption, ct, RpcMethods.BreakpointExceptions, request,
                data => Console.WriteLine(
                    $"exception break = {Output.Get(data, "breakWhenThrown")}"));
        });

        return command;
    }

    private static void WriteBreakpoint(JsonElement bp) =>
        Console.WriteLine(
            $"#{Output.Get(bp, "breakpointId"),-3} {Output.Get(bp, "kind"),-9} " +
            $"{Output.Get(bp, "location"),-40} hits={Output.Get(bp, "hitCount", "0")} " +
            $"{Output.Get(bp, "condition", "")}");

    // ---------------------------------------------------------------- plumbing

    private static async Task<int> Run(
        System.CommandLine.ParseResult parseResult,
        Option<bool> jsonOption,
        CancellationToken ct,
        string method,
        object? parameters,
        Action<JsonElement> writeText,
        bool autoStart = false,
        string? cursor = null)
    {
        var output = new Output(parseResult.GetValue(jsonOption));
        var client = new DaemonClient(RpcTransport.PipeName);

        // 続きを取るときは、元のコマンドではなくカーソルを引き換える
        if (cursor is { Length: > 0 })
        {
            var page = await client.InvokeAsync(
                RpcMethods.CursorNext, new CursorRequest { Cursor = cursor }, autoStart, RpcTimeout, ct);

            return output.Write(page, writeText);
        }

        // デバッグ操作はセッションを前提にする。デーモンを勝手に起こしても
        // アタッチされていないので、既定では自動起動しない
        var result = await client.InvokeAsync(method, parameters, autoStart, RpcTimeout, ct);
        return output.Write(result, writeText);
    }
}
