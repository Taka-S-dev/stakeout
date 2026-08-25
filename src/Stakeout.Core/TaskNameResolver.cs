using System.Text.RegularExpressions;

namespace Stakeout.Core;

/// <summary>
/// スレッドからタスク名を決める（design.md §9.6）。
///
/// 決め方は 3 段階。
/// 1. スレッドに名前が付いていればそれ
/// 2. 無ければ、スタック最下段付近のユーザーコードの関数名を設定の正規表現に当てる
/// 3. どちらも無ければ null
///
/// COM に触れないので単体テストできる。
/// </summary>
public sealed class TaskNameResolver
{
    private readonly (Regex Pattern, string Name)[] _patterns;

    public TaskNameResolver(IReadOnlyList<TaskEntryPattern> patterns)
    {
        _patterns = patterns
            .Select(p => (Compile(p.Pattern), p.Name))
            .Where(p => p.Item1 is not null)
            .Select(p => (p.Item1!, p.Name))
            .ToArray();
    }

    /// <summary>設定が空なら、スレッド名だけを使う解決器になる。</summary>
    public bool HasPatterns => _patterns.Length > 0;

    /// <param name="threadName">スレッドに付いている名前。無ければ空文字。</param>
    /// <param name="stackFunctions">最上段から最下段への関数名。</param>
    public string? Resolve(string? threadName, IReadOnlyList<string> stackFunctions)
    {
        if (!string.IsNullOrWhiteSpace(threadName))
        {
            return threadName;
        }

        // 最下段に近いほどエントリ関数に近い。深いほうから当てる
        for (var i = stackFunctions.Count - 1; i >= 0; i--)
        {
            if (Match(stackFunctions[i]) is { } name)
            {
                return name;
            }
        }

        return null;
    }

    /// <summary>1 つの関数名にパターンを当てる。</summary>
    public string? Match(string function)
    {
        // VS の関数名には "Task_A_Main(void *)" のように引数が付く。
        // パターンを書く人に $ の扱いを気にさせないよう、こちら側で落とす
        var bare = StripSignature(function);

        foreach (var (pattern, name) in _patterns)
        {
            var match = pattern.Match(bare);
            if (match.Success)
            {
                return match.Result(name);
            }
        }

        return null;
    }

    /// <summary>"Task_A_Main(void *)" → "Task_A_Main"、"NativeLib.dll!nl_foo" → "nl_foo"。</summary>
    public static string StripSignature(string function)
    {
        var text = function;

        var bang = text.LastIndexOf('!');
        if (bang >= 0 && bang + 1 < text.Length)
        {
            text = text[(bang + 1)..];
        }

        var paren = text.IndexOf('(');
        if (paren >= 0)
        {
            text = text[..paren];
        }

        return text.Trim();
    }

    private static Regex? Compile(string pattern)
    {
        try
        {
            return new Regex(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant);
        }
        catch (ArgumentException)
        {
            // 設定の正規表現が壊れている。タスク名が付かないだけで、
            // 調査自体は続けられる。ここで落とさない
            return null;
        }
    }
}
