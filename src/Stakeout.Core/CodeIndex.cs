using System.Diagnostics;
using Stakeout.Rpc;

namespace Stakeout.Core;

/// <summary>
/// gtags による静的シンボル索引（design.md §13）。
///
/// デバッガに一切依存しない。Target が動いていなくても使える。
/// 動的な調査の**前**に候補を絞るためのものである。
/// </summary>
public sealed class CodeIndex
{
    /// <summary>gtags の呼び出しに許す時間。索引が壊れていると返ってこないことがある。</summary>
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(20);

    private readonly string? _root;

    public CodeIndex(CodeIndexConfig config) => _root = config.GtagsRoot;

    /// <summary>索引が使えるか。</summary>
    public bool IsConfigured => _root is { Length: > 0 } && Directory.Exists(_root);

    /// <summary>シンボルの定義を探す。</summary>
    public IReadOnlyList<CodeLocation> Definitions(string symbol)
    {
        EnsureConfigured();
        return Query(new[] { "--result=grep", symbol }, symbol, CodeMatchKind.Definition);
    }

    /// <summary>
    /// シンボルの参照を探す。
    ///
    /// gtags の参照索引は、変数がマクロ付きで宣言されていると空になることがある
    /// （<c>NL_API extern Shared g_shared;</c> など）。空だったときは
    /// 索引済みファイルへのテキスト検索に落とし、その旨を印として返す（ADR 0017）。
    /// </summary>
    public IReadOnlyList<CodeLocation> References(string symbol)
    {
        EnsureConfigured();

        var references = Query(new[] { "-r", "--result=grep", symbol }, symbol, CodeMatchKind.Reference);
        if (references.Count > 0)
        {
            return references;
        }

        return Query(new[] { "-g", "--result=grep", symbol }, symbol, CodeMatchKind.TextSearch);
    }

    /// <summary>
    /// そのシンボルに書いていそうな場所を探す（design.md §13）。
    /// 確信度付きで返す。**これは候補であって、犯人ではない。**
    /// </summary>
    public IReadOnlyList<WriteSite> Writers(string expr)
    {
        EnsureConfigured();

        // {,,NativeLib.dll} のような修飾はデバッガ向けの記法であって、ソースには無い。
        // 付けたまま照合すると、どの行にも当たらず「候補ゼロ」になる
        expr = WriteSiteDetector.StripModuleQualifier(expr);

        var baseSymbol = WriteSiteDetector.BaseSymbol(expr);
        if (baseSymbol.Length == 0)
        {
            throw new BackendException(
                ErrorCodes.Usage,
                $"'{expr}' からシンボル名を取り出せません。",
                "g_shared.counter のように、変数名から始まる式を指定してください。");
        }

        // 索引の種類に関わらずテキストで拾う。書き込み判定は行の中身を見るので、
        // どのみち行のテキストが要る
        var hits = Query(new[] { "-g", "--result=grep", baseSymbol }, baseSymbol, CodeMatchKind.TextSearch);
        var functions = new Dictionary<string, IReadOnlyList<(int Line, string Name)>>(StringComparer.OrdinalIgnoreCase);
        var sites = new List<WriteSite>();

        foreach (var hit in hits)
        {
            var verdict = WriteSiteDetector.Classify(hit.Text, expr);
            if (verdict.Confidence == WriteConfidence.None)
            {
                continue;
            }

            if (!functions.TryGetValue(hit.File, out var definitions))
            {
                definitions = DefinitionsIn(hit.File);
                functions[hit.File] = definitions;
            }

            sites.Add(new WriteSite(
                File: hit.File,
                Line: hit.Line,
                Text: hit.Text.Trim(),
                Function: EnclosingFunction(definitions, hit.Line),
                Confidence: verdict.Confidence.ToString().ToLowerInvariant(),
                Reason: verdict.Reason));
        }

        return sites
            .OrderByDescending(s => s.Confidence == "high")
            .ThenBy(s => s.File, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Line)
            .ToArray();
    }

