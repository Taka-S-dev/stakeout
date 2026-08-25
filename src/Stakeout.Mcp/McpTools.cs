using System.Text.Json;
using System.Text.Json.Nodes;
using Stakeout.Rpc;

namespace Stakeout.Mcp;

/// <param name="Name">MCP に見せる道具の名前。</param>
/// <param name="Description">何をするか。**短く書く。**</param>
/// <param name="Schema">引数のスキーマ。</param>
/// <param name="Method">対応する stakeout の RPC メソッド。</param>
/// <param name="Build">MCP の引数を RPC の引数に変える。</param>
public sealed record McpTool(
    string Name,
    string Description,
    JsonObject Schema,
    string Method,
    Func<JsonElement, object?> Build);

/// <summary>
/// MCP に見せる道具（design.md §20 Phase 7）。
///
/// **低レベル操作は載せない。** ツール定義はクライアントの文脈を毎回消費するので、
/// 「調査 1 手」になっている複合コマンドと、それに要る最小限だけを出す。
/// 細かい操作が要るなら CLI を使えばよい。
///
/// 定義の合計が 4 KiB を超えないこと（design.md §20 Phase 7 の完了条件）。
/// </summary>
internal static class McpTools
{
    public static IReadOnlyList<McpTool> All { get; } = Build();

    /// <summary>MCP に送る形の JSON 設定。本体と検証で同じものを使う。</summary>
    public static readonly JsonSerializerOptions WireJson = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>tools/list に載せる配列。</summary>
    public static JsonArray Describe()
    {
        var tools = new JsonArray();

        foreach (var tool in All)
        {
            tools.Add(new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["inputSchema"] = tool.Schema.DeepClone(),
            });
        }

