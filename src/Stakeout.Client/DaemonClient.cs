using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using Stakeout.Rpc;
using StreamJsonRpc;

namespace Stakeout.Client;

/// <param name="Ok">成功したか。</param>
/// <param name="Data">成功時のペイロード。JSON のまま持ち回る。</param>
/// <param name="Error">失敗時のエラー。</param>
public sealed record CliResult(bool Ok, JsonElement Data, StakeoutError? Error, bool Truncated, string? Cursor)
{
    public static CliResult Failure(string code, string message, string hint) =>
        new(false, default, new StakeoutError(code, message, hint), false, null);
}

/// <summary>
/// デーモンへの接続（design.md §8.1）。
/// 接続できなければ <c>stakeout --detach</c> を起動して最大 5 秒待つ。
/// </summary>
public sealed class DaemonClient
{
    /// <summary>stakeoutd.exe の場所を明示するための環境変数。開発時とテストで使う。</summary>
    public const string DaemonPathEnvironmentVariable = "STAKEOUT_EXE";

    /// <summary>自動起動したデーモンを待つ上限（design.md §8.1）。</summary>
    public static readonly TimeSpan AutoStartTimeout = TimeSpan.FromSeconds(5);

    private readonly string _pipeName;

    public DaemonClient(string pipeName) => _pipeName = pipeName;