    /// <summary>そのファイルにある定義を行番号付きで返す。囲んでいる関数を求めるのに使う。</summary>
    private IReadOnlyList<(int Line, string Name)> DefinitionsIn(string file)
    {
        var definitions = Query(new[] { "-f", "--result=grep", file }, file, CodeMatchKind.Definition);

        return definitions
            .Select(d => (d.Line, Name: ExtractName(d.Text)))
            .Where(d => d.Name.Length > 0)
            .OrderBy(d => d.Line)
            .ToArray();
    }

    /// <summary>行番号を囲む定義のうち、直前にあるものを返す。</summary>
    private static string? EnclosingFunction(IReadOnlyList<(int Line, string Name)> definitions, int line)
    {
        string? found = null;

        foreach (var (definitionLine, name) in definitions)
        {
            if (definitionLine > line)
            {
                break;
            }

            found = name;
        }

        return found;
    }

    /// <summary>"void nl_stray_write(long value)" -> "nl_stray_write"。</summary>
    private static string ExtractName(string text)
    {
        var paren = text.IndexOf('(');
        var head = paren >= 0 ? text[..paren] : text;

        var end = head.Length;
        while (end > 0 && !char.IsLetterOrDigit(head[end - 1]) && head[end - 1] != '_')
        {
            end--;
        }

        var start = end;
        while (start > 0 && (char.IsLetterOrDigit(head[start - 1]) || head[start - 1] == '_'))
        {
            start--;
        }

        return end > start ? head[start..end] : string.Empty;
    }

    private IReadOnlyList<CodeLocation> Query(string[] arguments, string symbol, CodeMatchKind kind)
    {
        var output = RunGlobal(arguments);
        var results = new List<CodeLocation>();

        foreach (var line in output.Split('\n'))
        {
            // file:line:text
            var text = line.TrimEnd('\r');
            if (text.Length == 0)
            {
                continue;
            }

            var firstColon = text.IndexOf(':');
            if (firstColon < 0)
            {
                continue;
            }

            var secondColon = text.IndexOf(':', firstColon + 1);
            if (secondColon < 0 || !int.TryParse(text[(firstColon + 1)..secondColon], out var lineNumber))
            {
                continue;
            }

            results.Add(new CodeLocation(
                File: text[..firstColon].Replace('\\', '/'),
                Line: lineNumber,
                Text: text[(secondColon + 1)..],
                Symbol: symbol,
                Kind: kind));
        }

        return results;
    }

    private string RunGlobal(string[] arguments)
    {
        var info = new ProcessStartInfo("global")
        {
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info)
            ?? throw new BackendException(
                ErrorCodes.NotConfigured,
                "global コマンドを起動できません。",
                "GNU GLOBAL をインストールし、PATH に通してください。");

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();

        if (!process.WaitForExit((int)CommandTimeout.TotalMilliseconds))
        {
            process.Kill(entireProcessTree: true);
            throw new BackendException(
                ErrorCodes.Timeout,
                $"global が {CommandTimeout.TotalSeconds:F0} 秒以内に応答しませんでした。",
                $"{_root} で gtags を実行し直して索引を作り直してください。");
        }

        // 見つからない場合の終了コードは 0 以外だが、それはエラーではない
        if (process.ExitCode != 0 && stdout.Length == 0 && stderr.Contains("GTAGS not found", StringComparison.OrdinalIgnoreCase))
        {
            throw new BackendException(
                ErrorCodes.NotConfigured,
                $"{_root} に GTAGS がありません。",
                $"そのディレクトリで gtags を実行して索引を作ってください。");
        }

        return stdout;
    }

    private void EnsureConfigured()
    {
        if (IsConfigured)
        {
            return;
        }

        throw new BackendException(
            ErrorCodes.NotConfigured,
            _root is { Length: > 0 }
                ? $"code.gtagsRoot に指定されたディレクトリがありません: {_root}"
                : "code.gtagsRoot が設定されていません。",
            "stakeout.json の code.gtagsRoot に、GTAGS のあるディレクトリを指定してください。");
    }
}
