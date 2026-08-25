using System.Text.RegularExpressions;
using Stakeout.Core;
using Stakeout.Rpc;

namespace Stakeout.Backend.EnvDte;

/// <summary>
/// データブレークポイントが意図したアドレスを監視しているかの検証（ADR 0006 の罠 3）。
///
/// <c>&amp;</c> を忘れた式は例外を出さずに通り、値をアドレスとして解釈した
/// まったく無関係な番地を監視するブレークポイントになる。エラーは出ないので、
/// 調査者は「書き込みが検出されない」と誤解したまま時間を失う。
///
/// COM に触れないので単体テストできる。
/// </summary>
internal static partial class BreakpointAddressCheck
{
    [GeneratedRegex(@"0x[0-9A-Fa-f]+")]
    private static partial Regex AddressPattern();

    /// <param name="breakpointName">VS が付けたブレークポイント名。表示言語で文面が変わる。</param>
    /// <param name="expectedAddress">意図したアドレス（&amp;式 の評価結果）。</param>
    public static void Verify(string breakpointName, string expectedAddress)
    {
        var match = AddressPattern().Match(breakpointName);

        if (!match.Success)
        {
            // アドレスが読み取れないだけなら通す。誤検出で使えなくするほうが害が大きい
            return;
        }

        var actual = Normalize(match.Value);
        var expected = Normalize(expectedAddress);

        if (actual == expected)
        {
            return;
        }

        throw new BackendException(
            ErrorCodes.Backend,
            $"データブレークポイントが意図しないアドレスに張られました " +
            $"(期待 0x{expected} / 実際 0x{actual})。",
            "データ式は {,,モジュール名}&式 の形で指定してください。" +
            "& を忘れると、値がアドレスとして解釈されます。");
    }

    /// <summary>先頭の 0x と余分なゼロを落として比べられるようにする。</summary>
    private static string Normalize(string address)
    {
        var digits = address.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? address[2..]
            : address;

        var trimmed = digits.TrimStart('0');
        return (trimmed.Length == 0 ? "0" : trimmed).ToLowerInvariant();
    }
}
