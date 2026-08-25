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
            Path.Combine(workingDirectory, ProjectFileName),
        };

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

            Merge(merged, layer);
            loaded.Add(path);
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

        return new LoadedConfig(config, loaded);
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
