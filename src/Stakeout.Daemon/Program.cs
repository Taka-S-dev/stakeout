using Stakeout.Core;

namespace Stakeout.Daemon;

/// <summary>
/// stakeoutd.exe（design.md §4.1）。常駐して Backend 接続を保持し、RPC を受ける。
///
///   stakeout            前面で動く（デバッグ用）
///   stakeout --detach   コンソールを持たずに常駐する。CLI からの自動起動で使う
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var pipeName = StakeoutPaths.PipeName;
        var startedAt = DateTimeOffset.Now;
        var sessionId = Guid.NewGuid().ToString("n")[..8];

        LoadedConfig config;
        try
        {
            config = ConfigLoader.Load(Environment.CurrentDirectory);
        }
        catch (ConfigException ex)
        {
            // 壊れた設定で黙って既定値に落ちない。どのファイルが悪いかを言って止まる
            // --detach でもここだけは書く。自動起動した stakeout がこれを読んで hint にする
            Console.Error.WriteLine($"設定の読み込みに失敗しました: {ex.Message}");
            Console.Error.Flush();
            return 1;
        }

        var logDir = config.Config.Log.Dir ?? StakeoutPaths.DefaultLogDirectory;
        using var log = SessionLog.Open(logDir, sessionId, startedAt);

        await using var state = new DaemonState(config, log, startedAt);

        log.Daemon(
            $"stakeout {StakeoutPaths.Version} started pid={Environment.ProcessId} pipe={pipeName} " +
            $"elevated={StakeoutPaths.IsElevated()} backend={config.Config.Backend} " +
            $"config=[{string.Join(", ", config.LoadedPaths)}]");

        // --detach のときはコンソールに何も書かない。自動起動した親（stakeout）が
        // 標準ストリームを切り離しているため、書いても誰も読まないバッファに溜まる
        var detached = args.Contains("--detach");
        if (!detached)
        {
            Console.WriteLine($"stakeout {StakeoutPaths.Version} listening on \\\\.\\pipe\\{pipeName}");
            Console.WriteLine($"log: {log.Path}");
        }

        using var lifetime = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            lifetime.Cancel();
        };

        var server = new PipeServer(pipeName, state);
        try
        {
            await server.RunAsync(lifetime.Token);
        }
        catch (Exception ex)
        {
            log.Daemon($"fatal: {ex.GetType().Name}: {ex.Message}");
            if (!detached)
            {
                Console.Error.WriteLine($"stakeout が異常終了しました: {ex.Message}");
            }

            return 1;
        }

        log.Daemon("stakeout stopped");
        return 0;
    }

    private static void ClearStartupError()
    {
        try
        {
            File.Delete(StakeoutPaths.StartupErrorFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 消せなくても起動は続ける
        }
    }

    private static void RecordStartupError(string message)
    {
        try
        {
            Directory.CreateDirectory(StakeoutPaths.StateRoot);
            File.WriteAllText(StakeoutPaths.StartupErrorFile, message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 記録できなくても終了コードは返る
        }
    }
}
