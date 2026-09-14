using System.Globalization;

namespace Stakeout.Backend.EnvDte;

/// <summary>
/// Visual Studio が返す「表示用の値の文字列」を、数値に戻す。
///
/// EnvDTE にメモリを直接読む API は無いので、design.md §14 のとおり
/// <c>*(unsigned char(*)[N])(ADDR)</c> を式として評価し、その要素を読む。
/// 要素の値は文字列で返ってくるため、ここで解釈する必要がある。
///
/// **表示の揺れを全部ここに閉じ込める。** VS は同じ unsigned char を、
/// 書式指定子・言語・設定によって違う形で見せる。
///
/// <list type="bullet">
///   <item><c>0x41</c>（<c>,x</c> を付けた場合）</item>
///   <item><c>65 'A'</c>（既定）</item>
///   <item><c>0x41 'A'</c></item>
///   <item><c>18 '\x12'</c>（表示できない文字）</item>
/// </list>
///
/// **数値の解釈は接頭辞で決める。** <c>18</c> は 10 進なら 18、16 進なら 24 であり、
/// 取り違えるとメモリの中身を静かに間違える。接頭辞が無ければ 10 進として読む。
/// stakeout は <c>,x</c> を付けて要求するので、通常は接頭辞が付く。
/// </summary>
public static class VsValueParser
{
    /// <summary>
    /// 表示文字列を 1 バイトとして読む。解釈できなければ null。
    /// **推測でゼロを返さない。** 読めなかったバイトと 0x00 は区別する必要がある。
    /// </summary>
    public static byte? ParseByte(string? display)
    {
        if (string.IsNullOrWhiteSpace(display))
        {
            return null;
        }

        // 後ろの文字リテラル（'A' や '\x12'）は数値の別表現なので落とす
        var text = display.Trim();
        var quote = text.IndexOf('\'');

        if (quote >= 0)
        {
            text = text[..quote].Trim();
        }

        if (text.Length == 0)
        {
            return null;
        }

        var hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);

        if (hex)
        {
            text = text[2..];
        }

        var style = hex ? NumberStyles.HexNumber : NumberStyles.Integer;

        if (!int.TryParse(text, style, CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }

        // unsigned char のはずだが、符号付きとして見せる設定もありうる
        if (value is < -128 or > 255)
        {
            return null;
        }

        return unchecked((byte)value);
    }

    /// <summary>
    /// 値の文字列の先頭にあるアドレスを取り出す。取り出せなければ false。
    ///
    /// VS はポインタや <c>&amp;expr</c> を <c>0x00007ff6a2c31040 {counter=3 ...}</c> のように、
    /// アドレスの後ろに中身を付けて見せる。先頭の 16 進数だけを読む。
    /// DbgEng 風の <c>00007ff6`a2c31040</c> も受ける。
    /// </summary>
    public static bool TryExtractAddress(string? display, out ulong address)
    {
        address = 0;

        if (string.IsNullOrWhiteSpace(display))
        {
            return false;
        }

        var text = display.TrimStart();

        // メンバのアドレスは NativeLib.dll!0x00007ffa... {値} のように、モジュール名が前に付く（実機で確認）。
        // 前置きはモジュール名に使う文字（英数字 _ . -）だけに限る。
        // "hello!0x1234" のような文字列の値を、誤ってアドレスにしない
        var bang = text.IndexOf('!');
        if (bang > 0
            && text[..bang].All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-')
            && text[(bang + 1)..].StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            text = text[(bang + 1)..];
        }

        if (!text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var digits = new string(text[2..].TakeWhile(c => Uri.IsHexDigit(c) || c == '`').ToArray())
            .Replace("`", string.Empty, StringComparison.Ordinal);

        return digits.Length > 0
            && ulong.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out address);
    }
}
