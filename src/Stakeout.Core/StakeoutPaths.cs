using System.Runtime.InteropServices;
using System.Security.Principal;
using Stakeout.Rpc;

namespace Stakeout.Core;

/// <summary>
/// パスと実行環境（design.md §7, §12.1, §14）。
/// </summary>
public static class StakeoutPaths
{
    /// <summary>名前付きパイプ名。実体は <see cref="RpcTransport.PipeName"/>。</summary>
    public static string PipeName => RpcTransport.PipeName;

    /// <summary>%LOCALAPPDATA%\stakeout</summary>
    public static string StateRoot => RpcTransport.StateRoot;

    /// <summary>セッションログの既定の置き場（design.md §14）。</summary>
    public static string DefaultLogDirectory => Path.Combine(StateRoot, "logs");

    /// <summary>起動失敗の記録先。実体は <see cref="RpcTransport.StartupErrorFile"/>。</summary>
    public static string StartupErrorFile => RpcTransport.StartupErrorFile;

    /// <summary>Trace DB の置き場（design.md §12.1）。Phase 6 で使う。</summary>
    public static string TraceDirectory => Path.Combine(StateRoot, "trace");

    /// <summary>
    /// このプロセスが昇格しているか。
    /// Visual Studio と昇格レベルが違うと COM が無言で失敗するため、
    /// 診断のために <c>daemon.status</c> で返す（design.md §10.3）。
    /// </summary>
    public static bool IsElevated()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return false;
        }

        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    /// <summary>stakeout / stakeout のバージョン。</summary>
    public static string Version =>
        typeof(StakeoutPaths).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
