using System.Text.Json.Serialization;

namespace Stakeout.Core;

/// <summary>
/// 設定（design.md §16）。探索順は <see cref="ConfigLoader"/> を見ること。
/// すべての項目に既定値を持たせ、設定ファイルが無くても動くようにする。
/// </summary>
public sealed record StakeoutConfig
{
    /// <summary>既定の Backend 名。</summary>
    public string Backend { get; init; } = "envdte";

    /// <summary>
    /// アタッチを許可するプロセス名の正規表現。
    /// **空なら全拒否**（design.md §15.3）。既定で何にでも繋がる道具にはしない。
    /// </summary>
    public IReadOnlyList<string> AllowProcesses { get; init; } = Array.Empty<string>();

    /// <summary><c>launch</c> を許可するか。</summary>
    public bool AllowLaunch { get; init; }

    public EnvDteConfig EnvDte { get; init; } = new();

    public DbgEngConfig DbgEng { get; init; } = new();

    public CodeIndexConfig Code { get; init; } = new();

    public TasksConfig Tasks { get; init; } = new();

    public LimitsConfig Limits { get; init; } = new();

    public LogConfig Log { get; init; } = new();
}

public sealed record EnvDteConfig
{
    /// <summary>使う Visual Studio の pid。null なら自動選択（ADR 0002）。</summary>
    public int? VsPid { get; init; }

    /// <summary>
    /// ProgID を明示する場合に指定する。既定は null = ROT から版数非依存で自動検出（ADR 0002）。
    /// </summary>
    public string? ProgId { get; init; }

    /// <summary>出力ペインがこの行数を超えたら回収後に消す。0 で無効。</summary>
    public int ClearOutputPaneAfterLines { get; init; } = 5000;
}

public sealed record DbgEngConfig
{
    /// <summary>dbgeng.dll のあるディレクトリ。</summary>
    public string? Path { get; init; }

    /// <summary>シンボルパス。</summary>
    public string? SymbolPath { get; init; }
}

public sealed record CodeIndexConfig
{
    /// <summary>GTAGS のあるディレクトリ。null なら code.* は NOT_CONFIGURED を返す。</summary>
    public string? GtagsRoot { get; init; }
}

public sealed record TasksConfig
{
    /// <summary>スレッドのエントリ関数名からタスク名を導く規則（design.md §9.6）。</summary>
    public IReadOnlyList<TaskEntryPattern> TaskEntryPatterns { get; init; } = Array.Empty<TaskEntryPattern>();
}

/// <param name="Pattern">関数名にあてる正規表現。</param>
/// <param name="Name">タスク名。<c>$1</c> などのキャプチャ参照が使える。</param>
public sealed record TaskEntryPattern(string Pattern, string Name);

/// <summary>
/// 上限値（design.md §8.5, §15.3, ADR 0008）。
/// 実測コストに基づく: 式評価 1 回 6.3 ms、スタック 1 段 4.0 ms。
/// </summary>
public sealed record LimitsConfig
{
    /// <summary>1 レスポンスの上限バイト数。超えたら truncate して cursor を返す。</summary>
    public int ResponseBytes { get; init; } = 65536;

    /// <summary><c>wait</c> の既定タイムアウト秒。</summary>
    public int WaitSec { get; init; } = 20;

    /// <summary>Composite の既定タイムアウト秒。</summary>
    public int CompositeSec { get; init; } = 60;

    /// <summary>Composite が集めるヒット数の既定。</summary>
    public int MaxHits { get; init; } = 100;

    /// <summary>1 リクエストで取得するスタックフレームの上限（ADR 0008）。</summary>
    public int MaxFramesPerRequest { get; init; } = 200;

    /// <summary>1 リクエストで行う式評価の上限（ADR 0008）。</summary>
    public int MaxEvaluationsPerRequest { get; init; } = 300;

    /// <summary>
    /// 1 リクエストで読むメモリのバイト数の上限（design.md §10.8）。
    /// EnvDTE では式評価で代替するため遅い。既定 4 KiB。
    /// </summary>
    public int MaxMemoryReadBytes { get; init; } = 4096;
}

public sealed record LogConfig
{
    /// <summary>ログの出力先。null なら %LOCALAPPDATA%\stakeout\\logs。</summary>
    public string? Dir { get; init; }
}

/// <summary>
/// 設定ファイルの JSON デシリアライズ設定。
/// キーは camelCase、コメント（jsonc）と末尾カンマを許す。
/// </summary>
public static class ConfigJson
{
    public static readonly System.Text.Json.JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };
}
