namespace Stakeout.Backend.EnvDte;

/// <summary>
/// ファイル名だけ（または相対パス）で指定された行ブレークポイントを、
/// どのフルパスで張り直すかの候補（ADR 0023）。
///
/// ファイル名だけの指定は、Visual Studio が開いている同名の別ファイルに解決されることがある。
/// そのブレークポイントは一度も止まらず、エラーにもならない。
///
/// COM に触れないので単体テストできる。
/// </summary>
internal static class SourceFileCandidates
{
    /// <summary>索引ルートから拾う候補の上限。巨大なソースツリーで止まらないようにする。</summary>
    private const int MaxFromRoot = 20;

    /// <param name="file">利用者が指定したファイル。</param>
    /// <param name="sourceRoot">調査対象のソースツリー（code.gtagsRoot）。無ければ null。</param>
    /// <param name="openDocuments">Visual Studio が開いているドキュメントのフルパス。</param>
    public static IReadOnlyList<string> For(string file, string? sourceRoot, IEnumerable<string> openDocuments)
    {
        if (string.IsNullOrWhiteSpace(file) || Path.IsPathRooted(file))
        {
            // フルパスは指定どおりに扱う。推測で別のファイルに張り替えない
            return Array.Empty<string>();
        }

        var suffix = Normalize(file);
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string path)
        {
            string full;
            try
            {
                full = Path.GetFullPath(path);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return;
            }

            // 消えたファイルは候補にしない。削除済みの作業コピーを開いたままの VS はよくある
            if (EndsWithPath(full, suffix) && File.Exists(full) && seen.Add(full))
            {
                result.Add(full);
            }
        }

        // 索引ルートを先に見る。調査対象のソースツリーを指しているはずの設定である
        if (sourceRoot is { Length: > 0 } && Directory.Exists(sourceRoot))
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                MatchCasing = MatchCasing.CaseInsensitive,
            };

            try
            {
                foreach (var path in Directory.EnumerateFiles(sourceRoot, Path.GetFileName(suffix), options))
                {
                    Add(path);
                    if (result.Count >= MaxFromRoot)
                    {
                        break;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 走査できなくても、開いているドキュメントからは探せる
            }
        }

        foreach (var document in openDocuments)
        {
            if (document is { Length: > 0 })
            {
                Add(document);
            }
        }

        return result;
    }

    private static string Normalize(string path) =>
        path.Replace('/', '\\').TrimStart('.', '\\');

    private static bool EndsWithPath(string fullPath, string suffix) =>
        fullPath.EndsWith("\\" + suffix, StringComparison.OrdinalIgnoreCase)
        || string.Equals(fullPath, suffix, StringComparison.OrdinalIgnoreCase);
}
