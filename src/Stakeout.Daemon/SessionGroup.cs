using System.Text.RegularExpressions;
using Stakeout.Backend.EnvDte;
using Stakeout.Core;
using Stakeout.Rpc;

namespace Stakeout.Daemon;

/// <summary>
/// セッションの束（design.md §6）。
///
/// Phase 1 では「セッションを 1 つだけ持つ薄いクラス」である。
/// 複数プロセス対応（design.md 付録 C）が必要になったとき、
/// ここに中身を足せば上位層を書き換えずに済む。
///
/// Backend への操作はセッション単位で直列化する（design.md §7.2）。
/// COM は並行呼び出しに耐えないため、これは飾りではなく必須である。
/// </summary>
public sealed class SessionGroup : IAsyncDisposable
{
    private readonly StakeoutConfig _config;
    private readonly Action<string> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IDebuggerBackend? _backend;

    /// <summary>読み込んだ設定ファイル。拒否の理由を説明するのに使う。</summary>
    private readonly IReadOnlyList<string> _configPaths;

    public SessionGroup(StakeoutConfig config, Action<string> log, IReadOnlyList<string>? configPaths = null)
    {
        _config = config;
        _log = log;
        _configPaths = configPaths ?? Array.Empty<string>();
    }

    /// <summary>現在の Backend。未アタッチなら <see cref="NullBackend"/> を返す。</summary>
    public IDebuggerBackend Backend => _backend ?? NullBackendInstance;

    private static readonly NullBackend NullBackendInstance = new();

    public SessionInfo? Session => (_backend as EnvDteBackend)?.Session;

    public IReadOnlyList<SessionInfo> Sessions =>
        Session is { } session ? new[] { session } : Array.Empty<SessionInfo>();

