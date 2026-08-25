using System.Text.RegularExpressions;

namespace Stakeout.Core;

/// <summary>書き込みらしさの確信度。</summary>
public enum WriteConfidence
{
    /// <summary>書いていない（読みだけ、宣言、コメント）。</summary>
    None,

    /// <summary>書いている可能性はあるが、確実ではない。</summary>
    Low,

    /// <summary>ほぼ確実に書いている。</summary>
    High,
}

/// <param name="Confidence">確信度。</param>
/// <param name="Reason">なぜそう判定したか。</param>
public sealed record WriteVerdict(WriteConfidence Confidence, string Reason);

/// <summary>
/// ソース 1 行を見て、そこがシンボルへの書き込みかを判定する（design.md §13）。
///
/// **これはヒューリスティックである。** 静的解析ではないので、
/// 見落としも誤検出もある。だから確信度と理由を付けて返し、
/// 使う側が「候補」として扱えるようにする。断定しない。
///
/// COM にもプロセスにも触れないので単体テストできる。
/// </summary>
public static partial class WriteSiteDetector
{
    [GeneratedRegex(@"^\s*(//|/\*|\*)")]
    private static partial Regex CommentLine();

    [GeneratedRegex(@"^\s*(extern|NL_API\s+extern)\b")]
    private static partial Regex ExternDeclaration();

    [GeneratedRegex("\"(\\\\.|[^\"\\\\])*\"")]
    private static partial Regex StringLiteral();

    /// <param name="line">ソースの 1 行。</param>
    /// <param name="expr">監視したい式。"g_shared.counter" や "g_ctx"。</param>
    public static WriteVerdict Classify(string line, string expr)
    {
        if (CommentLine().IsMatch(line))
        {
            return new WriteVerdict(WriteConfidence.None, "コメント行");
        }

        if (ExternDeclaration().IsMatch(line))
        {
            return new WriteVerdict(WriteConfidence.None, "extern 宣言");
        }

        // 文字列リテラルを外す。printf("g_shared.counter=%p") のような書式文字列を
        // 代入と読むと、確信度 high の誤検出になる
        line = StringLiteral().Replace(line, "\"\"");

        var escaped = Regex.Escape(expr);

        // 代入。== や != と間違えないよう、= の前後を見る
        if (Regex.IsMatch(line, $@"{escaped}\s*(\[[^\]]*\])?\s*([-+*/|&^%]|<<|>>)?=(?!=)"))
        {
            return new WriteVerdict(WriteConfidence.High, "代入");
        }

        // 前置・後置のインクリメント／デクリメント
        if (Regex.IsMatch(line, $@"(\+\+|--)\s*{escaped}\b") ||
            Regex.IsMatch(line, $@"{escaped}\s*(\+\+|--)"))
        {
            return new WriteVerdict(WriteConfidence.High, "インクリメント/デクリメント");
        }

        // memcpy / memset / memmove の書き込み先（第 1 引数）
        if (Regex.IsMatch(line, $@"\b(memcpy|memset|memmove|strcpy|strncpy|strcat)\s*\(\s*\(?[^,]*&?{escaped}\b"))
        {
            return new WriteVerdict(WriteConfidence.High, "メモリ操作関数の書き込み先");
        }

        // 基底シンボルへの代入は、そのメンバも壊しうる。
        // "g_shared.counter" を追っているとき "g_shared = ..." は見逃せない
        var baseSymbol = BaseSymbol(expr);
        if (baseSymbol != expr && Regex.IsMatch(line, $@"\b{Regex.Escape(baseSymbol)}\s*=(?!=)"))
        {
            return new WriteVerdict(WriteConfidence.High, $"基底シンボル {baseSymbol} への代入");
        }

        if (baseSymbol != expr &&
            Regex.IsMatch(line, $@"\b(memcpy|memset|memmove)\s*\(\s*\(?[^,]*&?{Regex.Escape(baseSymbol)}\b"))
        {
            return new WriteVerdict(WriteConfidence.High, $"基底シンボル {baseSymbol} へのメモリ操作");
        }

        // アドレスを渡している。呼ばれた先で書かれるかもしれない
        if (Regex.IsMatch(line, $@"&\s*{escaped}\b") ||
            (baseSymbol != expr && Regex.IsMatch(line, $@"&\s*{Regex.Escape(baseSymbol)}\b")))
        {
            return new WriteVerdict(WriteConfidence.Low, "アドレスを渡している（呼び先で書かれるかもしれない）");
        }

        return new WriteVerdict(WriteConfidence.None, "読み取りのみ");
    }

    /// <summary>
    /// デバッガ向けのモジュール修飾を落とす。
    /// "{,,NativeLib.dll}g_shared.counter" -> "g_shared.counter"。
    /// ソースコードにはこの記法が無いので、照合する前に外す。
    /// </summary>
    public static string StripModuleQualifier(string expr)
    {
        var text = expr.Trim();
        var brace = text.LastIndexOf('}');
        return brace >= 0 ? text[(brace + 1)..].Trim() : text;
    }

    /// <summary>"g_shared.counter" -> "g_shared"、"g_ctx->inner.id" -> "g_ctx"。</summary>
    public static string BaseSymbol(string expr)
    {
        var text = StripModuleQualifier(expr).TrimStart('&', '*', '(', ' ');

        var end = 0;
        while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_'))
        {
            end++;
        }

        return text[..end];
    }
}
