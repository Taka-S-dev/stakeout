using System.Runtime.InteropServices;

namespace DbgEngProbe;

/// <summary>
/// RCW を介さず、vtable を直接叩いて同じことを試す。
///
/// 仮説: <c>Marshal.GetObjectForIUnknown</c> が作る RCW を通すと、
/// 呼び出しが別スレッドへ回され、侵入アタッチ（DebugActiveProcess と
/// WaitForDebugEvent が同一スレッドであることを要求する）が成立しない。
///
/// 非侵入アタッチは成功し、侵入アタッチだけが無反応、という観測に合う。
/// ここで生呼び出しなら通るなら、仮説が確かめられる。
/// </summary>
internal static unsafe class RawProbe
{
    // vtable の添字。先頭 3 つは IUnknown（QueryInterface / AddRef / Release）
    private const int ClientAttachProcess = 3 + 9;
    private const int ControlGetExecutionStatus = 3 + 46;
    private const int ControlWaitForEvent = 3 + 90;
    private const int SymbolsGetNumberModules = 3 + 9;
    private const int SymbolsGetOffsetByName = 3 + 5;

    private static readonly Guid IidDebugClient = new("27fe5639-8407-4f47-8364-ee118fb08ac8");
    private static readonly Guid IidDebugControl = new("5182e668-105e-416e-ad92-24ef800424ba");
    private static readonly Guid IidDebugSymbols = new("8c31e98c-983a-48a5-9016-6fe5d667a950");

    [DllImport("dbgeng.dll")]
    private static extern int DebugCreate(in Guid interfaceId, out IntPtr @interface);

    public static int Run(int pid)
    {
        var hr = DebugCreate(in IidDebugClient, out var client);
        if (hr < 0 || client == IntPtr.Zero)
        {
            Console.Error.WriteLine($"R1 FAIL: DebugCreate hr=0x{hr:X8}");
            return 1;
        }

        Console.WriteLine("R1 OK  DebugCreate（生ポインタのまま保持）");

        var control = QueryInterface(client, IidDebugControl);
        var symbols = QueryInterface(client, IidDebugSymbols);

        if (control == IntPtr.Zero || symbols == IntPtr.Zero)
        {
            Console.Error.WriteLine("R1 FAIL: QueryInterface");
            return 1;
        }

        try
        {
            // 侵入アタッチ
            var attach = (delegate* unmanaged[Stdcall]<IntPtr, ulong, uint, uint, int>)
                Vtable(client, ClientAttachProcess);

            hr = attach(client, 0, (uint)pid, 0);
            Console.WriteLine($"R2 AttachProcess(default) hr=0x{hr:X8}");

            if (hr < 0)
            {
                return 1;
            }

            var wait = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, int>)
                Vtable(control, ControlWaitForEvent);

            var status = (delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)
                Vtable(control, ControlGetExecutionStatus);

            var numberModules = (delegate* unmanaged[Stdcall]<IntPtr, uint*, uint*, int>)
                Vtable(symbols, SymbolsGetNumberModules);

            for (var attempt = 1; attempt <= 3; attempt++)
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                hr = wait(control, 0, 5000);
                watch.Stop();

                uint executionStatus = 0;
                uint loaded = 0;
                uint unloaded = 0;
                status(control, &executionStatus);
                numberModules(symbols, &loaded, &unloaded);

                Console.WriteLine(
                    $"  wait #{attempt}: hr=0x{hr:X8} ({watch.ElapsedMilliseconds} ms) " +
                    $"status={executionStatus} modules={loaded}");

                if (hr == 0 && loaded > 0)
                {
                    break;
                }
            }

            uint finalModules = 0;
            uint ignored = 0;
            numberModules(symbols, &finalModules, &ignored);

            if (finalModules == 0)
            {
                Console.WriteLine("R2 FAIL 生呼び出しでも初期イベントが来ない");
                return 1;
            }

            Console.WriteLine($"R2 OK  侵入アタッチで modules={finalModules}");

            // シンボル解決まで通るか
            var offsetByName = (delegate* unmanaged[Stdcall]<IntPtr, byte*, ulong*, int>)
                Vtable(symbols, SymbolsGetOffsetByName);

            var name = System.Text.Encoding.ASCII.GetBytes("NativeLib!g_shared\0");
            ulong address = 0;

            fixed (byte* namePtr = name)
            {
                hr = offsetByName(symbols, namePtr, &address);
            }

            Console.WriteLine(hr >= 0
                ? $"R3 OK  NativeLib!g_shared = 0x{address:X}"
                : $"R3 FAIL: GetOffsetByName hr=0x{hr:X8}");

            return 0;
        }
        finally
        {
            // Target を殺さずに離れる
            var detach = (delegate* unmanaged[Stdcall]<IntPtr, int>)Vtable(client, 3 + 22);
            Console.WriteLine($"detach hr=0x{detach(client):X8}");

            Release(symbols);
            Release(control);
            Release(client);
        }
    }

    private static IntPtr Vtable(IntPtr instance, int index)
    {
        var vtable = *(IntPtr**)instance;
        return vtable[index];
    }

    private static IntPtr QueryInterface(IntPtr instance, Guid iid)
    {
        var queryInterface = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Vtable(instance, 0);

        IntPtr result;
        var hr = queryInterface(instance, &iid, &result);
        return hr >= 0 ? result : IntPtr.Zero;
    }

    private static void Release(IntPtr instance)
    {
        if (instance == IntPtr.Zero)
        {
            return;
        }

        var release = (delegate* unmanaged[Stdcall]<IntPtr, uint>)Vtable(instance, 2);
        release(instance);
    }
}
