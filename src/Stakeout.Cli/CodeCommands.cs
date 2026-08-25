using System.CommandLine;
using System.Text.Json;
using Stakeout.Client;
using Stakeout.Rpc;

namespace Stakeout.Cli;

/// <summary>
/// 静的索引と、それを使う調査（design.md §8.4 / §13 / §9.5）。
///
/// `code *` はデバッガに依存しない。Target が動いていなくても使える。
/// 動的な調査の**前**に候補を絞るためのものである。
/// </summary>
internal static class CodeCommands
{
    private static readonly TimeSpan RpcTimeout = TimeSpan.FromSeconds(60);

    /// <summary>find-corruption は内部で待つ。RPC 側はそれより長く取る。</summary>
    private static readonly TimeSpan CompositeRpcTimeout = TimeSpan.FromMinutes(5);

    public static IEnumerable<Command> Build(Option<bool> jsonOption)
    {
        yield return Code(jsonOption);
        yield return FindCorruption(jsonOption);
        yield return Log(jsonOption);
        yield return Doctor(jsonOption);
    }

    /// <summary>
    /// design.md §3.2 の確認項目に答える。
    /// 実機で 1 コマンド打てば、あの表が埋まる形にしてある。
    /// </summary>
    private static Command Doctor(Option<bool> jsonOption)
    {
        var command = new Command("doctor", "この環境で何が分かっていて、何が分かっていないかを出す");

        command.SetAction((parseResult, ct) => Run(parseResult, jsonOption, ct,
            RpcMethods.DaemonDoctor, null, WriteDoctor, RpcTimeout));

        return command;
    }

