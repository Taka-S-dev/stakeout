using Stakeout.Core;
using Stakeout.Rpc;

namespace Stakeout.Daemon;

/// <summary>
/// デーモンが抱える状態（design.md §4.1）。
/// Backend 接続・ログ・設定・カーソルをここで持ち、RPC ハンドラから参照する。
/// </summary>
public sealed class DaemonState : IAsyncDisposable
{
    private readonly CancellationTokenSource _shutdown = new();

    public DaemonState(LoadedConfig config, SessionLog log, DateTimeOffset startedAt)
    {
        Config = config.Config;
        ConfigPaths = config.LoadedPaths;
        Log = log;
        StartedAt = startedAt;
        Group = new SessionGroup(Config, log.Daemon, ConfigPaths);
    }

    public StakeoutConfig Config { get; }

    public IReadOnlyList<string> ConfigPaths { get; }

    public SessionLog Log { get; }

    /// <summary>セッションの束（design.md §6）。Phase 1 では 1 つだけ持つ。</summary>
    public SessionGroup Group { get; }

    public DateTimeOffset StartedAt { get; }

    public CursorStore Cursors { get; } = new();

    /// <summary>停止が要求されたら発火する。</summary>
    public CancellationToken ShutdownToken => _shutdown.Token;

    /// <summary>現在のセッション。</summary>
    public IReadOnlyList<SessionInfo> Sessions => Group.Sessions;

    public DaemonStatus BuildStatus(string pipeName, DateTimeOffset now) => new(
        Version: StakeoutPaths.Version,
        Pid: Environment.ProcessId,
        PipeName: pipeName,
        IsElevated: StakeoutPaths.IsElevated(),
        StartedAt: StartedAt,
        UptimeSeconds: Math.Round((now - StartedAt).TotalSeconds, 1),
        ConfiguredBackend: Config.Backend,
        ConfigPaths: ConfigPaths,
        LogPath: Log.Path,
        Sessions: Sessions);

    /// <summary>停止を要求する。RPC の応答を返し切ってから実際に落ちる。</summary>
    public void RequestShutdown() => _shutdown.Cancel();

    public async ValueTask DisposeAsync()
    {
        // Target を殺さないよう、必ずデタッチしてから閉じる（design.md §15.3）
        try
        {
            await Group.DisposeAsync();
        }
        catch (Exception ex)
        {
            Log.Daemon($"detach on shutdown failed: {ex.GetType().Name}: {ex.Message}");
        }

        _shutdown.Dispose();
        Log.Dispose();
    }
}
