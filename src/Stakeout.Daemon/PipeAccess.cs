using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Stakeout.Daemon;

/// <summary>
/// 名前付きパイプの ACL と、繋いできた相手の記録（ADR 0024）。
///
/// 管理者で動くデーモンに .NET の CurrentUserOnly を使うと、ACL の宛先も所有者も
/// Administrators グループになる。通常権限のトークンでは Administrators が deny-only なので、
/// 同じユーザーでも開けない。<c>pipe.allowUnelevatedClients</c> のときは、宛先と所有者を
/// ユーザー本人の SID にする。所有者を本人にしておくと、通常権限クライアントの
/// CurrentUserOnly の検査（所有者 == 自分）もそのまま通る。
/// </summary>
internal static class PipeAccess
{
    /// <summary>設定に従ってサーバー側のパイプを作る。</summary>
    public static NamedPipeServerStream Create(string pipeName, int maxInstances, bool allowUnelevatedClients)
    {
        if (!allowUnelevatedClients)
        {
            return new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                maxInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        }

        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new InvalidOperationException("現在のユーザーの SID を取得できません。");

        // 本人だけ。グループ（Administrators 等）には与えない。管理者の別ユーザーが繋げてはいけない
        var security = new PipeSecurity();
        security.SetOwner(user);
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            maxInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            security);
    }

    /// <summary>繋いできたプロセスの pid・実行ファイル・昇格状態。ログに残す（ADR 0024 の決定 4）。</summary>
    public static string DescribeClient(NamedPipeServerStream pipe)
    {
        try
        {
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var pid))
            {
                return "client=(unknown)";
            }

            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            var exe = process.ProcessName;
            var elevated = IsElevated(process);

            return $"client pid={pid} exe={exe} elevated={(elevated is null ? "?" : elevated.Value.ToString())}";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return "client=(unknown)";
        }
    }

    private static bool? IsElevated(System.Diagnostics.Process process)
    {
        if (!OpenProcessToken(process.Handle, TokenQuery, out var token))
        {
            return null;
        }

        try
        {
            var size = Marshal.SizeOf<uint>();
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (!GetTokenInformation(token, TokenElevation, buffer, (uint)size, out _))
                {
                    return null;
                }

                return Marshal.ReadInt32(buffer) != 0;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            CloseHandle(token);
        }
    }

    private const uint TokenQuery = 0x0008;
    private const int TokenElevation = 20;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint clientProcessId);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int informationClass, IntPtr information, uint length, out uint returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
