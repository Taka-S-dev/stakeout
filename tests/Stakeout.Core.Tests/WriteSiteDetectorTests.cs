using Stakeout.Core;

namespace Stakeout.Core.Tests;

/// <summary>
/// 書き込み判定のヒューリスティック（design.md §13）。
///
/// これは静的解析ではない。見落としも誤検出もある前提で、
/// **確信度を付けて候補として返す**のが仕事である。
/// だからここで確かめるのは「断定していないこと」でもある。
/// </summary>
public sealed class WriteSiteDetectorTests
{
    private static WriteConfidence Classify(string line, string expr = "g_shared.counter") =>
        WriteSiteDetector.Classify(line, expr).Confidence;

    [Theory]
    [InlineData("    g_shared.counter = value;")]
    [InlineData("    g_shared.counter += delta;")]
    [InlineData("    g_shared.counter |= mask;")]
    [InlineData("    g_shared.counter <<= 2;")]
    public void 代入は確信度が高い(string line)
    {
        Assert.Equal(WriteConfidence.High, Classify(line));
    }

    [Theory]
    [InlineData("    if (g_shared.counter == 0) {")]
    [InlineData("    if (g_shared.counter != limit) {")]
    [InlineData("    return g_shared.counter;")]
    [InlineData("    printf(\"%ld\", g_shared.counter);")]
    public void 読み取りは書き込みとみなさない(string line)
    {
        // == と = を取り違えると、読み取り行がすべて候補になり、候補一覧が役に立たなくなる
        Assert.Equal(WriteConfidence.None, Classify(line));
    }

    [Fact]
    public void 書式文字列の中の等号は代入ではない()
    {
        // printf("&g_shared.counter=%p") を代入と読むと、確信度 high の誤検出になる
        var line = "    printf(\"&g_ctx=%p &g_shared.counter=%p\\n\",";

        Assert.Equal(WriteConfidence.None, Classify(line));
    }

    [Theory]
    [InlineData("    g_shared.counter++;")]
    [InlineData("    ++g_shared.counter;")]
    public void インクリメントは書き込みである(string line)
    {
        Assert.Equal(WriteConfidence.High, Classify(line));
    }

    [Fact]
    public void 基底シンボルへのメモリ操作はメンバも壊す()
    {
        var line = "    memset((void *)&g_shared, 0, sizeof(g_shared));";

        Assert.Equal(WriteConfidence.High, Classify(line));
    }

    [Fact]
    public void 固定長バッファへの_strcpy_は書き込みである()
    {
        var line = "    strcpy(g_ctx.name, name);";

        Assert.Equal(WriteConfidence.High, Classify(line, "g_ctx.name"));
    }

    [Fact]
    public void アドレスを渡しているだけなら確信度は低い()
    {
        // 呼び先で書かれるかもしれないし、読むだけかもしれない。断定しない
        var line = "    save_counter(&g_shared.counter);";

        Assert.Equal(WriteConfidence.Low, Classify(line));
    }

    [Theory]
    [InlineData("// g_shared.counter = 0;")]
    [InlineData("/* g_shared.counter への書き込み */")]
    [InlineData(" * g_shared.counter を更新する")]
    public void コメント行は候補にしない(string line)
    {
        Assert.Equal(WriteConfidence.None, Classify(line));
    }

    [Fact]
    public void extern_宣言は候補にしない()
    {
        Assert.Equal(WriteConfidence.None, Classify("NL_API extern Shared g_shared;", "g_shared"));
    }

    [Fact]
    public void 隣接メンバへの範囲外書き込みは見つけられない()
    {
        // これは限界の記録である。g_shared.scratch[4] は構造体上 counter を踏むが、
        // ソースの字面には counter が出てこない。静的には見つからない。
        // だから find-corruption が「候補に無い書き込み」として動的に捕まえる
        var line = "    g_shared.scratch[4] = value;";

        Assert.Equal(WriteConfidence.None, Classify(line));
    }

    [Theory]
    [InlineData("g_shared.counter", "g_shared")]
    [InlineData("g_ctx->inner.id", "g_ctx")]
    [InlineData("&g_shared.counter", "g_shared")]
    [InlineData("{,,NativeLib.dll}g_shared.counter", "g_shared")]
    [InlineData("g_ctx.name[0]", "g_ctx")]
    public void 基底シンボルを取り出せる(string expr, string expected)
    {
        Assert.Equal(expected, WriteSiteDetector.BaseSymbol(expr));
    }

    [Fact]
    public void モジュール修飾を落とせる()
    {
        // ソースにはこの記法が無い。付けたまま照合すると候補がゼロになる
        Assert.Equal(
            "g_shared.counter",
            WriteSiteDetector.StripModuleQualifier("{,,NativeLib.dll}g_shared.counter"));
    }

    [Fact]
    public void 修飾付きの式でも代入を見つけられる()
    {
        var verdict = WriteSiteDetector.Classify(
            "    g_shared.counter = value;",
            WriteSiteDetector.StripModuleQualifier("{,,NativeLib.dll}g_shared.counter"));

        Assert.Equal(WriteConfidence.High, verdict.Confidence);
    }
}
