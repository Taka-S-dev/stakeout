using System.Text.Json;
using System.Text.Json.Nodes;

namespace Stakeout.Core;

/// <param name="Config">合成後の設定。</param>
/// <param name="LoadedPaths">実際に読み込んだファイル。優先度の低い順。</param>
public sealed record LoadedConfig(StakeoutConfig Config, IReadOnlyList<string> LoadedPaths);

/// <summary>
/// 設定の読み込み（design.md §16）。
/// 探索順は <c>%APPDATA%\stakeout\stakeout.json</c> → <c>./.stakeout.json</c> で、
/// **後から読んだもの（プロジェクト直下）が勝つ**。
///
/// 合成はレコード単位の上書きではなく JSON の深いマージで行う。
/// そうしないと、プロジェクト側で <c>limits.waitSec</c> だけ変えたつもりが
/// <c>limits</c> ごと既定値に戻る、という直感に反する挙動になる。
/// </summary>
public static class ConfigLoader
{
    public const string ProjectFileName = ".stakeout.json";
    public const string UserFileName = "stakeout.json";

    /// <summary>既定の探索パスを組み立てる。優先度の低い順に返す。</summary>
    public static IReadOnlyList<string> DefaultSearchPaths(string workingDirectory) =>
        new[]
        {
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "stakeout",
                UserFileName),
            FindProjectFile(workingDirectory) ?? Path.Combine(workingDirectory, ProjectFileName),
        };

    /// <summary>
    /// 作業ディレクトリから親へ遡って、最も近い <c>.stakeout.json</c> を探す（ADR 0023）。見つからなければ null。
    ///
    /// 直下しか見ないと、エージェントが <c>cd</c> した先で自動起動したデーモンが設定を読まず、
    /// attach が DENIED になる（評価で実際に起きた）。
    /// </summary>
    public static string? FindProjectFile(string workingDirectory)
    {
        for (var dir = new DirectoryInfo(workingDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, ProjectFileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>既定の探索順で読み込む。</summary>
    public static LoadedConfig Load(string workingDirectory) =>
        LoadFrom(DefaultSearchPaths(workingDirectory));

    /// <summary>
    /// 指定したパスを優先度の低い順に読み込んで合成する。存在しないパスは黙って飛ばす。
    /// </summary>
    /// <exception cref="ConfigException">JSON として壊れているファイルがあった場合。</exception>
    public static LoadedConfig LoadFrom(IEnumerable<string> pathsLowestPriorityFirst)
    {
        var merged = new JsonObject();
        var loaded = new List<string>();

        // code.gtagsRoot を最後に書いた設定ファイルのディレクトリ。相対パスの基準にする
        string? gtagsBase = null;

        foreach (var path in pathsLowestPriorityFirst)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            JsonObject layer;
            try
            {
                var node = JsonNode.Parse(
                    File.ReadAllText(path),
                    documentOptions: new JsonDocumentOptions
                    {
                        CommentHandling = JsonCommentHandling.Skip,
                        AllowTrailingCommas = true,
                    });

                layer = node as JsonObject
                        ?? throw new ConfigException(path, "設定ファイルのトップレベルはオブジェクトでなければならない");
            }
            catch (JsonException ex)
            {
                throw new ConfigException(path, ex.Message, ex);
            }

            RejectUserOnlyKeys(path, layer);
            Merge(merged, layer);
            loaded.Add(path);

            if (layer["code"] is JsonObject code && code["gtagsRoot"] is JsonValue root && root.TryGetValue<string>(out _))
            {
                gtagsBase = Path.GetDirectoryName(Path.GetFullPath(path));
            }
        }

        StakeoutConfig config;
        try
        {
            config = merged.Deserialize<StakeoutConfig>(ConfigJson.Options) ?? new StakeoutConfig();
        }
        catch (JsonException ex)
        {
            throw new ConfigException(
                loaded.Count > 0 ? loaded[^1] : "(none)",
                $"設定の型が合わない: {ex.Message}",
                ex);
        }

        // 相対パスは、それを書いた設定ファイルの場所を基準にする（ADR 0023）。
        // 親から見つけた設定の相対パスを作業ディレクトリ基準で解くと、また外れる
        if (config.Code.GtagsRoot is { Length: > 0 } gtagsRoot && !Path.IsPathRooted(gtagsRoot) && gtagsBase is not null)
        {
            config = config with
            {
                Code = config.Code with { GtagsRoot = Path.GetFullPath(Path.Combine(gtagsBase, gtagsRoot)) },
            };
        }

        return new LoadedConfig(config, loaded);
    }

    /// <summary>
    /// ユーザー設定にしか書けない項目が、プロジェクト設定（<c>.stakeout.json</c>）にあれば止める（ADR 0024）。
    /// 黙って無視すると「書いたのに効かない」になり、黙って効かせると「クローンしただけで
    /// 管理者デバッガへの経路が開く」になる。どちらも避けて、理由を言って止まる。
    /// </summary>
    private static void RejectUserOnlyKeys(string path, JsonObject layer)
    {
        if (!string.Equals(Path.GetFileName(path), ProjectFileName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (layer["pipe"] is JsonObject pipe && pipe.ContainsKey("allowUnelevatedClients"))
        {
            throw new ConfigException(
                path,
                "pipe.allowUnelevatedClients はユーザー設定（%APPDATA%\\stakeout\\stakeout.json）にしか書けません。" +
                "リポジトリの設定で管理者デーモンへの接続を開かないためです（ADR 0024）");
        }
    }

    /// <summary>
    /// <paramref name="overlay"/> を <paramref name="target"/> に深くマージする。
    /// オブジェクト同士は再帰的に混ぜ、それ以外（配列・スカラ）は overlay で置き換える。
    /// 配列を要素単位でマージしないのは、allowProcesses のようなリストを
    /// 「プロジェクト側で完全に上書きしたい」場面のほうが多いためである。
    /// </summary>
    private static void Merge(JsonObject target, JsonObject overlay)
    {
        foreach (var (key, value) in overlay)
        {
            if (value is JsonObject nested && target[key] is JsonObject existing)
            {
                Merge(existing, nested);
                continue;
            }

            target[key] = value?.DeepClone();
        }
    }
}

/// <summary>設定ファイルが読めない・型が合わないときに投げる。</summary>
public sealed class ConfigException : Exception
{
    public ConfigException(string path, string message, Exception? inner = null)
        : base($"{path}: {message}", inner)
    {
        Path = path;
    }

    /// <summary>問題のあった設定ファイル。</summary>
    public string Path { get; }
}
