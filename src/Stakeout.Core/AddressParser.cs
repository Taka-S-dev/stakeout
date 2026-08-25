using System.Globalization;

namespace Stakeout.Core;

/// <summary>
/// 「アドレスそのもの」と「アドレスに評価される式」を見分ける。
///
/// Backend に依存しない。<c>0x7ff6a2c31040</c> はどの Backend でもアドレスであり、
/// <c>&amp;g_ctx</c> はどの Backend でも式である。式をどう評価するかだけが Backend の仕事になる。
/// </summary>
public static class AddressParser
{
    /// <summary>
    /// アドレスのリテラル表記なら true。<c>0x</c> 付き 16 進、および
    /// DbgEng 風の桁区切り（<c>00007ff6`a2c31040</c>）を受ける。
    ///
    /// **接頭辞の無い 10 進数は式として扱う。** <c>4096</c> がアドレスなのか
    /// 定数式なのかは決められないので、決めない。
    /// </summary>
    public static bool TryParse(string? text, out ulong address)
    {
        address = 0;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var value = text.Trim().Replace("`", string.Empty, StringComparison.Ordinal);

        if (!value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        value = value[2..];

        return value.Length > 0
            && value.All(Uri.IsHexDigit)
            && ulong.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out address);
    }
}
