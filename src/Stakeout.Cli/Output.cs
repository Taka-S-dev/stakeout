using System.Text.Encodings.Web;
using System.Text.Json;
using Stakeout.Client;
using Stakeout.Rpc;

namespace Stakeout.Cli;

/// <summary>
/// 出力（design.md §8.2）。
///
/// - 既定は人間向けテキスト
/// - <c>--json</c> または <c>DBG_JSON=1</c> なら RPC の data をそのまま stdout
/// - エラーは常に stderr。<c>--json</c> のときは JSON、そうでなければ 1 行テキスト
///
/// エージェントから使うときは常に <c>--json</c> にする（design.md §17）。
/// </summary>
public sealed class Output
{
    public const string JsonEnvironmentVariable = "DBG_JSON";

    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        // stdout に出す JSON は HTML に埋めない。+ や日本語をエスケープすると
        // エージェントにも人間にも読みにくくなるだけである
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,

        // data は RPC から来たものをそのまま出すので既に camelCase である。
        // CLI 側で組み立てるエラーだけ PascalCase になると、
        // エージェントが 2 通りのキーを扱うことになる
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public Output(bool jsonFlag)
    {
        Json = jsonFlag
               || Environment.GetEnvironmentVariable(JsonEnvironmentVariable) is "1" or "true";
    }

    /// <summary>JSON 出力モードか。</summary>
    public bool Json { get; }

    /// <summary>成功時のペイロードを書き、終了コードを返す。</summary>
    public int WriteSuccess(JsonElement data, Action<JsonElement> writeText) =>
        WriteSuccess(data, writeText, truncated: false, cursor: null);

    /// <summary>
    /// 成功時のペイロードを書く。
    ///
    /// <c>--json</c> では **封筒ごと** 出す（ADR 0015）。打ち切りとカーソルは
    /// 本文と一緒に運ばなければならない。別の流れ（標準エラー）に出すと、
    /// 出力をまとめて受け取る側で JSON が壊れる。
    /// </summary>
    public int WriteSuccess(JsonElement data, Action<JsonElement> writeText, bool truncated, string? cursor)
    {
        if (Json)
        {
            var envelope = new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["data"] = data.ValueKind == JsonValueKind.Undefined ? null : (object)data,
            };

            if (truncated)
            {
                envelope["truncated"] = true;
                envelope["cursor"] = cursor;
            }

            Console.Out.WriteLine(JsonSerializer.Serialize(envelope, Pretty));
        }
        else
        {
            writeText(data);

            if (truncated && cursor is { Length: > 0 })
            {
                Console.Out.WriteLine();
                Console.Out.WriteLine($"... 続きがあります。--cursor {cursor} で取得してください（10 分で失効）");
            }
        }

        return ExitCode.Ok;
    }

    /// <summary>エラーを stderr に書き、対応する終了コードを返す。</summary>
    public int WriteError(StakeoutError error)
    {
        if (Json)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(
                new Dictionary<string, object?> { ["ok"] = false, ["error"] = error }, Pretty));
        }
        else
        {
            Console.Error.WriteLine($"{error.Code}: {error.Message}");
            if (!string.IsNullOrWhiteSpace(error.Hint))
            {
                Console.Error.WriteLine($"hint: {error.Hint}");
            }
        }

        return ExitCode.FromErrorCode(error.Code);
    }

    /// <summary>結果をそのまま処理する。成功なら <paramref name="writeText"/>、失敗ならエラー出力。</summary>
    public int Write(CliResult result, Action<JsonElement> writeText)
    {
        if (!result.Ok)
        {
            return WriteError(result.Error ?? new StakeoutError(
                ErrorCodes.Internal, "デーモンが不正な応答を返しました。", "デーモンのログを確認してください。"));
        }

        // 続きがあることを黙って落とさない。切られたと知らなければ、
        // 読む側は「これで全部だ」と誤解する（design.md §8.5）
        return WriteSuccess(result.Data, writeText, result.Truncated, result.Cursor);
    }

    /// <summary>人間向けの 1 行を書く（JSON モードでは何もしない）。</summary>
    public void Line(string text)
    {
        if (!Json)
        {
            Console.Out.WriteLine(text);
        }
    }

    /// <summary>JsonElement からプロパティを文字列で取る。無ければ既定値。</summary>
    public static string Get(JsonElement element, string name, string fallback = "-") =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? fallback,
                JsonValueKind.Null or JsonValueKind.Undefined => fallback,
                _ => value.ToString(),
            }
            : fallback;
}
