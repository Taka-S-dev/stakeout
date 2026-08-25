using System.CommandLine;
using System.Diagnostics;
using System.Text.Json;
using Stakeout.Client;
using Stakeout.Rpc;

namespace Stakeout.Cli;

/// <summary>
/// stakeout.exe（design.md §8）。1 コマンド = 原則 1 RPC。
/// Phase 0 で実装するのは status と daemon 系だけ（design.md §20）。
/// </summary>
internal static class Program
{
    /// <summary>RPC 全体の既定タイムアウト（design.md §15.2）。</summary>
    private static readonly TimeSpan RpcTimeout = TimeSpan.FromSeconds(30);

    private static async Task<int> Main(string[] args)
    {
        // 日本語の hint やメッセージが、リダイレクト先でも化けないようにする
        try
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
        }
        catch (IOException)
        {
            // コンソールが無い環境（リダイレクト先がパイプのみ等）では設定できないことがある
        }

        var jsonOption = new Option<bool>("--json")
        {
            Description = "RPC の data をそのまま JSON で出力する",
            Recursive = true,
        };

        var root = new RootCommand("stakeout - エージェント駆動ネイティブ C デバッグ基盤の CLI");
        root.Options.Add(jsonOption);
        root.Subcommands.Add(BuildStatusCommand(jsonOption));
        root.Subcommands.Add(BuildDaemonCommand(jsonOption));

        foreach (var command in DebugCommands.Build(jsonOption))
        {
            root.Subcommands.Add(command);
        }

        foreach (var command in CompositeCommands.Build(jsonOption))
        {
            root.Subcommands.Add(command);
        }

        foreach (var command in CodeCommands.Build(jsonOption))
        {
            root.Subcommands.Add(command);
        }

        // 引数なしは使い方エラー（design.md §8.3）
        root.SetAction(_ =>
        {
            Console.Error.WriteLine("サブコマンドを指定してください。stakeout --help を見てください。");
            return ExitCode.UsageError;
        });

