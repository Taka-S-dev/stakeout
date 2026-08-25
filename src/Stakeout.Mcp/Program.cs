using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Stakeout.Client;
using Stakeout.Rpc;

namespace Stakeout.Mcp;

/// <summary>
/// MCP の stdio サーバー（design.md §4.1 / §20 Phase 7）。
///
/// stakeout の RPC への**薄いアダプタ**である。ここに調査の判断を入れない。
/// 判断は Skill にあり、機能はデーモンにある。この層がやるのは、
/// MCP の呼び出しを RPC に写して、結果をそのまま返すことだけ。
///
/// シェルを持つクライアントでは CLI で足りる（design.md §4.3）。
/// これはシェルの無いクライアント向けの入口である。
/// </summary>
internal static class Program
{
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>複合コマンドは内部で待つ。RPC 側のタイムアウトはそれより長く取る。</summary>
    private static readonly TimeSpan RpcTimeout = TimeSpan.FromMinutes(5);

    private static async Task<int> Main(string[] args)
    {
        // MCP は UTF-8 で話す。既定（CP932）のままだと日本語の説明が化ける。
        // 道具一覧を出すだけの経路でも同じなので、分岐より前に設定する
        Console.OutputEncoding = new UTF8Encoding(false);

        if (args.Contains("--tools"))
        {
            // 道具の定義とその大きさを出す。4 KiB を超えていないかを測るため
            return DescribeTools();
        }

        var client = new DaemonClient(RpcTransport.PipeName);
        using var stdin = Console.OpenStandardInput();
        using var reader = new StreamReader(stdin, new UTF8Encoding(false));

        while (await reader.ReadLineAsync() is { } line)
        {
            if (line.Trim().Length == 0)
            {
                continue;
            }

            var response = await HandleAsync(client, line, CancellationToken.None);
            if (response is null)
            {
                // 通知には応答しない
                continue;
            }

            Console.Out.WriteLine(response);
            await Console.Out.FlushAsync();
        }

        return 0;
    }

    private static async Task<string?> HandleAsync(DaemonClient client, string line, CancellationToken ct)
    {
        JsonNode? request;
        try
        {
            request = JsonNode.Parse(line);
        }
        catch (JsonException ex)
        {
            return Error(null, -32700, $"JSON として読めません: {ex.Message}");
        }

        if (request is not JsonObject message)
        {
            return Error(null, -32600, "オブジェクトではありません。");
        }

        var id = message["id"];
        var method = message["method"]?.GetValue<string>();

        // 通知（id 無し）には応答しない
        if (id is null)
        {
            return null;
        }

        return method switch
        {
            "initialize" => Initialize(id),
            "tools/list" => ToolsList(id),
            "tools/call" => await ToolsCallAsync(client, id, message["params"], ct),
            "ping" => Result(id, new JsonObject()),
            _ => Error(id, -32601, $"未知のメソッドです: {method}"),
        };
    }

    private static string Initialize(JsonNode id) => Result(id, new JsonObject
    {
        ["protocolVersion"] = "2024-11-05",
        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
        ["serverInfo"] = new JsonObject
        {
            ["name"] = "stakeout",
            ["version"] = Core.StakeoutPaths.Version,
        },
    });

    private static string ToolsList(JsonNode id) =>
        Result(id, new JsonObject { ["tools"] = McpTools.Describe() });

    private static async Task<string> ToolsCallAsync(
        DaemonClient client, JsonNode id, JsonNode? parameters, CancellationToken ct)
    {
        var name = parameters?["name"]?.GetValue<string>();
        var tool = McpTools.All.FirstOrDefault(t => t.Name == name);

        if (tool is null)
        {
            return Error(id, -32602, $"未知の道具です: {name}");
        }

        var arguments = parameters?["arguments"] is { } node
            ? JsonSerializer.Deserialize<JsonElement>(node.ToJsonString())
            : default;

        object? request;
        try
        {
            request = tool.Build(arguments);
        }
        catch (McpException ex)
        {
            return ToolFailure(id, ex.Message);
        }

        // デバッグ操作はセッションを前提にする。attach だけはデーモンを起こしてよい
        var autoStart = tool.Method == RpcMethods.SessionAttach;
        var result = await client.InvokeAsync(tool.Method, request, autoStart, RpcTimeout, ct);

        if (!result.Ok)
        {
            var error = result.Error!;

            // hint を落とさない。次に何をすればよいかが、そこにしか書いていない
            return ToolFailure(id, $"{error.Code}: {error.Message}\nhint: {error.Hint}");
        }

        var payload = new JsonObject
        {
            ["ok"] = true,
            ["data"] = result.Data.ValueKind == JsonValueKind.Undefined
                ? null
                : JsonNode.Parse(result.Data.GetRawText()),
        };

        if (result.Truncated)
        {
            payload["truncated"] = true;
            payload["cursor"] = result.Cursor;
        }

        return Result(id, new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = payload.ToJsonString(Json),
                },
            },
        });
    }

    /// <summary>
    /// 道具の実行結果としての失敗。プロトコルのエラーとは分ける。
    /// プロトコルのエラーにすると、クライアントによっては会話が止まる。
    /// </summary>
    private static string ToolFailure(JsonNode id, string text) => Result(id, new JsonObject
    {
        ["isError"] = true,
        ["content"] = new JsonArray
        {
            new JsonObject { ["type"] = "text", ["text"] = text },
        },
    });

    private static string Result(JsonNode id, JsonObject result) => new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id.DeepClone(),
        ["result"] = result,
    }.ToJsonString(Json);

    private static string Error(JsonNode? id, int code, string message) => new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    }.ToJsonString(Json);

    /// <summary>
    /// 道具の定義とその大きさを出す。
    /// クライアントの文脈を毎回消費するので、大きさは測って上限を守る。
    /// </summary>
    private static int DescribeTools()
    {
        var bytes = McpTools.DescribeByteCount();

        Console.WriteLine(McpTools.Describe().ToJsonString(McpTools.WireJson));
        Console.Error.WriteLine();
        Console.Error.WriteLine($"道具 {McpTools.All.Count} 個 / 定義 {bytes} バイト（上限 4096）");

        return bytes <= 4096 ? 0 : 1;
    }
}
