using System.Runtime.InteropServices;
using Stakeout.Backend.EnvDte;
using Stakeout.Core;
using Stakeout.Rpc;
using Xunit;

namespace Stakeout.Backend.EnvDte.Tests;

/// <summary>
/// STA で起きた例外の翻訳。
///
/// 翻訳しないと COM の失敗がそのままデーモンに届いて INTERNAL になり、
/// hint が「ログを見よ」だけになる。エージェントは次の一手を決められない。
/// </summary>
public sealed class StaDispatcherTests
{
    private const int RpcECallRejected = unchecked((int)0x80010001);

    [Fact]
    public async Task 作業項目の例外を翻訳して返す()
    {
        using var sta = new StaDispatcher(
            "test",
            ex => new BackendException(ErrorCodes.Precondition, "translated", "close the dialog", ex));

        var ex = await Assert.ThrowsAsync<BackendException>(() =>
            sta.InvokeAsync<int>(
                () => throw new COMException("rejected", RpcECallRejected),
                TestContext.Current.CancellationToken));

        Assert.Equal(ErrorCodes.Precondition, ex.Code);
        Assert.IsType<COMException>(ex.InnerException);
    }

    [Fact]
    public async Task 翻訳が失敗したら元の例外を返す()
    {
        // 翻訳の失敗で、本当の原因を上書きしない
        using var sta = new StaDispatcher("test", _ => throw new InvalidOperationException("broken translator"));

        await Assert.ThrowsAsync<COMException>(() =>
            sta.InvokeAsync<int>(
                () => throw new COMException("rejected", RpcECallRejected),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task 翻訳が無ければ素通しする()
    {
        using var sta = new StaDispatcher("test");

        await Assert.ThrowsAsync<COMException>(() =>
            sta.InvokeAsync<int>(
                () => throw new COMException("rejected", RpcECallRejected),
                TestContext.Current.CancellationToken));
    }
}