    /// <summary>
    /// メソッドを 1 回呼ぶ。接続 → 呼び出し → 切断で完結させる。
    /// 接続を持ち回らないのは、CLI が 1 コマンド 1 RPC だからである（design.md §4.1）。
    /// </summary>
    public async Task<CliResult> InvokeAsync(
        string method,
        object? parameters,
        bool autoStart,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var pipe = await ConnectAsync(autoStart, ct);
        if (pipe is null)
        {
            var hint = _startupFailure is { Length: > 0 } failure
                ? $"stakeout の起動に失敗しました: {failure}"
                : autoStart
                    ? "stakeoutd.exe が見つからないか起動に失敗しました。stakeout daemon start を試すか、STAKEOUT_EXE で場所を指定してください。"
                    : "stakeout daemon start でデーモンを起動してください。";

            return CliResult.Failure(
                ErrorCodes.DaemonUnreachable,
                $"デーモンに接続できません (pipe: {_pipeName})。",
                hint);
        }

        using (pipe)
        {
            var formatter = new SystemTextJsonFormatter
            {
                JsonSerializerOptions = RpcJson.Options,
            };

            using var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(pipe, formatter));
            rpc.StartListening();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            try
            {
                var raw = parameters is null
                    ? await rpc.InvokeWithCancellationAsync<JsonElement>(method, null, cts.Token)
                    : await rpc.InvokeWithCancellationAsync<JsonElement>(method, new[] { parameters }, cts.Token);

                return Parse(raw);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return CliResult.Failure(
                    ErrorCodes.Timeout,
                    $"{method} が {timeout.TotalSeconds:F0} 秒以内に応答しませんでした。",
                    "デーモンが応答していません。stakeout daemon status で状態を確認してください。");
            }
            catch (RemoteInvocationException ex)
            {
                return CliResult.Failure(
                    ErrorCodes.Internal,
                    $"デーモンがエラーを返しました: {ex.Message}",
                    "デーモンのログを確認してください（stakeout daemon status で場所が分かります）。");
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or ConnectionLostException)
            {
                // shutdown は応答を返した直後に切れることがある。呼び出し側で扱う
                return CliResult.Failure(
                    ErrorCodes.DaemonUnreachable,
                    $"デーモンとの接続が切れました: {ex.Message}",
                    "stakeout daemon status で状態を確認してください。");
            }
        }
    }

    private static CliResult Parse(JsonElement raw)
    {
        var ok = raw.TryGetProperty("ok", out var okElement) && okElement.ValueKind == JsonValueKind.True;

        StakeoutError? error = null;
        if (raw.TryGetProperty("error", out var errorElement) && errorElement.ValueKind == JsonValueKind.Object)
        {
            error = new StakeoutError(
                errorElement.TryGetProperty("code", out var c) ? c.GetString() ?? ErrorCodes.Internal : ErrorCodes.Internal,
                errorElement.TryGetProperty("message", out var m) ? m.GetString() ?? string.Empty : string.Empty,
                errorElement.TryGetProperty("hint", out var h) ? h.GetString() ?? string.Empty : string.Empty);
        }

        var data = raw.TryGetProperty("data", out var d) ? d.Clone() : default;
        var truncated = raw.TryGetProperty("truncated", out var t) && t.ValueKind == JsonValueKind.True;
        var cursor = raw.TryGetProperty("cursor", out var cu) ? cu.GetString() : null;

        return new CliResult(ok, data, error, truncated, cursor);
    }

    /// <summary>自動起動したデーモンが即座に死んだ場合の、その理由。</summary>
    private string? _startupFailure;

    private async Task<NamedPipeClientStream?> ConnectAsync(bool autoStart, CancellationToken ct)
    {
        var pipe = await TryConnectAsync(TimeSpan.FromMilliseconds(300), ct);
        if (pipe is not null || !autoStart)
        {
            return pipe;
        }

        var daemon = TryStartDaemon();
        if (daemon is null)
        {
            return null;
        }

        var connected = await TryConnectAsync(AutoStartTimeout, ct);
        if (connected is not null)
        {
            return connected;
        }

        // 起動したのに繋がらないなら、たいてい設定が壊れているなどでデーモン自身が
        // 落ちている。「見つからない」で片付けると原因に辿り着けない
        if (daemon.HasExited)
        {
            _startupFailure = ReadStartupError()
                ?? $"stakeout が終了コード {daemon.ExitCode} で終了しました。";
        }

        return null;
    }

    /// <summary>".../src/Stakeout.Mcp/bin/..." を ".../src/Stakeout.Daemon/bin/..." に読み替える。</summary>
    private static string ReplaceProjectFolder(string path, string projectName)
    {
        var separator = Path.DirectorySeparatorChar;
        var segments = path.Split(separator);

        for (var i = 0; i < segments.Length; i++)
        {
            if (segments[i].StartsWith("Stakeout.", StringComparison.Ordinal))
            {
                segments[i] = projectName;
                return string.Join(separator, segments);
            }
        }

        return path;
    }

    /// <summary>
    /// デーモンが書き残した起動失敗の理由を読む。
    /// 標準エラーで受け取らないのは、子プロセスの出力をリダイレクトすると
    /// ハンドル継承が起きて、stakeout の出力を読んでいる側が固まるからである。
    /// </summary>
    private static string? ReadStartupError()
    {
        try
        {
            var path = RpcTransport.StartupErrorFile;
            if (!File.Exists(path))
            {
                return null;
            }

            var text = File.ReadAllText(path).Trim();
            return text.Length == 0 ? null : text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task<NamedPipeClientStream?> TryConnectAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (true)
        {
            var pipe = new NamedPipeClientStream(
                ".", _pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

            try
            {
                await pipe.ConnectAsync(200, ct);
                return pipe;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException)
            {
                await pipe.DisposeAsync();
                if (DateTime.UtcNow >= deadline)
                {
                    return null;
                }

                await Task.Delay(100, ct);
            }
        }
    }

    /// <summary>デーモンを常駐モードで起動する。起動できなければ null。</summary>
    private static Process? TryStartDaemon()
    {
        var exe = FindDaemonExecutable();
        if (exe is null)
        {
            return null;
        }

        try
        {
            // **UseShellExecute = true でなければならない。**
            //
            // false だと .NET は CreateProcess をハンドル継承つきで呼ぶ。すると
            // stakeout 自身の標準出力ハンドルが常駐デーモンに引き継がれ、stakeout が終了しても
            // 出力パイプが閉じない。stakeout の出力を読んでいる側（シェルのパイプ、
            // PowerShell の出力キャプチャ、CI）はそこで永久に待つ。
            // 標準ストリームをリダイレクトしても解決しない。継承自体が起きるためである。
            //
            // ShellExecuteEx はハンドルを継承しない。その代わり標準エラーを受け取れないので、
            // 起動に失敗した理由はデーモンが書き残すファイルから読む。
            var info = new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Environment.CurrentDirectory,
            };

            info.ArgumentList.Add("--detach");

            return Process.Start(info);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// stakeoutd.exe を探す。
    /// 配布時は stakeout.exe と同じディレクトリに入る。開発中は各プロジェクトの
    /// bin 配下に分かれるので、その並びも見る。
    /// </summary>
    public static string? FindDaemonExecutable()
    {
        if (Environment.GetEnvironmentVariable(DaemonPathEnvironmentVariable) is { Length: > 0 } fromEnv)
        {
            return File.Exists(fromEnv) ? fromEnv : null;
        }

        var baseDir = AppContext.BaseDirectory;
        var beside = Path.Combine(baseDir, "stakeoutd.exe");
        if (File.Exists(beside))
        {
            return beside;
        }

        // 開発時は各プロジェクトの bin 配下に分かれている。
        // 呼び出し元は Cli とは限らない（Mcp からも使う）ので、
        // プロジェクト名を決め打ちにせず、Stakeout.* の区間を Stakeout.Daemon に置き換える
        var sibling = ReplaceProjectFolder(baseDir, "Stakeout.Daemon");

        var devPath = Path.Combine(sibling, "stakeoutd.exe");
        if (File.Exists(devPath))
        {
            return devPath;
        }

        var binIndex = sibling.IndexOf(
            $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
            StringComparison.Ordinal);

        if (binIndex < 0)
        {
            return null;
        }

        var binRoot = sibling[..(binIndex + 5)];
        if (!Directory.Exists(binRoot))
        {
            return null;
        }

        return Directory
            .EnumerateFiles(binRoot, "stakeoutd.exe", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }
}
