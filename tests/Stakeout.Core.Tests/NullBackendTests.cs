using Stakeout.Core;
using Stakeout.Rpc;

namespace Stakeout.Core.Tests;

/// <summary>
/// 空実装が「黙って成功する」ようになっていないことを確かめる。
/// 成功を装う空実装は、上位層のバグをテストごとすり抜けさせる。
/// </summary>
public sealed class NullBackendTests
{
    [Fact]
    public async Task アタッチは設定されていないと言って失敗する()
    {
        await using var backend = new NullBackend();

        var ex = await Assert.ThrowsAsync<BackendException>(
            () => backend.AttachAsync(1234, new AttachOptions(), CancellationToken.None));

        Assert.Equal(ErrorCodes.NotConfigured, ex.Code);
        Assert.NotEmpty(ex.Hint);
    }

    [Fact]
    public async Task 読み取り系はセッションが無いと言って失敗する()
    {
        await using var backend = new NullBackend();

        var ex = await Assert.ThrowsAsync<BackendException>(
            () => backend.GetThreadsAsync(CancellationToken.None));

        Assert.Equal(ErrorCodes.NotAttached, ex.Code);
    }

    [Fact]
    public async Task デタッチは何度呼んでも失敗しない()
    {
        // 停止処理から呼ばれる。ここで例外を出すと Target を残したままデーモンが落ちる
        await using var backend = new NullBackend();

        await backend.DetachAsync(CancellationToken.None);
        await backend.DetachAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ブレークポイント一覧は空を返す()
    {
        await using var backend = new NullBackend();

        Assert.Empty(await backend.ListBreakpointsAsync(CancellationToken.None));
    }

    [Fact]
    public void できることは何も申告しない()
    {
        var backend = new NullBackend();

        Assert.False(backend.Capabilities.DataBreakpoint);
        Assert.False(backend.Capabilities.Tracepoint);
        Assert.Equal(0, backend.Capabilities.DataBreakpointSlots);
        Assert.Equal("null", backend.Name);
    }

    [Fact]
    public async Task エラーには必ず_hint_が付く()
    {
        // エージェントがエラーだけを読んで次の一手を決められることが要件（design.md §15.1）
        await using var backend = new NullBackend();

        var ex = await Assert.ThrowsAsync<BackendException>(
            () => backend.EvaluateAsync("g_ctx", null, new EvalOptions(), CancellationToken.None));

        Assert.False(string.IsNullOrWhiteSpace(ex.Hint));
        Assert.Equal(ex.Code, ex.ToError().Code);
        Assert.Equal(ex.Hint, ex.ToError().Hint);
    }
}