    private static void WriteDoctor(JsonElement data)
    {
        var target = Output.Get(data, "targetName", string.Empty);
        Console.WriteLine(target.Length > 0
            ? $"target   {target} (pid {Output.Get(data, "targetPid")})"
            : "target   （アタッチしていません）");

        if (!data.TryGetProperty("findings", out var findings) || findings.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var unresolved = 0;

        foreach (var finding in findings.EnumerateArray())
        {
            var status = Output.Get(finding, "status");
            var mark = status switch
            {
                "Answered" => "OK  ",
                "Caution" => "注意",
                "Unknown" => "不明",
                _ => "未確認",
            };

            if (status != "Answered")
            {
                unresolved++;
            }

            Console.WriteLine();
            Console.WriteLine($"[{mark}] {Output.Get(finding, "id")} {Output.Get(finding, "question")}");
            Console.WriteLine($"       {Output.Get(finding, "answer")}");

            if (finding.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String)
            {
                Console.WriteLine($"       {detail.GetString()}");
            }

            if (finding.TryGetProperty("nextStep", out var next) && next.ValueKind == JsonValueKind.String)
            {
                Console.WriteLine($"       -> {next.GetString()}");
            }
        }

        Console.WriteLine();
        Console.WriteLine(unresolved == 0
            ? "すべて答えが出ました。"
            : $"{unresolved} 件は答えが出ていません。上の -> に従ってください。");
    }

    /// <summary>
    /// セッションログを読む（design.md §14）。
    /// 調査の根拠として「どのコマンドが何を返したか」を引くために要る。
    /// </summary>
    private static Command Log(Option<bool> jsonOption)
    {
        var countOption = new Option<int>("-n")
        {
            Description = "末尾から読む行数",
            DefaultValueFactory = _ => 50,
        };

        var kindOption = new Option<string?>("--kind")
        {
            Description = "種別で絞る（rpc.request / rpc.response / stop / trace / daemon）",
        };

        var tail = new Command("tail", "セッションログの末尾を表示する") { countOption, kindOption };
        tail.SetAction((parseResult, ct) => Run(parseResult, jsonOption, ct,
            RpcMethods.LogTail,
            new LogTailRequest
            {
                Count = parseResult.GetValue(countOption),
                Kind = parseResult.GetValue(kindOption),
            },
            data =>
            {
                foreach (var line in data.EnumerateArray())
                {
                    Console.WriteLine(line.GetString());
                }
            }));

        var command = new Command("log", "セッションログ") { tail };
        command.SetAction(_ =>
        {
            Console.Error.WriteLine("stakeout log tail を指定してください。");
            return ExitCode.UsageError;
        });

        return command;
    }

    private static Command Code(Option<bool> jsonOption)
    {
        var symbolArgument = new Argument<string>("symbol") { Description = "シンボル名または式" };

        var def = new Command("def", "定義を探す") { symbolArgument };
        def.SetAction((parseResult, ct) => Run(parseResult, jsonOption, ct,
            RpcMethods.CodeDefinitions,
            new CodeQueryRequest { Symbol = parseResult.GetValue(symbolArgument) ?? string.Empty },
            WriteLocations));

        var refs = new Command("refs", "参照を探す") { symbolArgument };
        refs.SetAction((parseResult, ct) => Run(parseResult, jsonOption, ct,
            RpcMethods.CodeReferences,
            new CodeQueryRequest { Symbol = parseResult.GetValue(symbolArgument) ?? string.Empty },
            WriteLocations));

        var writers = new Command("writers", "そのシンボルに書いていそうな場所を探す") { symbolArgument };
        writers.SetAction((parseResult, ct) => Run(parseResult, jsonOption, ct,
            RpcMethods.CodeWriters,
            new CodeQueryRequest { Symbol = parseResult.GetValue(symbolArgument) ?? string.Empty },
            WriteWriters));

        var command = new Command("code", "静的な索引を引く（gtags）") { def, refs, writers };
        command.SetAction(_ =>
        {
            Console.Error.WriteLine("stakeout code (def|refs|writers) を指定してください。");
            return ExitCode.UsageError;
        });

        return command;
    }

    private static void WriteLocations(JsonElement data)
    {
        foreach (var location in data.EnumerateArray())
        {
            var kind = Output.Get(location, "kind");
            var note = kind == "TextSearch" ? "  [テキスト検索]" : string.Empty;

            Console.WriteLine(
                $"{Output.Get(location, "file")}:{Output.Get(location, "line"),-5} " +
                $"{Output.Get(location, "text").Trim()}{note}");
        }
    }

    private static void WriteWriters(JsonElement data)
    {
        foreach (var site in data.EnumerateArray())
        {
            Console.WriteLine(
                $"[{Output.Get(site, "confidence"),-4}] {Output.Get(site, "file")}:{Output.Get(site, "line"),-5} " +
                $"{Output.Get(site, "function", "-"),-20} {Output.Get(site, "text")}");
            Console.WriteLine($"         {Output.Get(site, "reason")}");
        }

        Console.WriteLine();
        Console.WriteLine("これは候補です。実際に書いているかは stakeout find-corruption で確かめてください。");
    }

    private static Command FindCorruption(Option<bool> jsonOption)
    {
        var symbolArgument = new Argument<string>("symbol") { Description = "監視する式" };
        var maxHitsOption = new Option<int>("--max-hits")
        {
            Description = "捕まえる書き込みの回数",
            DefaultValueFactory = _ => 10,
        };

        var timeoutOption = new Option<int?>("--timeout") { Description = "待つ秒数（既定 60）" };

        var command = new Command("find-corruption", "静的な候補に無い書き込みを見つける")
        {
            symbolArgument, maxHitsOption, timeoutOption,
        };

        command.SetAction((parseResult, ct) =>
        {
            var seconds = parseResult.GetValue(timeoutOption);

            var request = new FindCorruptionRequest
            {
                Symbol = parseResult.GetValue(symbolArgument) ?? string.Empty,
                MaxHits = parseResult.GetValue(maxHitsOption),
                TimeoutMs = seconds is { } s ? s * 1000 : null,
            };

            return Run(parseResult, jsonOption, ct,
                RpcMethods.CompositeFindCorruption, request, WriteCorruption, CompositeRpcTimeout);
        });

        return command;
    }

    private static void WriteCorruption(JsonElement data)
    {
        Console.WriteLine(
            $"{Output.Get(data, "symbol")} @ {Output.Get(data, "address")} ({Output.Get(data, "size")} バイト)");

        if (Output.Get(data, "indexAvailable") != "True")
        {
            Console.WriteLine("Code Index が未設定のため、想定内かどうかは判定できません。");
        }

        if (data.TryGetProperty("warning", out var warning) && warning.ValueKind == JsonValueKind.String)
        {
            Console.WriteLine($"warning  {warning.GetString()}");
        }

        if (data.TryGetProperty("hits", out var hits) && hits.ValueKind == JsonValueKind.Array)
        {
            // 想定外を先に出す。これが探しているものである
            var ordered = hits.EnumerateArray()
                .OrderBy(h => Output.Get(h, "expected") == "True")
                .ToArray();

            Console.WriteLine();
            foreach (var hit in ordered)
            {
                var mark = Output.Get(hit, "expected") == "True" ? "  " : "!!";
                var file = Output.Get(hit, "file", string.Empty);
                var where = file.Length > 0 ? $" {Path.GetFileName(file)}:{Output.Get(hit, "line", "")}" : string.Empty;

                Console.WriteLine(
                    $"{mark} {Output.Get(hit, "function")}{where} " +
                    $"task={Output.Get(hit, "taskName", "-")} thread={Output.Get(hit, "threadId")}");
                Console.WriteLine($"     {Output.Get(hit, "before")} -> {Output.Get(hit, "after")}");
                Console.WriteLine($"     {Output.Get(hit, "note")}");
            }

            Console.WriteLine();
            Console.WriteLine("!! が付いた行が、静的な候補に無い書き込みです。");
        }

        if (data.TryGetProperty("staticWriters", out var writers) && writers.ValueKind == JsonValueKind.Array)
        {
            Console.WriteLine();
            Console.WriteLine("静的な候補");
            foreach (var site in writers.EnumerateArray())
            {
                Console.WriteLine(
                    $"  [{Output.Get(site, "confidence"),-4}] {Output.Get(site, "file")}:{Output.Get(site, "line")} " +
                    $"{Output.Get(site, "function", "-")}");
            }
        }
    }

    private static async Task<int> Run(
        System.CommandLine.ParseResult parseResult,
        Option<bool> jsonOption,
        CancellationToken ct,
        string method,
        object? parameters,
        Action<JsonElement> writeText,
        TimeSpan? timeout = null)
    {
        var output = new Output(parseResult.GetValue(jsonOption));
        var client = new DaemonClient(RpcTransport.PipeName);

        // code * はセッションを必要としない。デーモンが無ければ起こしてよい
        var needsSession = method == RpcMethods.CompositeFindCorruption;

        var result = await client.InvokeAsync(
            method, parameters, autoStart: !needsSession, timeout ?? RpcTimeout, ct);

        return output.Write(result, writeText);
    }
}
