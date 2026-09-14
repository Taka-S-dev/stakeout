using Stakeout.Backend.EnvDte;

namespace Stakeout.Backend.EnvDte.Tests;

/// <summary>
/// Visual Studio の表示文字列を数値に戻す部分（design.md §14 の ReadMemory 代替）。
///
/// EnvDTE にメモリを読む API は無く、式評価の結果を**文字列で**受け取るしかない。
/// つまりここが間違うと、メモリの中身を静かに間違える。**例外は出ない。**
/// メモリ破壊の調査でそれが起きると、誤ったバイト列を根拠に結論を出すことになる。
/// </summary>
public sealed class VsValueParserTests
{
    [Theory]
    [InlineData("0x41", 0x41)]
    [InlineData("0X41", 0x41)]
    [InlineData(" 0x0f ", 0x0f)]
    [InlineData("0xff", 0xff)]
    [InlineData("0x00", 0x00)]
    public void 接頭辞が付いていれば16進として読む(string display, int expected) =>
        Assert.Equal((byte)expected, VsValueParser.ParseByte(display));

    [Theory]
    [InlineData("0x41 'A'", 0x41)]
    [InlineData("65 'A'", 65)]
    [InlineData(@"18 '\x12'", 18)]
    [InlineData("0 '\0'", 0)]
    public void 後ろの文字リテラルは落とす(string display, int expected) =>
        Assert.Equal((byte)expected, VsValueParser.ParseByte(display));

    /// <summary>
    /// **18 は 10 進なら 18、16 進なら 24 である。** 取り違えると中身が変わるので、
    /// 接頭辞が無いものは 10 進として読むと決めておく。
    /// </summary>
    [Fact]
    public void 接頭辞が無ければ10進として読む()
    {
        Assert.Equal((byte)18, VsValueParser.ParseByte("18"));
        Assert.Equal((byte)24, VsValueParser.ParseByte("0x18"));
    }

    [Fact]
    public void 符号付きで見せられても値を保つ() =>
        Assert.Equal((byte)0xff, VsValueParser.ParseByte("-1"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<エラーです>")]
    [InlineData("CXX0017: エラーです: シンボルが見つかりません")]
    [InlineData("256")]
    [InlineData("0x1ff")]
    public void 読めないものはnullを返す_ゼロで代用しない(string? display) =>
        Assert.Null(VsValueParser.ParseByte(display));

    [Theory]
    [InlineData("0x00007ff6a2c31040", 0x00007ff6a2c31040UL)]
    [InlineData("0x00007ff6a2c31040 {counter=3 scratch=0x... }", 0x00007ff6a2c31040UL)]
    [InlineData("0x7ff6a2c31040 <g_shared>", 0x7ff6a2c31040UL)]
    [InlineData("00007ff6`a2c31040", 0UL)]
    [InlineData("0x00007ff6`a2c31040", 0x00007ff6a2c31040UL)]
    // 構造体メンバのアドレス（&g_ctx.inner.flags）は、実機でモジュール名が前に付いた
    [InlineData("NativeLib.dll!0x00007ffafdf79b4c {2779096485}", 0x00007ffafdf79b4cUL)]
    [InlineData("\"hello!0x1234\"", 0UL)]
    public void 値の先頭にあるアドレスを取り出す(string display, ulong expected)
    {
        var ok = VsValueParser.TryExtractAddress(display, out var address);

        Assert.Equal(expected != 0, ok);
        Assert.Equal(expected, address);
    }

    [Theory]
    [InlineData("3")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("<未定義値>")]
    public void アドレスでないものは取り出さない(string? display) =>
        Assert.False(VsValueParser.TryExtractAddress(display, out _));
}
