using System.Text.Json.Serialization;

namespace Stakeout.Rpc;

/// <summary>
/// エラーコード（design.md §15.1）。
/// JSON では文字列としてそのまま出るので、名前を変えると CLI の互換が壊れる。
/// </summary>
public static class ErrorCodes
{
    /// <summary>状態要件違反。今できることを hint に書く。</summary>
    public const string Precondition = "PRECONDITION";

    /// <summary>セッションが無い。</summary>
    public const string NotAttached = "NOT_ATTACHED";

    /// <summary>期限内に完了しなかった。</summary>
    public const string Timeout = "TIMEOUT";

    /// <summary>Backend が投げた例外。</summary>
    public const string Backend = "BACKEND";

    /// <summary>プロセス / シンボル / ブレークポイントが見つからない。</summary>
    public const string NotFound = "NOT_FOUND";

    /// <summary>allowlist の外。</summary>
    public const string Denied = "DENIED";

    /// <summary>Backend の Capabilities が足りない。</summary>
    public const string Unsupported = "UNSUPPORTED";

    /// <summary>設定されていない（Code Index など）。</summary>
    public const string NotConfigured = "NOT_CONFIGURED";

    /// <summary>コマンドの使い方が誤っている。</summary>
    public const string Usage = "USAGE";

    /// <summary>デーモンに到達できない。</summary>
    public const string DaemonUnreachable = "DAEMON_UNREACHABLE";

    /// <summary>上のどれでもない内部エラー。</summary>
    public const string Internal = "INTERNAL";
}

/// <summary>
/// エラー本体。<c>Hint</c> は必須の扱いにする（design.md §15.1）。
/// エージェントがエラーだけを読んで次の一手を決められることが、この基盤の要件である。
/// </summary>
/// <param name="Code">ErrorCodes のいずれか。</param>
/// <param name="Message">何が起きたか。</param>
/// <param name="Hint">次に何をすればよいか。</param>
public sealed record StakeoutError(string Code, string Message, string Hint);

/// <summary>
/// すべての RPC メソッドの戻り値（design.md §7）。
/// JSON-RPC のエラー機構ではなくペイロードで成否を運ぶ。CLI が
/// <c>--json</c> でそのまま出力するのがこの形である。
/// </summary>
public sealed record RpcResult
{
    /// <summary>成功したか。</summary>
    [JsonPropertyName("ok")]
    public required bool Ok { get; init; }

    /// <summary>成功時のペイロード。失敗時は null。</summary>
    [JsonPropertyName("data")]
    public object? Data { get; init; }

    /// <summary>失敗時のエラー。成功時は null。</summary>
    [JsonPropertyName("error")]
    public StakeoutError? Error { get; init; }

    /// <summary>応答が打ち切られたか（design.md §8.5）。</summary>
    [JsonPropertyName("truncated")]
    public bool Truncated { get; init; }

    /// <summary>続きを取るためのカーソル。打ち切られていなければ null。</summary>
    [JsonPropertyName("cursor")]
    public string? Cursor { get; init; }

    public static RpcResult Success(object? data) => new() { Ok = true, Data = data };

    public static RpcResult Partial(object? data, string cursor) =>
        new() { Ok = true, Data = data, Truncated = true, Cursor = cursor };

    public static RpcResult Failure(string code, string message, string hint) =>
        new() { Ok = false, Error = new StakeoutError(code, message, hint) };

    public static RpcResult Failure(StakeoutError error) => new() { Ok = false, Error = error };
}
