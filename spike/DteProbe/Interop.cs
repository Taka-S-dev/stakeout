using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text.RegularExpressions;

namespace DteProbe;

/// <summary>ROT 上で動いている Visual Studio の 1 インスタンス。</summary>
internal sealed record DteCandidate(string MonikerName, string ProgId, int Major, int Minor, int Pid, object Dte)
{
    public string Version => $"{Major}.{Minor}";
}

/// <summary>
/// Running Object Table から Visual Studio の DTE を版数非依存で拾う（ADR 0002）。
/// .NET 8 には Marshal.GetActiveObject が無いので ole32 を直接叩く。
/// </summary>
internal static class Rot
{
    // !VisualStudio.DTE.16.0:1234 のような表示名
    private static readonly Regex MonikerPattern =
        new(@"^!(VisualStudio\.DTE\.(\d+)\.(\d+)):(\d+)$", RegexOptions.Compiled);

    [DllImport("ole32.dll", PreserveSig = false)]
    private static extern void GetRunningObjectTable(int reserved, out IRunningObjectTable prot);

    [DllImport("ole32.dll", PreserveSig = false)]
    private static extern void CreateBindCtx(int reserved, out IBindCtx ppbc);

    public static IReadOnlyList<DteCandidate> Enumerate()
    {
        var found = new List<DteCandidate>();

        GetRunningObjectTable(0, out var table);
        CreateBindCtx(0, out var bindCtx);
        table.EnumRunning(out var monikers);
        monikers.Reset();

        var buffer = new IMoniker[1];
        while (monikers.Next(1, buffer, IntPtr.Zero) == 0)
        {
            var moniker = buffer[0];
            try
            {
                moniker.GetDisplayName(bindCtx, null, out var displayName);
                var m = MonikerPattern.Match(displayName);
                if (!m.Success)
                {
                    continue;
                }

                // ROT には死んだ登録が残ることがある。GetObject が落ちたら黙って捨てる
                object? dte = null;
                try
                {
                    table.GetObject(moniker, out dte);
                }
                catch (COMException)
                {
                    continue;
                }

                if (dte is null)
                {
                    continue;
                }

                found.Add(new DteCandidate(
                    displayName,
                    m.Groups[1].Value,
                    int.Parse(m.Groups[2].Value),
                    int.Parse(m.Groups[3].Value),
                    int.Parse(m.Groups[4].Value),
                    dte));
            }
            finally
            {
                Marshal.ReleaseComObject(moniker);
            }
        }

        return found;
    }
}

