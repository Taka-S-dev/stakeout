using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Stakeout.Backend.EnvDte;

/// <summary>
/// DTE 呼び出しを行う専用 STA スレッド（design.md §10.2）。
///
/// COM のメッセージフィルタは STA スレッドにしか登録できず、DTE のイベントは
/// ウィンドウメッセージ経由で配送される。したがってこのスレッドは
/// **メッセージを回し続けなければならない**。回さないと、待っている間に
/// イベントが届かず、応答待ちのまま固まる。
/// </summary>
internal sealed class StaDispatcher : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly CancellationTokenSource _stop = new();

    private OleMessageFilter? _filter;

    public StaDispatcher(string name)
    {
        _thread = new Thread(Run)
        {
            Name = name,
            IsBackground = true,
        };

        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait();
    }

    /// <summary>メッセージフィルタが再試行した回数。診断用。</summary>
    public int ComRetryCount => _filter?.RetryCount ?? 0;

    /// <summary>STA スレッドで実行して結果を受け取る。</summary>
    public Task<T> InvokeAsync<T>(Func<T> work, CancellationToken ct = default)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (_stop.IsCancellationRequested)
        {
            completion.SetException(new ObjectDisposedException(nameof(StaDispatcher)));
            return completion.Task;
        }

        using var registration = ct.Register(() => completion.TrySetCanceled(ct));

        _queue.Add(() =>
        {
            if (completion.Task.IsCompleted)
            {
                return;
            }

            try
            {
                completion.TrySetResult(work());
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });

        return completion.Task;
    }

    /// <summary>戻り値の無い版。</summary>
    public Task InvokeAsync(Action work, CancellationToken ct = default) =>
        InvokeAsync<object?>(() => { work(); return null; }, ct);

    /// <summary>
    /// 条件が真になるまでポーリングする（design.md §10.2 のポーリング主）。
    ///
    /// **待ちの全体を 1 つの作業項目にしてはならない。**
    /// STA スレッドは 1 本しかないので、待ち続ける作業項目がそれを占有すると、
    /// その間 stakeout status すら返らなくなる（design.md §7.2 に反する）。
    /// 1 回の判定だけを投げ、間隔はスレッドの外で待つ。
    /// </summary>
    public async Task<bool> WaitUntilAsync(
        Func<bool> condition, TimeSpan timeout, TimeSpan interval, CancellationToken ct)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;

        while (true)
        {
            if (await InvokeAsync(condition, ct))
            {
                return true;
            }

            if (Environment.TickCount64 >= deadline || ct.IsCancellationRequested)
            {
                return false;
            }

            await Task.Delay(interval, ct);
        }
    }

    /// <summary>
    /// STA スレッドで実行し、指定時間内に終わらなければ既定値を返す。
    /// 状態表示のような「あれば嬉しい」情報のために、呼び出し側を待たせない。
    /// </summary>
    public T InvokeWithFallback<T>(Func<T> work, T fallback, TimeSpan timeout)
    {
        try
        {
            var task = InvokeAsync(work);
            return task.Wait(timeout) ? task.Result : fallback;
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    private void Run()
    {
        _filter = OleMessageFilter.Register();
        _ready.Set();

        try
        {
            while (!_stop.IsCancellationRequested)
            {
                // 仕事があれば処理し、無ければメッセージを回す。
                // Take で無限に待つと、その間イベントが配送されない
                if (_queue.TryTake(out var work, millisecondsTimeout: 10))
                {
                    work();
                }

                PumpMessages();
            }
        }
        finally
        {
            OleMessageFilter.Revoke();
        }
    }

    /// <summary>溜まっているウィンドウメッセージを処理する。DTE イベントはこれで届く。</summary>
    private static void PumpMessages()
    {
        while (PeekMessage(out var message, IntPtr.Zero, 0, 0, PmRemove))
        {
            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _queue.CompleteAdding();

        // 応答しないスレッドのために停止処理を止めない
        _thread.Join(TimeSpan.FromSeconds(5));

        _queue.Dispose();
        _ready.Dispose();
        _stop.Dispose();
    }

    // ---------------------------------------------------------------- win32

    private const uint PmRemove = 0x0001;

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int PointX;
        public int PointY;
    }

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out Msg message, IntPtr hwnd, uint filterMin, uint filterMax, uint action);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Msg message);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref Msg message);
}
