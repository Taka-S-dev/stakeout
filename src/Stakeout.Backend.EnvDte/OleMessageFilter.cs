using System.Runtime.InteropServices;
using Stakeout.Core;
using Stakeout.Rpc;

namespace Stakeout.Backend.EnvDte;

/// <summary>
/// COM のメッセージフィルタ（design.md §10.2 / ADR 0004）。
/// ビジー中の Visual Studio が返す拒否を再試行に変える。
///
/// 実測では VS は一貫して <c>SERVERCALL_RETRYLATER</c> を返したが、
/// <c>SERVERCALL_REJECTED</c> も再試行する。コストが無く、他の状態で起こりうる。
/// </summary>
[ComImport, Guid("00000016-0000-0000-C000-000000000046"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IOleMessageFilter
{
    [PreserveSig] int HandleInComingCall(int callType, IntPtr taskCaller, int tickCount, IntPtr interfaceInfo);

    [PreserveSig] int RetryRejectedCall(IntPtr taskCallee, int tickCount, int rejectType);

    [PreserveSig] int MessagePending(IntPtr taskCallee, int tickCount, int pendingType);
}

internal sealed class OleMessageFilter : IOleMessageFilter
{
    /// <summary>再試行を諦めるまでの時間（design.md §15.2）。</summary>
    public const int RetryWindowMs = 30_000;

    private const int ServerCallIsHandled = 0;
    private const int ServerCallRejected = 1;
    private const int ServerCallRetryLater = 2;
    private const int PendingMsgWaitDefProcess = 2;
    private const int CancelCall = -1;
    private const int RetryAfterMs = 100;

    private int _retryCount;

    /// <summary>再試行した回数。診断用。</summary>
    public int RetryCount => Volatile.Read(ref _retryCount);

    [DllImport("ole32.dll")]
    private static extern int CoRegisterMessageFilter(IOleMessageFilter? newFilter, out IOleMessageFilter? oldFilter);

    /// <summary>呼び出しスレッド（STA でなければならない）に登録する。</summary>
    public static OleMessageFilter Register()
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
        {
            throw new InvalidOperationException("メッセージフィルタは STA スレッドにしか登録できません。");
        }

        var filter = new OleMessageFilter();
        var hr = CoRegisterMessageFilter(filter, out _);

        if (hr != 0)
        {
            throw new BackendException(
                ErrorCodes.Backend,
                $"CoRegisterMessageFilter が失敗しました (hr=0x{hr:X8})。",
                "stakeout を再起動してください。");
        }

        return filter;
    }

    public static void Revoke() => CoRegisterMessageFilter(null, out _);

    // VS からこちらへの着信は常に受ける
    public int HandleInComingCall(int callType, IntPtr taskCaller, int tickCount, IntPtr interfaceInfo)
        => ServerCallIsHandled;

    public int RetryRejectedCall(IntPtr taskCallee, int tickCount, int rejectType)
    {
        if (rejectType is not (ServerCallRejected or ServerCallRetryLater))
        {
            return CancelCall;
        }

        if (tickCount >= RetryWindowMs)
        {
            return CancelCall;
        }

        Interlocked.Increment(ref _retryCount);
        return RetryAfterMs;
    }

    // 応答待ち中もメッセージを処理する。DTE イベントの配送に必要
    public int MessagePending(IntPtr taskCallee, int tickCount, int pendingType)
        => PendingMsgWaitDefProcess;
}
