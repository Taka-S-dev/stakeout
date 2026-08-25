using Stakeout.Core;

namespace Stakeout.Core.Tests;

public sealed class PagerTests
{
    private sealed record Item(string Name, string Payload);

    private static Item[] Items(int count, int payloadSize) =>
        Enumerable.Range(0, count)
                  .Select(i => new Item($"item{i}", new string('x', payloadSize)))
                  .ToArray();

    [Fact]
    public void 空なら空を返す()
    {
        var page = Pager.Split(Array.Empty<Item>(), 1024);

        Assert.Empty(page.Items);
        Assert.False(page.Truncated);
    }

    [Fact]
    public void 上限に収まるなら全部返す()
    {
        var page = Pager.Split(Items(5, 10), 64 * 1024);

        Assert.Equal(5, page.Items.Count);
        Assert.Empty(page.Rest);
        Assert.False(page.Truncated);
    }

    [Fact]
    public void 上限を超えたら打ち切って残りを返す()
    {
        var items = Items(100, 100);

        var page = Pager.Split(items, 1024);

        Assert.True(page.Truncated);
        Assert.InRange(page.Items.Count, 1, 99);
        Assert.Equal(items.Length, page.Items.Count + page.Rest.Count);

        // 先頭から順に詰める。順序を入れ替えない
        Assert.Equal(items[0], page.Items[0]);
        Assert.Equal(items[page.Items.Count], page.Rest[0]);
    }

    [Fact]
    public void 一件目だけで上限を超えてもその一件は返す()
    {
        // 空を返すと呼び出し側が cursor を辿り続けて終わらなくなる
        var items = Items(3, 5000);

        var page = Pager.Split(items, 100);

        Assert.Single(page.Items);
        Assert.Equal(2, page.Rest.Count);
        Assert.True(page.Truncated);
    }

    [Fact]
    public void 上限がゼロ以下なら例外になる()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Pager.Split(Items(1, 1), 0));
    }
}

public sealed class CursorStoreTests
{
    /// <summary>時間を進められる TimeProvider。有効期限の検証に使う。</summary>
    private sealed class TestTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    [Fact]
    public void 預けた残りをカーソルで引き換えられる()
    {
        var store = new CursorStore();
        var cursor = store.Create(new[] { 1, 2, 3 });

        Assert.True(store.TryTake(cursor, out var remainder));
        Assert.Equal(new[] { 1, 2, 3 }, remainder);
    }

    [Fact]
    public void カーソルは一度しか使えない()
    {
        var store = new CursorStore();
        var cursor = store.Create("rest");

        Assert.True(store.TryTake(cursor, out _));
        Assert.False(store.TryTake(cursor, out var second));
        Assert.Null(second);
    }

    [Fact]
    public void 知らないカーソルは失敗する()
    {
        var store = new CursorStore();

        Assert.False(store.TryTake("00000000000000000000000000000000", out _));
    }

    [Fact]
    public void 有効期限を過ぎたカーソルは失敗して破棄される()
    {
        var time = new TestTime();
        var store = new CursorStore(time, TimeSpan.FromMinutes(10));
        var cursor = store.Create("rest");

        time.Advance(TimeSpan.FromMinutes(11));

        Assert.False(store.TryTake(cursor, out _));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void 有効期限内なら残っている()
    {
        var time = new TestTime();
        var store = new CursorStore(time, TimeSpan.FromMinutes(10));
        store.Create("rest");

        time.Advance(TimeSpan.FromMinutes(9));

        Assert.Equal(1, store.Count);
    }
}
