using System.Runtime.InteropServices;
using Stakeout.Core;
using Stakeout.Rpc;

namespace Stakeout.Backend.EnvDte;

/// <summary>
/// COM の HRESULT を stakeout のエラーに翻訳する（design.md §21 / ADR 0004, 0008）。
/// Backend の外に HRESULT を漏らさない。
/// </summary>
internal static class ComErrors
{
    /// <summary>VS がビジー。呼び出し側で待てば回復しうる。</summary>
    public const int RpcECallRejected = unchecked((int)0x80010001);

    public const int RpcEServerCallRetryLater = unchecked((int)0x8001010A);

    /// <summary>VS との接続が切れた（VS が終了した）。</summary>
    public const int RpcEDisconnected = unchecked((int)0x80010108);

    public const int CoEObjectNotConnected = unchecked((int)0x800401FD);

    /// <summary>今その操作はできない。実行中に Break を呼んだ等。</summary>
    public const int VsMethodNotValidNow = unchecked((int)0x89711007);

    /// <summary>データ式がブレークポイントとして無効。</summary>
    public const int VsInvalidDataExpression = unchecked((int)0x89711010);

    /// <summary>既にデバッガがアタッチされている。</summary>
    public const int VsAlreadyAttached = unchecked((int)0x80040001);

    public static bool IsBusy(Exception ex) =>
        ex is COMException && ex.HResult is RpcECallRejected or RpcEServerCallRetryLater;

    public static bool IsDisconnected(Exception ex) =>
        ex is COMException && ex.HResult is RpcEDisconnected or CoEObjectNotConnected;

    /// <summary>COM 例外を <see cref="BackendException"/> に変換する。</summary>
    public static BackendException Translate(Exception ex, string operation, string currentState)
    {
        if (ex is BackendException backend)
        {
            return backend;
        }

        if (IsDisconnected(ex))
        {
            return new BackendException(
                ErrorCodes.NotAttached,
                $"{operation}: Visual Studio との接続が切れました。",
                "Visual Studio が終了した可能性があります。stakeout attach からやり直してください。",
                ex);
        }

        if (IsBusy(ex))
        {
            return new BackendException(
                ErrorCodes.Timeout,
                $"{operation}: Visual Studio が応答しません。",
                "Visual Studio がモーダルダイアログを表示しているか、長い処理の最中です。" +
                "ダイアログを閉じてから再実行してください。",
                ex);
        }

        return ex.HResult switch
        {
            VsMethodNotValidNow => new BackendException(
                ErrorCodes.Precondition,
                $"{operation}: 現在の状態では実行できません（{currentState}）。",
                "stakeout status で状態を確認し、必要なら stakeout pause / stakeout continue を先に実行してください。",
                ex),

            VsInvalidDataExpression => new BackendException(
                ErrorCodes.Backend,
                $"{operation}: データ式が無効です。",
                "式は停止しているフレームのスコープで解決されます。" +
                "別モジュールのグローバルには {,,モジュール名} を付けてください。",
                ex),

            VsAlreadyAttached => new BackendException(
                ErrorCodes.Precondition,
                $"{operation}: 既にデバッガがアタッチされています。",
                "stakeout detach してからやり直してください。",
                ex),

            _ => new BackendException(
                ErrorCodes.Backend,
                $"{operation}: {ex.Message} (hr=0x{ex.HResult:X8})",
                "Visual Studio の状態を確認してください。繰り返す場合は stakeout log tail でログを見てください。",
                ex),
        };
    }
}
