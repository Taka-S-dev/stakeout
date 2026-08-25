using System.Runtime.InteropServices;

namespace DbgEngProbe;

/// <summary>
/// dbgeng を通さず、Win32 の DebugActiveProcess を直接叩く。
/// ここが失敗するなら、問題は dbgeng ではなく OS 側の許可にある。
/// </summary>
internal static class OsProbe
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DebugActiveProcess(uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DebugActiveProcessStop(uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DebugSetProcessKillOnExit([MarshalAs(UnmanagedType.Bool)] bool killOnExit);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WaitForDebugEvent(out DebugEvent debugEvent, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ContinueDebugEvent(uint processId, uint threadId, uint continueStatus);

    [StructLayout(LayoutKind.Sequential)]
    private struct DebugEvent
    {
        public uint DebugEventCode;
        public uint ProcessId;
        public uint ThreadId;

        // 実際の共用体はもっと大きい。イベント種別だけ見たいので余白を確保する
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 160)]
        public byte[] Union;
    }

    private const uint DbgContinue = 0x00010002;

    public static int Run(int pid)
    {
        if (!DebugActiveProcess((uint)pid))
        {
            var error = Marshal.GetLastWin32Error();
            Console.Error.WriteLine(
                $"O1 FAIL DebugActiveProcess error={error} ({new System.ComponentModel.Win32Exception(error).Message})");
            return 1;
        }

        Console.WriteLine("O1 OK  DebugActiveProcess");

        // デタッチ時に Target を殺さない
        DebugSetProcessKillOnExit(false);

        try
        {
            for (var i = 0; i < 6; i++)
            {
                if (!WaitForDebugEvent(out var debugEvent, 3000))
                {
                    var error = Marshal.GetLastWin32Error();
                    Console.WriteLine($"  wait #{i + 1}: なし error={error}");
                    continue;
                }

                Console.WriteLine(
                    $"  wait #{i + 1}: code={debugEvent.DebugEventCode} " +
                    $"pid={debugEvent.ProcessId} tid={debugEvent.ThreadId}");

                ContinueDebugEvent(debugEvent.ProcessId, debugEvent.ThreadId, DbgContinue);
            }

            Console.WriteLine("O2 OK  デバッグイベントを受け取れた");
            return 0;
        }
        finally
        {
            Console.WriteLine($"detach={DebugActiveProcessStop((uint)pid)}");
        }
    }
}
