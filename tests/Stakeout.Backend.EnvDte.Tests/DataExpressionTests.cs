using Stakeout.Core;
using Stakeout.Rpc;

namespace Stakeout.Backend.EnvDte.Tests;

/// <summary>
/// データブレークポイントのアドレス検証（ADR 0006 の罠 3）。
///
/// <c>&amp;</c> を忘れた式は例外を出さずに通り、「値をアドレスとして解釈した」
/// まったく無関係な番地を監視するブレークポイントになる。調査者はそれに気づかず
/// 「書き込みが検出されない」と誤解する。だから作成後にアドレスを突き合わせる。
/// </summary>
public sealed class DataBreakpointAddressTests
{
    [Theory]
    [InlineData("'0x00007FF92CBC8268' から (4 バイト)変わった場合", "0x00007ff92cbc8268")]
    [InlineData("'0x00007ff92cbc8268' から (4 バイト)変わった場合", "0x00007FF92CBC8268")]
    [InlineData("when '0x7FF92CBC8268' changes (4 bytes)", "0x00007FF92CBC8268")]
    public void 意図したアドレスと一致すれば通す(string breakpointName, string expected)
    {
        // 表示言語や先頭のゼロで弾かない
        BreakpointAddressCheck.Verify(breakpointName, expected);
    }

    [Fact]
    public void 別のアドレスを監視していたら弾く()
    {
        // 0x0BADF00D は counter の「値」。& を忘れるとこれがアドレスになる
        var ex = Assert.Throws<BackendException>(() =>
            BreakpointAddressCheck.Verify(
                "'0x000000000BADF00D' から (4 バイト)変わった場合",
                "0x00007FF92CBC8268"));

        Assert.Equal(ErrorCodes.Backend, ex.Code);
        Assert.Contains("モジュール名", ex.Hint);
    }

    [Fact]
    public void アドレスが読み取れない名前は通す()
    {
        // 誤検出でデータブレークポイントを使えなくするほうが害が大きい
        BreakpointAddressCheck.Verify("g_shared.counter が変更されたとき", "0x00007FF92CBC8268");
    }
}
