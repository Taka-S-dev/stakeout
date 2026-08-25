using System.Text.Json;
using System.Text.Json.Serialization;

namespace Stakeout.Core;

/// <summary>ログ 1 行の種別（design.md §14）。</summary>
public static class LogKinds
{
    public const string RpcRequest = "rpc.request";
    public const string RpcResponse = "rpc.response";
    public const string Stop = "stop";
    public const string Trace = "trace";
    public const string Daemon = "daemon";
}

/// <summary>
/// セッションログの 1 行（design.md §14）。
/// 調査レポートの根拠になるので、値は落とすより切って残す。
/// </summary>
public sealed record LogEntry
{
    [JsonPropertyName("ts")]
    public required DateTimeOffset Timestamp { get; init; }

    [JsonPropertyName("seq")]
    public required long Seq { get; init; }

    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("method")]
    public string? Method { get; init; }

    [JsonPropertyName("params")]
    public string? Params { get; init; }

    [JsonPropertyName("result")]
    public string? Result { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("durationMs")]
    public double? DurationMs { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    /// <summary>params / result のいずれかを切り詰めたか。</summary>
    [JsonPropertyName("truncated")]
    public bool Truncated { get; init; }
}

/// <summary>
/// JSONL のセッションログ（design.md §14）。
/// 追記のみ。複数スレッドから呼ばれるので書き込みは直列化する。
/// </summary>
public sealed class SessionLog : IDisposable
{
    /// <summary>params / result を切り詰める閾値（design.md §14）。</summary>
    public const int MaxFieldBytes = 8 * 1024;

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,

        // ログに残る params / result を、ワイヤ表現と同じ camelCase にする。
        // 食い違うとログを見ながらクエリを書くときに毎回つまずく
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly StreamWriter _writer;
    private readonly object _gate = new();
    private long _seq;

    private SessionLog(string path, StreamWriter writer)
    {
        Path = path;
        _writer = writer;
    }

    /// <summary>書き込み先。</summary>
    public string Path { get; }

    /// <summary>
    /// <paramref name="directory"/> に <c>yyyyMMdd-&lt;sessionId&gt;.jsonl</c> を作って開く。
    /// </summary>
    public static SessionLog Open(string directory, string sessionId, DateTimeOffset now)
    {
        Directory.CreateDirectory(directory);
        var path = System.IO.Path.Combine(directory, $"{now:yyyyMMdd}-{sessionId}.jsonl");

        // 書き込み中でも読めるようにする。stakeout log tail は動いているデーモンの
        // ログを読むので、共有を絞ると自分のログが読めなくなる
        var stream = new FileStream(
            path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite,
            bufferSize: 4096, FileOptions.SequentialScan);

        return new SessionLog(path, new StreamWriter(stream) { AutoFlush = true });
    }

    /// <summary>デーモン自身の出来事を記録する。</summary>
    public void Daemon(string message) =>
        Write(new LogEntry
        {
            Timestamp = DateTimeOffset.Now,
            Seq = 0,
            Kind = LogKinds.Daemon,
            Message = message,
        });

    /// <summary>RPC の要求を記録する。</summary>
    public void RpcRequest(string method, object? parameters)
    {
        var (json, truncated) = Serialize(parameters);
        Write(new LogEntry
        {
            Timestamp = DateTimeOffset.Now,
            Seq = 0,
            Kind = LogKinds.RpcRequest,
            Method = method,
            Params = json,
            Truncated = truncated,
        });
    }

    /// <summary>RPC の応答を記録する。</summary>
    public void RpcResponse(string method, object? result, string? error, double durationMs)
    {
        var (json, truncated) = Serialize(result);
        Write(new LogEntry
        {
            Timestamp = DateTimeOffset.Now,
            Seq = 0,
            Kind = LogKinds.RpcResponse,
            Method = method,
            Result = json,
            Error = error,
            DurationMs = Math.Round(durationMs, 2),
            Truncated = truncated,
        });
    }

    /// <summary>
    /// 末尾から <paramref name="count"/> 行を読む（design.md §14）。
    /// 自分が書いている最中のファイルを読むので、共有を明示して開く。
    /// </summary>
    public IReadOnlyList<string> Tail(int count, string? kind)
    {
        lock (_gate)
        {
            _writer.Flush();
        }

        using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);

        var lines = reader.ReadToEnd()
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0);

        if (kind is { Length: > 0 })
        {
            lines = lines.Where(l => l.Contains($"\"kind\":\"{kind}\"", StringComparison.Ordinal));
        }

        var all = lines.ToArray();
        return all.Length <= count ? all : all[^count..];
    }

    private void Write(LogEntry entry)
    {
        lock (_gate)
        {
            var numbered = entry with { Seq = ++_seq };
            _writer.WriteLine(JsonSerializer.Serialize(numbered, WriteOptions));
        }
    }

    /// <summary>
    /// ログ用に JSON 化し、長すぎれば切る。
    /// Target のメモリ内容をそのまま残さないための上限でもある（design.md §21）。
    /// </summary>
    private static (string? Json, bool Truncated) Serialize(object? value)
    {
        if (value is null)
        {
            return (null, false);
        }

        string json;
        try
        {
            json = JsonSerializer.Serialize(value, WriteOptions);
        }
        catch (NotSupportedException ex)
        {
            // ログのためにデーモンを落とさない
            return ($"(serialize failed: {ex.GetType().Name})", false);
        }

        if (json.Length <= MaxFieldBytes)
        {
            return (json, false);
        }

        return (json[..MaxFieldBytes], true);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer.Dispose();
        }
    }
}
