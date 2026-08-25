namespace Stakeout.Rpc;

/// <summary>
/// <c>daemon.status</c> の戻り値（design.md §7.1）。
/// </summary>
/// <param name="Version">stakeout のバージョン。</param>
/// <param name="Pid">デーモンのプロセス ID。</param>
/// <param name="PipeName">待ち受けている名前付きパイプ名。</param>
/// <param name="IsElevated">
/// デーモンが昇格しているか。Visual Studio と昇格レベルが違うと COM が無言で失敗するため、
/// 食い違いを診断できるように返す（design.md §10.3）。
/// </param>
/// <param name="StartedAt">起動時刻。</param>
/// <param name="UptimeSeconds">起動からの経過秒数。</param>
/// <param name="ConfiguredBackend">設定ファイルで既定になっている Backend 名。</param>
/// <param name="ConfigPaths">実際に読み込んだ設定ファイル（探索順、design.md §16）。</param>
/// <param name="LogPath">このデーモンのセッションログ。</param>
/// <param name="Sessions">現在のセッション。Phase 0 では常に空。</param>
public sealed record DaemonStatus(
    string Version,
    int Pid,
    string PipeName,
    bool IsElevated,
    DateTimeOffset StartedAt,
    double UptimeSeconds,
    string ConfiguredBackend,
    IReadOnlyList<string> ConfigPaths,
    string LogPath,
    IReadOnlyList<SessionInfo> Sessions);

/// <summary>
/// <c>daemon.ping</c> の戻り値。到達確認だけなので最小限にする。
/// </summary>
public sealed record DaemonPong(string Version, int Pid);

/// <summary>
/// <c>daemon.shutdown</c> の戻り値。
/// </summary>
/// <param name="DetachedSessions">終了前にデタッチしたセッション数。</param>
public sealed record DaemonShutdownResult(int DetachedSessions);