        return tools;
    }

    /// <summary>
    /// 実際に送られる形での大きさ。
    /// 測り方が本体と検証で違うと、片方だけが上限を守っているつもりになる
    /// （最初の実装では 3535 と 4943 に割れていた）。
    /// </summary>
    public static int DescribeByteCount() =>
        System.Text.Encoding.UTF8.GetByteCount(Describe().ToJsonString(WireJson));

    private static IReadOnlyList<McpTool> Build() => new[]
    {
        new McpTool(
            "stakeout_attach",
            "実行中のプロセスにアタッチする。pid か name のどちらかを渡す。",
            Object(
                ("pid", Integer("プロセス ID")),
                ("name", String("プロセス名の正規表現")),
                ("vsPid", Integer("使う Visual Studio の pid"))),
            RpcMethods.SessionAttach,
            args => new AttachRequest
            {
                Pid = OptionalInt(args, "pid"),
                Name = OptionalString(args, "name"),
                VsPid = OptionalInt(args, "vsPid"),
            }),

        new McpTool(
            "stakeout_detach",
            "デタッチする。Target は動いたまま残る。",
            Object(),
            RpcMethods.SessionDetach,
            _ => null),

        new McpTool(
            "stakeout_wait",
            "停止するまで待つ。stopped が false なら、まだ実行中。もう一度呼べばよい。",
            Object(("timeoutSec", Integer("待つ秒数。既定 20"))),
            RpcMethods.ExecWait,
            args => new WaitRequest { TimeoutMs = OptionalInt(args, "timeoutSec") * 1000 }),

        new McpTool(
            "stakeout_run_until",
            "指定位置に到達するまで実行し、停止位置のスタックと式の値をまとめて返す。",
            Object(
                ("location", Required(String("FILE:LINE または関数名"))),
                ("condition", String("条件式")),
                ("exprs", StringArray("停止時に評価する式")),
                ("timeoutSec", Integer("待つ秒数。既定 60"))),
            RpcMethods.CompositeRunUntil,
            args => new RunUntilRequest
            {
                Location = RequiredString(args, "location"),
                Condition = OptionalString(args, "condition"),
                Exprs = OptionalStringArray(args, "exprs"),
                TimeoutMs = OptionalInt(args, "timeoutSec") * 1000,
            }),

        new McpTool(
            "stakeout_watch_until_change",
            "式が指すメモリへの書き込みを捕まえ、値が変わるまで追う。書き込み元のタスク名が付く。",
            Object(
                ("expr", Required(String("監視する式。別モジュールなら {,,DLL名} を付ける"))),
                ("maxHits", Integer("見る書き込みの回数。既定 20")),
                ("timeoutSec", Integer("待つ秒数。既定 60"))),
            RpcMethods.CompositeWatchUntilChange,
            args => new WatchUntilChangeRequest
            {
                Expr = RequiredString(args, "expr"),
                MaxHits = OptionalInt(args, "maxHits") is { } hits and > 0 ? hits : 20,
                TimeoutMs = OptionalInt(args, "timeoutSec") * 1000,
            }),

        new McpTool(
            "stakeout_find_corruption",
            "そのシンボルに書いていそうな場所を静的に挙げ、実際の書き込みと突き合わせ、候補に無いものを名指しする。",
            Object(
                ("symbol", Required(String("監視する式"))),
                ("maxHits", Integer("捕まえる書き込みの回数。既定 10")),
                ("timeoutSec", Integer("待つ秒数。既定 60"))),
            RpcMethods.CompositeFindCorruption,
            args => new FindCorruptionRequest
            {
                Symbol = RequiredString(args, "symbol"),
                MaxHits = OptionalInt(args, "maxHits") is { } hits and > 0 ? hits : 10,
                TimeoutMs = OptionalInt(args, "timeoutSec") * 1000,
            }),

        new McpTool(
            "stakeout_stack",
            "コールスタックを返す。all を true にすると全スレッド（既定の深さ 5）。",
            Object(
                ("all", Boolean("全スレッドを取る")),
                ("threadId", Integer("対象スレッド")),
                ("maxDepth", Integer("深さ"))),
            RpcMethods.StackGet,
            args => new StackRequest
            {
                All = OptionalBool(args, "all"),
                ThreadId = OptionalInt(args, "threadId"),
                MaxDepth = OptionalInt(args, "maxDepth"),
            }),

        new McpTool(
            "stakeout_eval",
            "式を評価する。停止中のみ。別モジュールのグローバルには {,,DLL名} を付ける。",
            Object(
                ("expr", Required(String("C/C++ の式"))),
                ("format", String("書式指定子。x/d/s など"))),
            RpcMethods.VarsEval,
            args => new EvalRequest
            {
                Expr = RequiredString(args, "expr"),
                Format = OptionalString(args, "format"),
            }),

        new McpTool(
            "stakeout_mem",
            "メモリを読む。address はアドレス(0x..)かアドレスになる式(&g_ctx)。停止中のみ。",
            Object(
                ("address", Required(String("0x... または &expr"))),
                ("length", Integer("バイト数。既定 64"))),
            RpcMethods.MemRead,
            args => new MemReadRequest
            {
                Address = RequiredString(args, "address"),
                Length = OptionalInt(args, "length") ?? 64,
            }),

        new McpTool(
            "stakeout_dump",
            "構造体やポインタを再帰的に展開し、経路付きの一覧で返す。",
            Object(
                ("expr", Required(String("展開する式"))),
                ("depth", Integer("段数。既定 2")),
                ("maxItems", Integer("1 段あたりの要素数。既定 50"))),
            RpcMethods.VarsDump,
            args => new DumpRequest
            {
                Expr = RequiredString(args, "expr"),
                Depth = OptionalInt(args, "depth") is { } depth and > 0 ? depth : 2,
                MaxItems = OptionalInt(args, "maxItems") is { } items and > 0 ? items : 50,
            }),

        new McpTool(
            "stakeout_task_map",
            "スレッドとタスク名の対応を返す。",
            Object(),
            RpcMethods.CompositeTaskMap,
            _ => null),
    };

    // ---------------------------------------------------------------- schema helpers

    private static JsonObject Object(params (string Name, JsonObject Property)[] properties)
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject(),
        };

        var required = new JsonArray();

        foreach (var (name, property) in properties)
        {
            if (property.Remove("__required"))
            {
                required.Add(name);
            }

            ((JsonObject)schema["properties"]!)[name] = property;
        }

        if (required.Count > 0)
        {
            schema["required"] = required;
        }

        return schema;
    }

    private static JsonObject String(string description) =>
        new() { ["type"] = "string", ["description"] = description };

    private static JsonObject Integer(string description) =>
        new() { ["type"] = "integer", ["description"] = description };

    private static JsonObject Boolean(string description) =>
        new() { ["type"] = "boolean", ["description"] = description };

    private static JsonObject StringArray(string description) => new()
    {
        ["type"] = "array",
        ["items"] = new JsonObject { ["type"] = "string" },
        ["description"] = description,
    };

    private static JsonObject Required(JsonObject property)
    {
        property["__required"] = true;
        return property;
    }

    // ---------------------------------------------------------------- argument helpers

    private static string RequiredString(JsonElement args, string name) =>
        OptionalString(args, name)
        ?? throw new McpException($"引数 '{name}' が要ります。");

    private static string? OptionalString(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object
        && args.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? OptionalInt(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object
        && args.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var number)
            ? number
            : null;

    private static bool OptionalBool(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object
        && args.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.True;

    private static string[]? OptionalStringArray(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object
            || !args.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return value.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!)
            .ToArray();
    }
}

/// <summary>MCP のクライアントに返すべき使い方の誤り。</summary>
internal sealed class McpException : Exception
{
    public McpException(string message) : base(message)
    {
    }
}