        return await root.Parse(args).InvokeAsync();
    }

    // ---------------------------------------------------------------- status

    private static Command BuildStatusCommand(Option<bool> jsonOption)
    {
        var command = new Command("status", "デーモンの状態を表示する（必要なら自動起動する）");

        command.SetAction(async (parseResult, ct) =>
        {
            var output = new Output(parseResult.GetValue(jsonOption));
            var client = new DaemonClient(RpcTransport.PipeName);

            var result = await client.InvokeAsync(
                RpcMethods.DaemonStatus, null, autoStart: true, RpcTimeout, ct);

            return output.Write(result, WriteStatusText);
        });

        return command;
    }

    private static void WriteStatusText(JsonElement data)
    {
        Console.WriteLine($"stakeout     {Output.Get(data, "version")} (pid {Output.Get(data, "pid")})");
        Console.WriteLine($"pipe     \\\\.\\pipe\\{Output.Get(data, "pipeName")}");
        Console.WriteLine($"elevated {Output.Get(data, "isElevated")}");
        Console.WriteLine($"uptime   {Output.Get(data, "uptimeSeconds")} s");
        Console.WriteLine($"backend  {Output.Get(data, "configuredBackend")}");
        Console.WriteLine($"log      {Output.Get(data, "logPath")}");

        if (data.TryGetProperty("configPaths", out var paths) && paths.ValueKind == JsonValueKind.Array)
        {
            var list = paths.EnumerateArray().Select(p => p.GetString()).Where(p => p is not null).ToArray();
            Console.WriteLine($"config   {(list.Length == 0 ? "(既定値のみ)" : string.Join(", ", list))}");
        }

        if (data.TryGetProperty("sessions", out var sessions) && sessions.ValueKind == JsonValueKind.Array)
        {
            var count = sessions.GetArrayLength();
            Console.WriteLine($"sessions {count}");
            foreach (var session in sessions.EnumerateArray())
            {
                Console.WriteLine(
                    $"  {Output.Get(session, "sessionId")} " +
                    $"{Output.Get(session, "processName")} (pid {Output.Get(session, "pid")}) " +
                    $"{Output.Get(session, "state")}");
            }
        }
    }

    // ---------------------------------------------------------------- daemon

    private static Command BuildDaemonCommand(Option<bool> jsonOption)
    {
        var daemon = new Command("daemon", "デーモンの制御");

        var start = new Command("start", "デーモンを起動する（既に動いていれば何もしない）");
        start.SetAction(async (parseResult, ct) =>
        {
            var output = new Output(parseResult.GetValue(jsonOption));
            var client = new DaemonClient(RpcTransport.PipeName);

            var already = await client.InvokeAsync(
                RpcMethods.DaemonPing, null, autoStart: false, TimeSpan.FromSeconds(2), ct);

            if (already.Ok)
            {
                output.Line($"既に動いています (pid {Output.Get(already.Data, "pid")})");
                return output.Json ? output.WriteSuccess(already.Data, _ => { }) : ExitCode.Ok;
            }

            var started = await client.InvokeAsync(
                RpcMethods.DaemonPing, null, autoStart: true, RpcTimeout, ct);

            return output.Write(started, data =>
                Console.WriteLine($"起動しました (pid {Output.Get(data, "pid")})"));
        });

        var stop = new Command("stop", "デーモンを停止する（Target はデタッチして生かす）");
        stop.SetAction(async (parseResult, ct) =>
        {
            var output = new Output(parseResult.GetValue(jsonOption));
            var client = new DaemonClient(RpcTransport.PipeName);

            // 先に生存を確かめる。shutdown は応答直後に接続が切れるため、
            // 「元から動いていなかった」と「停止できた」を後から区別できない
            var alive = await client.InvokeAsync(
                RpcMethods.DaemonPing, null, autoStart: false, TimeSpan.FromSeconds(2), ct);

            if (!alive.Ok)
            {
                output.Line("動いていません");
                return ExitCode.Ok;
            }

            var result = await client.InvokeAsync(
                RpcMethods.DaemonShutdown, null, autoStart: false, RpcTimeout, ct);

            // 停止要求の直後に接続が切れるのは正常。停止できていれば目的は果たされている
            if (!result.Ok && result.Error?.Code == ErrorCodes.DaemonUnreachable)
            {
                output.Line("停止しました");
                return ExitCode.Ok;
            }

            return output.Write(result, data =>
                Console.WriteLine($"停止しました (デタッチしたセッション: {Output.Get(data, "detachedSessions", "0")})"));
        });

        var restart = new Command("restart", "デーモンを停止してから起動する");
        restart.SetAction(async (parseResult, ct) =>
        {
            var output = new Output(parseResult.GetValue(jsonOption));
            var client = new DaemonClient(RpcTransport.PipeName);

            await client.InvokeAsync(RpcMethods.DaemonShutdown, null, autoStart: false, RpcTimeout, ct);
            await WaitForPipeToCloseAsync(client, ct);

            var started = await client.InvokeAsync(
                RpcMethods.DaemonPing, null, autoStart: true, RpcTimeout, ct);

            return output.Write(started, data =>
                Console.WriteLine($"再起動しました (pid {Output.Get(data, "pid")})"));
        });

        var status = new Command("status", "デーモンの状態を表示する（自動起動しない）");
        status.SetAction(async (parseResult, ct) =>
        {
            var output = new Output(parseResult.GetValue(jsonOption));
            var client = new DaemonClient(RpcTransport.PipeName);

            var result = await client.InvokeAsync(
                RpcMethods.DaemonStatus, null, autoStart: false, RpcTimeout, ct);

            return output.Write(result, WriteStatusText);
        });

        daemon.Subcommands.Add(start);
        daemon.Subcommands.Add(stop);
        daemon.Subcommands.Add(restart);
        daemon.Subcommands.Add(status);

        daemon.SetAction(_ =>
        {
            Console.Error.WriteLine("stakeout daemon (start|stop|restart|status) のいずれかを指定してください。");
            return ExitCode.UsageError;
        });

        return daemon;
    }

    /// <summary>
    /// 古いデーモンがパイプを手放すまで待つ。
    /// 待たずに起動すると、まだ生きている古いデーモンに ping が通ってしまい、
    /// 再起動したつもりで古いプロセスが残る。
    /// </summary>
    private static async Task WaitForPipeToCloseAsync(DaemonClient client, CancellationToken ct)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            var ping = await client.InvokeAsync(
                RpcMethods.DaemonPing, null, autoStart: false, TimeSpan.FromSeconds(1), ct);

            if (!ping.Ok)
            {
                return;
            }

            await Task.Delay(100, ct);
        }
    }
}
