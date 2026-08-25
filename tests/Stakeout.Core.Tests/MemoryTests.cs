using Stakeout.Core;

namespace Stakeout.Core.Tests;

/// <summary>アドレスの見分けと、読み取り結果の整形。</summary>
public sealed class AddressParserTests
{
    [Theory]
    [InlineData("0x7ff6a2c31040", 0x7ff6a2c31040UL)]
    [InlineData("0X7FF6A2C31040", 0x7ff6a2c31040UL)]
    [InlineData(" 0x1000 ", 0x1000UL)]
    [InlineData("00007ff6`a2c31040", 0UL)]
    [InlineData("0x00007ff6`a2c31040", 0x00007ff6a2c31040UL)]
    public void アドレスのリテラルを読む(string text, ulong expected)
    {
        var ok = AddressParser.TryParse(text, out var address);

        Assert.Equal(expected != 0, ok);
        Assert.Equal(expected, address);
    }

    /// <summary>
    /// **接頭辞の無い 10 進はアドレスと決めつけない。**
    /// 4096 がアドレスなのか定数式なのかは決められないので、式として Backend に渡す。
    /// </summary>
    [Theory]
    [InlineData("4096")]
    [InlineData("&g_ctx")]
    [InlineData("p->buffer")]
    [InlineData("{,,NativeLib.dll}&g_shared")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("0x")]
    [InlineData("0xzz")]
    public void アドレスでないものは式として扱う(string? text) =>
        Assert.False(AddressParser.TryParse(text, out _));
}

public sealed class MemoryFormatTests
{
    [Fact]
    public void 十六進と_印字できる文字を並べる()
    {
        var bytes = new byte[] { 0x48, 0x65, 0x6c, 0x6c, 0x6f, 0x00, 0xff };
        var block = MemoryFormat.ToBlock(0x1000, 7, bytes);

        Assert.Equal("48 65 6c 6c 6f 00 ff", block.Hex);
        Assert.Equal("Hello..", block.Ascii);
        Assert.Equal(7, block.Length);
        Assert.Equal(7, block.Requested);
    }

    /// <summary>
    /// 読めたバイト数が要求より少ないことが、そのまま結果に出る必要がある。
    /// **0 で埋めた結果と区別できないと、未マップの領域をゼロ埋めと取り違える。**
    /// </summary>
    [Fact]
    public void 途中で読めなくなったことが長さに出る()
    {
        var block = MemoryFormat.ToBlock(0x1000, 64, new byte[] { 0x01, 0x02 });

        Assert.Equal(64, block.Requested);
        Assert.Equal(2, block.Length);
    }

    [Fact]
    public void hexdumpは1行16バイトでアドレスが進む()
    {
        var bytes = Enumerable.Range(0, 20).Select(i => (byte)i).ToArray();
        var lines = MemoryFormat.Lines(0x7ff6a2c31040, bytes).ToArray();

        Assert.Equal(2, lines.Length);
        Assert.StartsWith("00007ff6a2c31040  00 01 02 03", lines[0]);
        Assert.StartsWith("00007ff6a2c31050  10 11 12 13", lines[1]);
    }

    [Fact]
    public void 空のバイト列でも落ちない()
    {
        var block = MemoryFormat.ToBlock(0x1000, 16, Array.Empty<byte>());

        Assert.Equal(string.Empty, block.Hex);
        Assert.Empty(MemoryFormat.Lines(0x1000, Array.Empty<byte>()));
    }
}