    /// <summary>Backend 操作を直列化して実行する。</summary>
    public async Task<T> RunAsync<T>(Func<IDebuggerBackend, Task<T>> work, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await work(Backend);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 待ち系は他の読み取りと並行できる（design.md §7.2）。
    /// 直列化すると、停止を待っている間 stakeout status すら返らなくなる。
    /// </summary>
    public Task<T> RunUnserializedAsync<T>(Func<IDebuggerBackend, Task<T>> work) => work(Backend);

    public async Task<SessionInfo> AttachAsync(AttachRequest request, CancellationToken ct)
    {
        var pid = ResolvePid(request);
        EnsureAllowed(pid);

        await _gate.WaitAsync(ct);
        try
        {
            if (_backend is not null)
            {
                throw new BackendException(
                    ErrorCodes.Precondition,
                    "既にアタッチしています。",
                    "stakeout detach してからアタッチし直してください。");
            }

            var envDteConfig = request.VsPid is { } vsPid
                ? _config.EnvDte with { VsPid = vsPid }
                : _config.EnvDte;

            var backend = CreateBackend(request.Backend ?? _config.Backend, envDteConfig);

            try
            {
                var session = await backend.AttachAsync(pid, new AttachOptions(request.Engines), ct);
                _backend = backend;
                return session;
            }
            catch
            {
                await backend.DisposeAsync();
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DetachAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_backend is null)
            {
                return;
            }

            await _backend.DetachAsync(ct);
            await _backend.DisposeAsync();
            _backend = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>アタッチ可能なプロセスを列挙する。allowlist の判定結果を添える。</summary>
    public IReadOnlyList<TargetInfo> ListTargets(bool includeDenied)
    {
        var result = new List<TargetInfo>();

        foreach (var process in System.Diagnostics.Process.GetProcesses())
        {
            // Dispose した後は Id も読めない。先に取り出しておく
            int pid;
            string name;

            using (process)
            {
                try
                {
                    pid = process.Id;
                    name = process.ProcessName + ".exe";
                }
                catch (Exception)
                {
                    // 列挙中に終了したプロセス
                    continue;
                }
            }

            var allowed = IsAllowed(name);
            if (allowed || includeDenied)
            {
                result.Add(new TargetInfo(pid, name, allowed));
            }
        }

        return result.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private int ResolvePid(AttachRequest request)
    {
        if (request.Pid is { } pid)
        {
            return pid;
        }

        if (request.Name is not { Length: > 0 } pattern)
        {
            throw new BackendException(
                ErrorCodes.Usage,
                "アタッチ先が指定されていません。",
                "--pid か --name のどちらかを指定してください。");
        }

        var regex = new Regex(pattern, RegexOptions.IgnoreCase);
        var matches = ListTargets(includeDenied: false)
            .Where(t => regex.IsMatch(t.Name))
            .ToArray();

        return matches.Length switch
        {
            1 => matches[0].Pid,
            0 => throw new BackendException(
                ErrorCodes.NotFound,
                $"'{pattern}' に一致するプロセスがありません。",
                "stakeout targets --all で候補を確認し、allowProcesses に追加してください。"),
            _ => throw new BackendException(
                ErrorCodes.Precondition,
                $"'{pattern}' に {matches.Length} 個のプロセスが一致します。",
                $"--pid で指定してください。候補: " +
                string.Join(", ", matches.Select(m => $"{m.Name} (pid {m.Pid})"))),
        };
    }

    /// <summary>
    /// allowlist の判定（design.md §15.3）。**空なら全拒否**。
    /// 既定で何にでもアタッチできる道具にはしない。
    /// </summary>
    private bool IsAllowed(string processName) =>
        _config.AllowProcesses.Any(pattern =>
        {
            try
            {
                return Regex.IsMatch(processName, pattern, RegexOptions.IgnoreCase);
            }
            catch (ArgumentException)
            {
                // 設定の正規表現が壊れている。許可しない側に倒す
                return false;
            }
        });

    private void EnsureAllowed(int pid)
    {
        string name;
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            name = process.ProcessName + ".exe";
        }
        catch (ArgumentException)
        {
            throw new BackendException(
                ErrorCodes.NotFound,
                $"pid {pid} のプロセスがありません。",
                "stakeout targets で起動中のプロセスを確認してください。");
        }

        if (IsAllowed(name))
        {
            return;
        }

        throw new BackendException(
            ErrorCodes.Denied,
            $"{name} (pid {pid}) はアタッチが許可されていません。",
            DeniedHint());
    }

    /// <summary>
    /// 拒否の理由。**設定ファイルを読んでいないのか、読んだが許可していないのかを分けて言う。**
    /// 前者を「allowProcesses が空」とだけ言うと、エージェントは設定ファイルを読みに行き、
    /// そこには許可が書いてあるので混乱する（評価でデーモンが別のディレクトリで起動したときに実際に起きた）。
    /// </summary>
    private string DeniedHint()
    {
        if (!_configPaths.Any(p => Path.GetFileName(p) == ConfigLoader.ProjectFileName))
        {
            return $"{ConfigLoader.ProjectFileName} が読み込まれていません（デーモンの作業ディレクトリ: " +
                   $"{Environment.CurrentDirectory}、およびその親にありません）。" +
                   $"{ConfigLoader.ProjectFileName} のあるディレクトリで stakeout daemon restart してください。";
        }

        return _config.AllowProcesses.Count == 0
            ? $"allowProcesses が空です（読み込んだ設定: {string.Join(", ", _configPaths)}）。" +
              "allowProcesses に対象を追加してください（空は全拒否です）。"
            : $"allowProcesses に追加してください。現在の設定: {string.Join(", ", _config.AllowProcesses)}" +
              $"（読み込んだ設定: {string.Join(", ", _configPaths)}）";
    }

    private IDebuggerBackend CreateBackend(string name, EnvDteConfig envDte) => name.ToLowerInvariant() switch
    {
        "envdte" => new EnvDteBackend(envDte, _log, _config.Code.GtagsRoot),
        "null" => new NullBackend(),
        "dbgeng" => throw BackendException.Unsupported(
            "DbgEng Backend",
            "DbgEng Backend は Phase 5 で実装します。--backend envdte を使ってください。"),
        _ => throw new BackendException(
            ErrorCodes.Usage,
            $"未知の backend です: {name}",
            "envdte を指定してください。"),
    };

    public async ValueTask DisposeAsync()
    {
        await DetachAsync(CancellationToken.None);
        _gate.Dispose();
    }
}
