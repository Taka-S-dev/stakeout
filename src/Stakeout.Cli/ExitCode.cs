using Stakeout.Rpc;

namespace Stakeout.Cli;

/// <summary>
/// 終了コード（design.md §8.3）。
/// エージェントは終了コードだけで次の一手を分岐できる必要がある。
/// </summary>
public static class ExitCode
{
    /// <summary>成功。</summary>
    public const int Ok = 0;

    /// <summary>実行時エラー（Backend エラーなど）。</summary>
    public const int RuntimeError = 1;

    /// <summary>使い方の誤り。</summary>
    public const int UsageError = 2;

    /// <summary>タイムアウト、またはまだ実行中（<c>wait</c> で停止しなかった）。</summary>
    public const int Timeout = 3;

    /// <summary>前提条件違反（停止中でないのに <c>stack</c> など）。</summary>
    public const int Precondition = 4;

    /// <summary>対象なし（プロセス未発見、allowlist 外）。</summary>
    public const int NotFound = 5;

    /// <summary>エラーコードを終了コードに写す。</summary>
    public static int FromErrorCode(string code) => code switch
    {
        ErrorCodes.Usage => UsageError,
        ErrorCodes.Timeout => Timeout,
        ErrorCodes.Precondition => Precondition,
        ErrorCodes.NotAttached => Precondition,
        ErrorCodes.NotFound => NotFound,
        ErrorCodes.Denied => NotFound,
        _ => RuntimeError,
    };
}
