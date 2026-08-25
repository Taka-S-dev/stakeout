using Stakeout.Core;

namespace Stakeout.Backend.EnvDte;

/// <summary>
/// 呼び出し側の再試行（ADR 0004）。
///
/// メッセージフィルタは呼び出しごとの拒否をある程度吸収するが、
/// Visual Studio がビルド中などで長く塞がっていると再試行枠を使い切って例外になる。
/// 一時的なビジーを吸収するには、呼び出し側でも待つ必要がある。
///
/// モーダルダイアログによるブロックはここでは回復しない。それは待っても直らないので、
/// 呼び出す前に <see cref="VisualStudioReadinessCheck"/> で弾く。
/// </summary>
internal static class ComRetry
{
    /// <summary>既定の再試行期間（design.md §15.2 の「COM リトライ合計」）。</summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(200);

    public static T Run<T>(Func<T> call, string operation, TimeSpan? window = null)
    {
        var deadline = Environment.TickCount64 + (long)(window ?? DefaultWindow).TotalMilliseconds;

        while (true)
        {
            try
            {
                return call();
            }
            catch (Exception ex) when (ComErrors.IsBusy(ex))
            {
                if (Environment.TickCount64 >= deadline)
                {
                    throw ComErrors.Translate(ex, operation, "ビジー");
                }

                Thread.Sleep(Interval);
            }
            catch (Exception ex) when (ex is not BackendException)
            {
                throw ComErrors.Translate(ex, operation, "不明");
            }
        }
    }

    public static void Run(Action call, string operation, TimeSpan? window = null) =>
        Run<object?>(() => { call(); return null; }, operation, window);
}
