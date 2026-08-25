using Stakeout.Backend.EnvDte;

namespace Stakeout.Backend.EnvDte.Tests;

/// <summary>
/// フレーム ID と変数参照は 1 回の停止の中でしか意味を持たない。
/// 再開後も使えてしまうと、別の停止の古いフレームを指したまま値を読み、
/// 原因の分からない誤った調査結果になる。
/// </summary>
public sealed class StopScopeTests
{
    [Fact]
    public void フレーム_ID_は_1_から順に振られる()
    {
        var scope = new StopScope();

        Assert.Equal(1, scope.AddFrame(threadId: 100, depth: 0));
        Assert.Equal(2, scope.AddFrame(threadId: 100, depth: 1));
        Assert.Equal(3, scope.AddFrame(threadId: 200, depth: 0));
    }

    [Fact]
    public void フレーム_ID_からスレッドと深さを引ける()
    {
        var scope = new StopScope();
        var id = scope.AddFrame(threadId: 4242, depth: 7);

        var frame = scope.Frame(id);

        Assert.NotNull(frame);
        Assert.Equal(4242, frame.ThreadId);
        Assert.Equal(7, frame.Depth);
    }

    [Fact]
    public void 知らないフレーム_ID_は_null_になる()
    {
        var scope = new StopScope();

        Assert.Null(scope.Frame(999));
    }

    [Fact]
    public void 再開したら参照は全部無効になる()
    {
        var scope = new StopScope();
        var id = scope.AddFrame(threadId: 1, depth: 0);

        scope.Clear();

        Assert.Null(scope.Frame(id));
    }

    [Fact]
    public void クリア後は_ID_が振り直される()
    {
        // 古い ID が新しいフレームに当たると、無効になったことに気付けない…
        // わけではなく、そもそも停止ごとに取り直す運用なので振り直しでよい
        var scope = new StopScope();
        scope.AddFrame(threadId: 1, depth: 0);
        scope.AddFrame(threadId: 1, depth: 1);

        scope.Clear();

        Assert.Equal(1, scope.AddFrame(threadId: 2, depth: 0));
    }

    [Fact]
    public void 子を持たない式には参照番号を振らない()
    {
        // 0 は「展開できない」を意味する（DAP の約束）
        var scope = new StopScope();

        Assert.Equal(0, scope.AddExpression(expression: null!, hasChildren: false));
    }
}