/// <summary>
/// COM のメッセージフィルタ。VS がビジー（ビルド中・モーダル中）のときに返る
/// RPC_E_CALL_REJECTED / RPC_E_SERVERCALL_RETRYLATER をリトライに変える。
/// これが無いと「たまに失敗する」道具になる（design.md §10.2）。
/// </summary>
[ComImport, Guid("00000016-0000-0000-C000-000000000046"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IOleMessageFilter
{
    [PreserveSig] int HandleInComingCall(int callType, IntPtr taskCaller, int tickCount, IntPtr interfaceInfo);
    [PreserveSig] int RetryRejectedCall(IntPtr taskCallee, int tickCount, int rejectType);
    [PreserveSig] int MessagePending(IntPtr taskCallee, int tickCount, int pendingType);
}

internal sealed class RetryMessageFilter : IOleMessageFilter
{
    private const int ServerCallIsHandled = 0;
    private const int ServerCallRejected = 1;
    private const int ServerCallRetryLater = 2;
    private const int PendingMsgWaitDefProcess = 2;
    private const int CancelCall = -1;

    private readonly int _retryWindowMs;

    /// <summary>リトライした回数。スパイクの計測用。</summary>
    public int RetryCount;

    /// <summary>拒否理由の内訳。SERVERCALL_REJECTED はフィルタでは救いきれないことがある。</summary>
    public int RejectedCount;
    public int RetryLaterCount;

    private RetryMessageFilter(int retryWindowMs) => _retryWindowMs = retryWindowMs;

    [DllImport("ole32.dll")]
    private static extern int CoRegisterMessageFilter(IOleMessageFilter? newFilter, out IOleMessageFilter? oldFilter);

    /// <summary>呼び出しスレッド（STA でなければならない）に登録する。</summary>
    public static RetryMessageFilter Register(int retryWindowMs = 30_000)
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
        {
            throw new InvalidOperationException("メッセージフィルタは STA スレッドにしか登録できない");
        }

        var filter = new RetryMessageFilter(retryWindowMs);
        var hr = CoRegisterMessageFilter(filter, out _);
        if (hr != 0)
        {
            throw new InvalidOperationException($"CoRegisterMessageFilter が失敗した (hr=0x{hr:X8})");
        }

        return filter;
    }

    public static void Revoke() => CoRegisterMessageFilter(null, out _);

    // VS からこちらへの着信は常に受ける
    public int HandleInComingCall(int callType, IntPtr taskCaller, int tickCount, IntPtr interfaceInfo)
        => ServerCallIsHandled;

    // 拒否された呼び出しは、規定時間内なら 100ms 後に再試行する。
    // SERVERCALL_RETRYLATER だけでなく SERVERCALL_REJECTED も再試行する。
    // VS が起動直後やモーダル表示中は REJECTED を返し、RETRYLATER しか見ないと即失敗する。
    public int RetryRejectedCall(IntPtr taskCallee, int tickCount, int rejectType)
    {
        if (rejectType == ServerCallRejected)
        {
            Interlocked.Increment(ref RejectedCount);
        }
        else if (rejectType == ServerCallRetryLater)
        {
            Interlocked.Increment(ref RetryLaterCount);
        }
        else
        {
            return CancelCall;
        }

        if (tickCount >= _retryWindowMs)
        {
            return CancelCall;
        }

        Interlocked.Increment(ref RetryCount);
        return 100;
    }

    // 応答待ち中もメッセージを処理する（DTE イベントの配送に必要）
    public int MessagePending(IntPtr taskCallee, int tickCount, int pendingType)
        => PendingMsgWaitDefProcess;
}

internal static class Pump
{
    /// <summary>STA のメッセージを回しながら待つ。DTE イベントはこれが無いと配送されない。</summary>
    public static void For(TimeSpan duration)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < duration)
        {
            System.Windows.Forms.Application.DoEvents();
            Thread.Sleep(5);
        }
    }

    /// <summary>条件が真になるまでメッセージを回しながら待つ。true = 条件成立、false = タイムアウト。</summary>
    public static bool Until(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (condition())
            {
                return true;
            }

            System.Windows.Forms.Application.DoEvents();
            Thread.Sleep(5);
        }

        return condition();
    }
}

/// <summary>
/// COM 呼び出しの再試行。メッセージフィルタは呼び出しごとの拒否をある程度吸収するが、
/// VS が起動直後・モーダル表示中・ソリューション読み込み中は拒否が続き、
/// フィルタの再試行枠を使い切って COMException になる。呼び出し側でも待つ必要がある。
/// </summary>
internal static class Com
{
    private const int RpcECallRejected = unchecked((int)0x80010001);
    private const int RpcEServerCallRetryLater = unchecked((int)0x8001010A);
    private const int RpcEDisconnected = unchecked((int)0x80010108);

    public static bool IsBusy(Exception ex) =>
        ex is COMException && ex.HResult is RpcECallRejected or RpcEServerCallRetryLater;

    public static bool IsDisconnected(Exception ex) =>
        ex is COMException && ex.HResult == RpcEDisconnected;

    /// <summary>VS がビジーの間は待ち、応答したら結果を返す。</summary>
    public static T Retry<T>(Func<T> call, TimeSpan? window = null, TimeSpan? interval = null)
    {
        var deadline = DateTime.UtcNow + (window ?? TimeSpan.FromSeconds(30));
        var wait = interval ?? TimeSpan.FromMilliseconds(200);

        while (true)
        {
            try
            {
                return call();
            }
            catch (Exception ex) when (IsBusy(ex))
            {
                if (DateTime.UtcNow >= deadline)
                {
                    throw;
                }

                Pump.For(wait);
            }
        }
    }

    public static void Retry(Action call, TimeSpan? window = null, TimeSpan? interval = null)
        => Retry<object?>(() => { call(); return null; }, window, interval);
}
