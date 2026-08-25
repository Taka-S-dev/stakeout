using Stakeout.Core;

namespace Stakeout.Core.Tests;

/// <summary>
/// スレッドからタスク名を決める規則（design.md §9.6）。
///
/// タスク名は調査中に人間が覚えていられる唯一の識別子である。
/// スレッド ID は停止のたびに変わりうるし、覚えられない。
/// </summary>
public sealed class TaskNameResolverTests
{
    private static TaskNameResolver Resolver(params (string Pattern, string Name)[] patterns) =>
        new(patterns.Select(p => new TaskEntryPattern(p.Pattern, p.Name)).ToArray());

    [Fact]
    public void スレッド名があればそれを使う()
    {
        // OS がスレッド名を持っているなら、推測するより確実である
        var resolver = Resolver((@"^Task_(\w+)_Main$", "$1"));

        Assert.Equal("Renderer", resolver.Resolve("Renderer", new[] { "Task_A_Main" }));
    }

    [Fact]
    public void スレッド名が無ければエントリ関数から決める()
    {
        var resolver = Resolver((@"^Task_(\w+)_Main$", "$1"));

        var name = resolver.Resolve(string.Empty, new[] { "nl_update_state", "Task_A_Main", "BaseThreadInitThunk" });

        Assert.Equal("A", name);
    }

    [Fact]
    public void 最下段に近いほうを優先する()
    {
        // 最上段はたまたま通っただけの関数でありうる。
        // タスクを決めるのはエントリに近いほうである
        var resolver = Resolver((@"^Task_(\w+)_Main$", "$1"));

        var name = resolver.Resolve(null, new[] { "Task_Z_Main", "helper", "Task_A_Main" });

        Assert.Equal("A", name);
    }

    [Fact]
    public void 引数付きの関数名でも一致する()
    {
        // VS は "Task_A_Main(void *)" のように返す。
        // パターンを書く人に $ の扱いを気にさせない
        var resolver = Resolver((@"^Task_(\w+)_Main$", "$1"));

        Assert.Equal("A", resolver.Match("Task_A_Main(void *)"));
    }

    [Fact]
    public void モジュール修飾付きの関数名でも一致する()
    {
        var resolver = Resolver((@"^nl_(\w+)$", "lib-$1"));

        Assert.Equal("lib-update", resolver.Match("NativeLib.dll!nl_update"));
    }

    [Fact]
    public void どれにも当たらなければ_null_を返す()
    {
        var resolver = Resolver((@"^Task_(\w+)_Main$", "$1"));

        Assert.Null(resolver.Resolve(null, new[] { "main", "BaseThreadInitThunk" }));
    }

    [Fact]
    public void パターンが空ならスレッド名だけを見る()
    {
        var resolver = Resolver();

        Assert.False(resolver.HasPatterns);
        Assert.Equal("worker", resolver.Resolve("worker", new[] { "Task_A_Main" }));
        Assert.Null(resolver.Resolve(null, new[] { "Task_A_Main" }));
    }

    [Fact]
    public void 壊れた正規表現があっても他のパターンは効く()
    {
        // 設定の誤りでタスク名が付かないのは許容するが、調査自体は続けられること
        var resolver = Resolver(("(unclosed", "broken"), (@"^Task_(\w+)_Main$", "$1"));

        Assert.Equal("B", resolver.Resolve(null, new[] { "Task_B_Main" }));
    }

    [Theory]
    [InlineData("Task_A_Main(void *)", "Task_A_Main")]
    [InlineData("NativeLib.dll!nl_update_state(int)", "nl_update_state")]
    [InlineData("main", "main")]
    [InlineData("  spaced  ", "spaced")]
    public void 関数名から装飾を落とす(string input, string expected)
    {
        Assert.Equal(expected, TaskNameResolver.StripSignature(input));
    }
}
