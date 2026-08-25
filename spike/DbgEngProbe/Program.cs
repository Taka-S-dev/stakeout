using System.Runtime.InteropServices;
using System.Text;

// 使い捨ての検証用スパイク。
//
// 確かめたいこと（DbgEng を Phase 5 の Backend にできるか）:
//   D1  System32 の dbgeng.dll から DebugCreate でクライアントを作れるか
//   D2  実行中のプロセスに非侵入でないアタッチができるか
//   D3  シンボルが解決でき、グローバル変数のアドレスを引けるか
//   D4  メモリを直接読めるか（EnvDTE には無い機能）
//   D5  ブレークポイントを張って停止イベントを受けられるか
//
//   dbgengprobe --pid <target pid>

return Probe.Run(args);

internal static class Probe
{
    public static int Run(string[] args)
    {
        var index = Array.IndexOf(args, "--pid");
        if (index < 0 || index + 1 >= args.Length || !int.TryParse(args[index + 1], out var pid))
        {
            Console.Error.WriteLine("usage: dbgengprobe --pid <target pid>");
            return 2;
        }

        // DbgEng のクライアントはスレッド親和性が強い。専用スレッドで完結させる
        var raw = args.Contains("--raw");
        var result = 1;

        var thread = new Thread(
            () => result = args.Contains("--os") ? DbgEngProbe.OsProbe.Run(pid)
                : raw ? DbgEngProbe.RawProbe.Run(pid)
                : Probe.RunOnDedicatedThread(pid),
            1024 * 1024);
        thread.Start();
        thread.Join();
        return result;
    }

    private static int RunOnDedicatedThread(int pid)
    {
        // DebugCreate を呼ぶ前に、使う dbgeng.dll を確定させる
        Console.WriteLine($"dbgeng: {Dbg.DbgEngPath() ?? "(not found)"}");

        object? client;
        try
        {
            client = Dbg.CreateClient();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"D1 FAIL: {ex.Message}");
            return 1;
        }

        Console.WriteLine("D1 OK  DebugCreate でクライアントを作れた");

        // エンジンの診断出力を拾う。全マスクを受ける
        var callbacks = new ConsoleOutputCallbacks();
        var cbHr = ((IDebugClient)client).SetOutputCallbacks(callbacks);
        ((IDebugClient)client).SetOutputMask(0xFFFFFFFF);
        Console.WriteLine($"D0 SetOutputCallbacks hr=0x{cbHr:X8}");

        // interop の検算。vtable の並びが 1 つでもずれていれば、ここが化ける。
        // 化けたまま先へ進むと、原因を「アタッチの失敗」と読み違える
        {
            var check = (IDebugControl)client;
            var pageHr = check.GetPageSize(out var pageSize);
            var ptrHr = check.IsPointer64Bit();
            Console.WriteLine(
                $"D0 {(pageHr >= 0 && pageSize == 4096 ? "OK " : "FAIL")} " +
                $"GetPageSize hr=0x{pageHr:X8} size={pageSize} / IsPointer64Bit hr=0x{ptrHr:X8}");
        }

        var debugClient = (IDebugClient)client;
        var control = (IDebugControl)client;
        var symbols = (IDebugSymbols)client;
        var spaces = (IDebugDataSpaces)client;

        // アタッチした直後に止まるよう、エンジンに指示しておく。
        // これが無いと、エンジンはイベントを IGNORE_EVENT で流し続け、
        // 一度も停止状態に入らない。停止していないとシンボルもモジュールも引けない
        var engineOptions = control.AddEngineOptions(Dbg.EngineOptionInitialBreak);
        Console.WriteLine($"  AddEngineOptions(INITIAL_BREAK) hr=0x{engineOptions:X8}");

        // D2: アタッチ。どの方法なら初期イベントが来るかを切り分ける
        var mode = Environment.GetEnvironmentVariable("DBGENG_MODE") ?? "attach";
        int hr;

        switch (mode)
        {
            case "noninvasive":
                hr = debugClient.AttachProcess(0, (uint)pid, Dbg.AttachNonInvasive | Dbg.AttachNonInvasiveNoSuspend);
                Console.WriteLine($"  AttachProcess(noninvasive) hr=0x{hr:X8}");
                break;

            case "launch":
                var exe = Environment.GetEnvironmentVariable("DBGENG_EXE")
                    ?? throw new InvalidOperationException("DBGENG_EXE が要る");
                hr = debugClient.CreateProcessAndAttach(0, exe + " dbgeng --slow", Dbg.CreateProcessDebugOnlyThis, 0, 0);
                Console.WriteLine($"  CreateProcessAndAttach hr=0x{hr:X8}");
                break;

            case "noinitialbreak":
                hr = debugClient.AttachProcess(0, (uint)pid, Dbg.AttachInvasiveNoInitialBreak);
                Console.WriteLine($"  AttachProcess(invasive, no initial break) hr=0x{hr:X8}");
                break;

            case "existing":
                hr = debugClient.AttachProcess(0, (uint)pid, Dbg.AttachExisting);
                Console.WriteLine($"  AttachProcess(existing) hr=0x{hr:X8}");
                break;

            default:
                hr = debugClient.AttachProcess(0, (uint)pid, Dbg.AttachDefault);
                Console.WriteLine($"  AttachProcess(default) hr=0x{hr:X8}");
                break;
        }

        if (hr < 0)
        {
            Console.Error.WriteLine($"D2 FAIL: hr=0x{hr:X8}");
            return 1;
        }

        // 初期イベントが来るまで何度か待つ。1 回目で返らないことがある
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            hr = control.WaitForEvent(0, 5000);
            watch.Stop();

            // 戻り値を見ずに out の値だけ読むと、失敗したのに
            // 初期値を「観測結果」として読んでしまう
            var statusHr = control.GetExecutionStatus(out var status);
            var modulesHr = symbols.GetNumberModules(out var loaded, out _);

            Console.WriteLine(
                $"  wait #{attempt}: hr=0x{hr:X8} ({watch.ElapsedMilliseconds} ms) " +
                $"status={status}(hr=0x{statusHr:X8}) modules={loaded}(hr=0x{modulesHr:X8})");

            if (hr < 0)
            {
                var description = new StringBuilder(256);
                control.GetLastEventInformation(
                    out var eventType, out var eventPid, out var eventTid,
                    IntPtr.Zero, 0, out _, description, (uint)description.Capacity, out _);

                Console.WriteLine($"    last event: type={eventType} pid={eventPid} tid={eventTid} {description}");
            }

            if (hr == 0 && loaded > 0)
            {
                break;
            }
        }

        // ここまでで「アタッチは成功したがイベントを自動継続している」状態になりうる。
        // シンボルもモジュールも、停止していなければ引けない。明示的に中断させる
        symbols.GetNumberModules(out var beforeBreak, out _);
        if (beforeBreak == 0)
        {
            var interrupt = control.SetInterrupt(Dbg.InterruptActive);
            var broke = control.WaitForEvent(0, 10000);
            control.GetExecutionStatus(out var afterStatus);
            symbols.GetNumberModules(out var afterModules, out _);

            Console.WriteLine(
                $"  SetInterrupt hr=0x{interrupt:X8} / WaitForEvent hr=0x{broke:X8} " +
                $"status={afterStatus} modules={afterModules}");
        }

        control.GetExecutionStatus(out var finalStatus);
        symbols.GetNumberModules(out var modules, out _);
        Console.WriteLine($"D2 {(modules > 0 ? "OK " : "FAIL")} status={finalStatus} modules={modules}");

        if (modules > 0)
        {
            // 読み込まれているモジュール名を出す。シンボルの前置に何を使えばよいか分かる
            for (uint i = 0; i < Math.Min(modules, 8); i++)
            {
                if (symbols.GetModuleByIndex(i, out var moduleBase) >= 0)
                {
                    Console.WriteLine($"    module[{i}] base=0x{moduleBase:X}");
                }
            }
        }

        try
        {
            // D3: シンボル解決
            hr = symbols.GetOffsetByName("NativeLib!g_shared", out var address);
            if (hr >= 0)
            {
                Console.WriteLine($"D3 OK  NativeLib!g_shared = 0x{address:X}");
            }
            else
            {
                Console.WriteLine($"D3 FAIL: GetOffsetByName hr=0x{hr:X8}");
                address = 0;
            }

            // D4: メモリ直読み（EnvDTE には無い）
            if (address != 0)
            {
                var buffer = new byte[32];
                hr = spaces.ReadVirtual(address, buffer, (uint)buffer.Length, out var read);

                Console.WriteLine(hr >= 0
                    ? $"D4 OK  ReadVirtual {read} バイト: {Convert.ToHexString(buffer.AsSpan(0, (int)Math.Min(read, 24)))}"
                    : $"D4 FAIL: ReadVirtual hr=0x{hr:X8}");
            }

            // D5: ブレークポイント
            hr = control.AddBreakpoint(Dbg.BreakpointCode, uint.MaxValue, out var breakpoint);
            if (hr >= 0 && breakpoint is not null)
            {
                var bp = (IDebugBreakpoint)breakpoint;
                var set = bp.SetOffsetExpression("NativeLib!nl_bump_counter");
                var enabled = bp.AddFlags(Dbg.BreakpointEnabled);

                if (set >= 0 && enabled >= 0)
                {
                    control.SetExecutionStatus(Dbg.StatusGo);
                    var waited = control.WaitForEvent(0, 20000);

                    if (waited >= 0)
                    {
                        symbols.GetNameByOffset(CurrentOffset(control), out var name);
                        Console.WriteLine($"D5 OK  ブレークポイントで停止: {name}");
                    }
                    else
                    {
                        Console.WriteLine($"D5 FAIL: 停止しなかった hr=0x{waited:X8}");
                    }
                }
                else
                {
                    Console.WriteLine($"D5 FAIL: SetOffsetExpression=0x{set:X8} AddFlags=0x{enabled:X8}");
                }
            }
            else
            {
                Console.WriteLine($"D5 FAIL: AddBreakpoint hr=0x{hr:X8}");
            }
        }
        finally
        {
            // Target を殺さない。デタッチして抜ける
            var detached = debugClient.DetachProcesses();
            Console.WriteLine($"detach hr=0x{detached:X8}");
            Marshal.ReleaseComObject(client);
        }

        return 0;
    }

    private static ulong CurrentOffset(IDebugControl control)
    {
        // 現在の命令位置。停止位置の関数名を引くのに使う
        var registers = (IDebugRegisters)control;
        return registers.GetInstructionOffset(out var offset) >= 0 ? offset : 0;
    }
}

internal static class Dbg
{
    public const uint AttachDefault = 0;
    public const uint AttachNonInvasive = 0x00000001;
    public const uint AttachNonInvasiveNoSuspend = 0x00000004;
    public const uint CreateProcessDebugOnlyThis = 0x00000002;
    public const uint AttachExisting = 0x00000002;
    public const uint AttachInvasiveNoInitialBreak = 0x00000008;
    public const uint InterruptActive = 0;
    public const uint EngineOptionInitialBreak = 0x00000010;
    public const uint BreakpointCode = 0;
    public const uint BreakpointEnabled = 0x00000004;
    public const uint StatusGo = 1;

    [DllImport("dbgeng.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int DebugCreate(in Guid interfaceId, out IntPtr @interface);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibraryW(string path);

    /// <summary>
    /// 使う dbgeng.dll を決める。
    ///
    /// 既定は System32 の OS 同梱版。<c>DBGENG_PATH</c> を指定すると、
    /// そのディレクトリの dbgeng.dll を先に読み込む。Debugging Tools for Windows を
    /// 入れたときに、入れ直さずここだけ差し替えて確かめられるようにしてある。
    /// </summary>
    public static string? DbgEngPath()
    {
        if (Environment.GetEnvironmentVariable("DBGENG_PATH") is { Length: > 0 } directory)
        {
            var candidate = Path.Combine(directory, "dbgeng.dll");
            if (!File.Exists(candidate))
            {
                return $"(指定されたが見つからない: {candidate})";
            }

            // DllImport より先に読み込んでおけば、こちらが使われる
            if (LoadLibraryW(candidate) == IntPtr.Zero)
            {
                return $"(読み込めない: {candidate} error={Marshal.GetLastWin32Error()})";
            }

            return candidate;
        }

        var system = Path.Combine(Environment.SystemDirectory, "dbgeng.dll");
        return File.Exists(system) ? system : null;
    }

    public static object CreateClient()
    {
        // IDebugClient の IID。ここが違うと E_NOINTERFACE になる
        var iid = new Guid("27fe5639-8407-4f47-8364-ee118fb08ac8");

        var hr = DebugCreate(in iid, out var raw);
        if (hr < 0 || raw == IntPtr.Zero)
        {
            throw new InvalidOperationException($"DebugCreate hr=0x{hr:X8}");
        }

        var client = Marshal.GetObjectForIUnknown(raw);
        Marshal.Release(raw);
        return client;
    }
}

[ComImport, Guid("27fe5639-8407-4f47-8364-ee118fb08ac8"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDebugClient
{
    // vtable の並びをそのまま写す。使わないメソッドも順番のために宣言が要る
    [PreserveSig] int AttachKernel(uint flags, [MarshalAs(UnmanagedType.LPStr)] string? connectOptions);
    [PreserveSig] int GetKernelConnectionOptions(StringBuilder? buffer, uint bufferSize, out uint optionsSize);
    [PreserveSig] int SetKernelConnectionOptions([MarshalAs(UnmanagedType.LPStr)] string options);
    [PreserveSig] int StartProcessServer(uint flags, [MarshalAs(UnmanagedType.LPStr)] string options, IntPtr reserved);
    [PreserveSig] int ConnectProcessServer([MarshalAs(UnmanagedType.LPStr)] string remoteOptions, out ulong server);
    [PreserveSig] int DisconnectProcessServer(ulong server);
    [PreserveSig] int GetRunningProcessSystemIds(ulong server, uint[]? ids, uint count, out uint actualCount);
    [PreserveSig] int GetRunningProcessSystemIdByExecutableName(ulong server, [MarshalAs(UnmanagedType.LPStr)] string exeName, uint flags, out uint id);
    [PreserveSig] int GetRunningProcessDescription(ulong server, uint systemId, uint flags, StringBuilder? exeName, uint exeNameSize, out uint actualExeNameSize, StringBuilder? description, uint descriptionSize, out uint actualDescriptionSize);
    [PreserveSig] int AttachProcess(ulong server, uint processId, uint attachFlags);
    [PreserveSig] int CreateProcess(ulong server, [MarshalAs(UnmanagedType.LPStr)] string commandLine, uint createFlags);
    [PreserveSig] int CreateProcessAndAttach(ulong server, [MarshalAs(UnmanagedType.LPStr)] string? commandLine, uint createFlags, uint processId, uint attachFlags);
    [PreserveSig] int GetProcessOptions(out uint options);
    [PreserveSig] int AddProcessOptions(uint options);
    [PreserveSig] int RemoveProcessOptions(uint options);
    [PreserveSig] int SetProcessOptions(uint options);
    [PreserveSig] int OpenDumpFile([MarshalAs(UnmanagedType.LPStr)] string dumpFile);
    [PreserveSig] int WriteDumpFile([MarshalAs(UnmanagedType.LPStr)] string dumpFile, uint qualifier);
    [PreserveSig] int ConnectSession(uint flags, uint historyLimit);
    [PreserveSig] int StartServer([MarshalAs(UnmanagedType.LPStr)] string options);
    [PreserveSig] int OutputServers(uint outputControl, [MarshalAs(UnmanagedType.LPStr)] string machine, uint flags);
    [PreserveSig] int TerminateProcesses();
    [PreserveSig] int DetachProcesses();
    [PreserveSig] int EndSession(uint flags);
    [PreserveSig] int GetExitCode(out uint code);
    [PreserveSig] int DispatchCallbacks(uint timeout);
    [PreserveSig] int ExitDispatch([MarshalAs(UnmanagedType.Interface)] object client);
    [PreserveSig] int CreateClient([MarshalAs(UnmanagedType.Interface)] out object client);
    [PreserveSig] int GetInputCallbacks([MarshalAs(UnmanagedType.Interface)] out object callbacks);
    [PreserveSig] int SetInputCallbacks([MarshalAs(UnmanagedType.Interface)] object? callbacks);
    [PreserveSig] int GetOutputCallbacks([MarshalAs(UnmanagedType.Interface)] out object callbacks);
    [PreserveSig] int SetOutputCallbacks(IDebugOutputCallbacks? callbacks);
    [PreserveSig] int GetOutputMask(out uint mask);
    [PreserveSig] int SetOutputMask(uint mask);
}

/// <summary>
/// エンジン自身の診断出力を受け取る。
/// 「なぜアタッチできないのか」はここにしか出ないことがある。
/// </summary>
[ComImport, Guid("4bf58045-d654-4c40-b0af-683090f356dc"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDebugOutputCallbacks
{
    [PreserveSig] int Output(uint mask, [MarshalAs(UnmanagedType.LPStr)] string text);
}

internal sealed class ConsoleOutputCallbacks : IDebugOutputCallbacks
{
    public int Output(uint mask, string text)
    {
        foreach (var line in text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
        {
            Console.WriteLine($"    [engine] {line.TrimEnd()}");
        }

        return 0;
    }
}

[ComImport, Guid("5182e668-105e-416e-ad92-24ef800424ba"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDebugControl
{
    [PreserveSig] int GetInterrupt();
    [PreserveSig] int SetInterrupt(uint flags);
    [PreserveSig] int GetInterruptTimeout(out uint seconds);
    [PreserveSig] int SetInterruptTimeout(uint seconds);
    [PreserveSig] int GetLogFile(StringBuilder? buffer, uint bufferSize, out uint fileSize, out int append);
    [PreserveSig] int OpenLogFile([MarshalAs(UnmanagedType.LPStr)] string file, [MarshalAs(UnmanagedType.Bool)] bool append);
    [PreserveSig] int CloseLogFile();
    [PreserveSig] int GetLogMask(out uint mask);
    [PreserveSig] int SetLogMask(uint mask);
    [PreserveSig] int Input(StringBuilder? buffer, uint bufferSize, out uint inputSize);
    [PreserveSig] int ReturnInput([MarshalAs(UnmanagedType.LPStr)] string buffer);
    [PreserveSig] int Output(uint mask, [MarshalAs(UnmanagedType.LPStr)] string format);
    [PreserveSig] int OutputVaList(uint mask, [MarshalAs(UnmanagedType.LPStr)] string format, IntPtr va);
    [PreserveSig] int ControlledOutput(uint outputControl, uint mask, [MarshalAs(UnmanagedType.LPStr)] string format);
    [PreserveSig] int ControlledOutputVaList(uint outputControl, uint mask, [MarshalAs(UnmanagedType.LPStr)] string format, IntPtr va);
    [PreserveSig] int OutputPrompt(uint outputControl, [MarshalAs(UnmanagedType.LPStr)] string? format);
    [PreserveSig] int OutputPromptVaList(uint outputControl, [MarshalAs(UnmanagedType.LPStr)] string? format, IntPtr va);
    [PreserveSig] int GetPromptText(StringBuilder? buffer, uint bufferSize, out uint textSize);
    [PreserveSig] int OutputCurrentState(uint outputControl, uint flags);
    [PreserveSig] int OutputVersionInformation(uint outputControl);
    [PreserveSig] int GetNotifyEventHandle(out ulong handle);
    [PreserveSig] int SetNotifyEventHandle(ulong handle);
    [PreserveSig] int Assemble(ulong offset, [MarshalAs(UnmanagedType.LPStr)] string instr, out ulong endOffset);
    [PreserveSig] int Disassemble(ulong offset, uint flags, StringBuilder? buffer, uint bufferSize, out uint disassemblySize, out ulong endOffset);
    [PreserveSig] int GetDisassembleEffectiveOffset(out ulong offset);
    [PreserveSig] int OutputDisassembly(uint outputControl, ulong offset, uint flags, out ulong endOffset);
    [PreserveSig] int OutputDisassemblyLines(uint outputControl, uint previousLines, uint totalLines, ulong offset, uint flags, out uint offsetLine, out ulong startOffset, out ulong endOffset, ulong[]? lineOffsets);
    [PreserveSig] int GetNearInstruction(ulong offset, int delta, out ulong nearOffset);
    [PreserveSig] int GetStackTrace(ulong frameOffset, ulong stackOffset, ulong instructionOffset, IntPtr frames, uint framesSize, out uint framesFilled);
    [PreserveSig] int GetReturnOffset(out ulong offset);
    [PreserveSig] int OutputStackTrace(uint outputControl, IntPtr frames, uint framesSize, uint flags);
    [PreserveSig] int GetDebuggeeType(out uint debugClass, out uint qualifier);
    [PreserveSig] int GetActualProcessorType(out uint type);
    [PreserveSig] int GetExecutingProcessorType(out uint type);
    [PreserveSig] int GetNumberPossibleExecutingProcessorTypes(out uint number);
    [PreserveSig] int GetPossibleExecutingProcessorTypes(uint start, uint count, uint[]? types);
    [PreserveSig] int GetNumberProcessors(out uint number);
    [PreserveSig] int GetSystemVersion(out uint platformId, out uint major, out uint minor, StringBuilder? servicePackString, uint servicePackStringSize, out uint servicePackStringUsed, out uint servicePackNumber, StringBuilder? buildString, uint buildStringSize, out uint buildStringUsed);
    [PreserveSig] int GetPageSize(out uint size);
    [PreserveSig] int IsPointer64Bit();
    [PreserveSig] int ReadBugCheckData(out uint code, out ulong arg1, out ulong arg2, out ulong arg3, out ulong arg4);
    [PreserveSig] int GetNumberSupportedProcessorTypes(out uint number);
    [PreserveSig] int GetSupportedProcessorTypes(uint start, uint count, uint[]? types);
    [PreserveSig] int GetProcessorTypeNames(uint type, StringBuilder? fullNameBuffer, uint fullNameBufferSize, out uint fullNameSize, StringBuilder? abbrevNameBuffer, uint abbrevNameBufferSize, out uint abbrevNameSize);
    [PreserveSig] int GetEffectiveProcessorType(out uint type);
    [PreserveSig] int SetEffectiveProcessorType(uint type);
    [PreserveSig] int GetExecutionStatus(out uint status);
    [PreserveSig] int SetExecutionStatus(uint status);
    [PreserveSig] int GetCodeLevel(out uint level);
    [PreserveSig] int SetCodeLevel(uint level);
    [PreserveSig] int GetEngineOptions(out uint options);
    [PreserveSig] int AddEngineOptions(uint options);
    [PreserveSig] int RemoveEngineOptions(uint options);
    [PreserveSig] int SetEngineOptions(uint options);
    [PreserveSig] int GetSystemErrorControl(out uint outputLevel, out uint breakLevel);
    [PreserveSig] int SetSystemErrorControl(uint outputLevel, uint breakLevel);
    [PreserveSig] int GetTextMacro(uint slot, StringBuilder? buffer, uint bufferSize, out uint macroSize);
    [PreserveSig] int SetTextMacro(uint slot, [MarshalAs(UnmanagedType.LPStr)] string macro);
    [PreserveSig] int GetRadix(out uint radix);
    [PreserveSig] int SetRadix(uint radix);
    [PreserveSig] int Evaluate([MarshalAs(UnmanagedType.LPStr)] string expression, uint desiredType, IntPtr value, out uint remainderIndex);
    [PreserveSig] int CoerceValue(IntPtr input, uint outputType, IntPtr output);
    [PreserveSig] int CoerceValues(uint count, IntPtr input, uint[]? outputTypes, IntPtr output);
    [PreserveSig] int Execute(uint outputControl, [MarshalAs(UnmanagedType.LPStr)] string command, uint flags);
    [PreserveSig] int ExecuteCommandFile(uint outputControl, [MarshalAs(UnmanagedType.LPStr)] string commandFile, uint flags);
    [PreserveSig] int GetNumberBreakpoints(out uint number);
    [PreserveSig] int GetBreakpointByIndex(uint index, [MarshalAs(UnmanagedType.Interface)] out object breakpoint);
    [PreserveSig] int GetBreakpointById(uint id, [MarshalAs(UnmanagedType.Interface)] out object breakpoint);
    [PreserveSig] int GetBreakpointParameters(uint count, uint[]? ids, uint start, IntPtr parameters);
    [PreserveSig] int AddBreakpoint(uint type, uint desiredId, [MarshalAs(UnmanagedType.Interface)] out object breakpoint);
    [PreserveSig] int RemoveBreakpoint([MarshalAs(UnmanagedType.Interface)] object breakpoint);
    [PreserveSig] int AddExtension([MarshalAs(UnmanagedType.LPStr)] string path, uint flags, out ulong handle);
    [PreserveSig] int RemoveExtension(ulong handle);
    [PreserveSig] int GetExtensionByPath([MarshalAs(UnmanagedType.LPStr)] string path, out ulong handle);
    [PreserveSig] int CallExtension(ulong handle, [MarshalAs(UnmanagedType.LPStr)] string function, [MarshalAs(UnmanagedType.LPStr)] string? arguments);
    [PreserveSig] int GetExtensionFunction(ulong handle, [MarshalAs(UnmanagedType.LPStr)] string funcName, out IntPtr function);
    [PreserveSig] int GetWindbgExtensionApis32(IntPtr api);
    [PreserveSig] int GetWindbgExtensionApis64(IntPtr api);
    [PreserveSig] int GetNumberEventFilters(out uint specificEvents, out uint specificExceptions, out uint arbitraryExceptions);
    [PreserveSig] int GetEventFilterText(uint index, StringBuilder? buffer, uint bufferSize, out uint textSize);
    [PreserveSig] int GetEventFilterCommand(uint index, StringBuilder? buffer, uint bufferSize, out uint commandSize);
    [PreserveSig] int SetEventFilterCommand(uint index, [MarshalAs(UnmanagedType.LPStr)] string command);
    [PreserveSig] int GetSpecificFilterParameters(uint start, uint count, IntPtr parameters);
    [PreserveSig] int SetSpecificFilterParameters(uint start, uint count, IntPtr parameters);
    [PreserveSig] int GetSpecificFilterArgument(uint index, StringBuilder? buffer, uint bufferSize, out uint argumentSize);
    [PreserveSig] int SetSpecificFilterArgument(uint index, [MarshalAs(UnmanagedType.LPStr)] string argument);
    [PreserveSig] int GetExceptionFilterParameters(uint count, uint[]? codes, uint start, IntPtr parameters);
    [PreserveSig] int SetExceptionFilterParameters(uint count, IntPtr parameters);
    [PreserveSig] int GetExceptionFilterSecondCommand(uint index, StringBuilder? buffer, uint bufferSize, out uint commandSize);
    [PreserveSig] int SetExceptionFilterSecondCommand(uint index, [MarshalAs(UnmanagedType.LPStr)] string command);
    [PreserveSig] int WaitForEvent(uint flags, uint timeout);
    [PreserveSig] int GetLastEventInformation(out uint type, out uint processId, out uint threadId, IntPtr extraInformation, uint extraInformationSize, out uint extraInformationUsed, StringBuilder? description, uint descriptionSize, out uint descriptionUsed);
}

[ComImport, Guid("8c31e98c-983a-48a5-9016-6fe5d667a950"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDebugSymbols
{
    [PreserveSig] int GetSymbolOptions(out uint options);
    [PreserveSig] int AddSymbolOptions(uint options);
    [PreserveSig] int RemoveSymbolOptions(uint options);
    [PreserveSig] int SetSymbolOptions(uint options);
    [PreserveSig] int GetNameByOffset(ulong offset, StringBuilder? nameBuffer, uint nameBufferSize, out uint nameSize, out ulong displacement);
    [PreserveSig] int GetOffsetByName([MarshalAs(UnmanagedType.LPStr)] string symbol, out ulong offset);
    [PreserveSig] int GetNearNameByOffset(ulong offset, int delta, StringBuilder? nameBuffer, uint nameBufferSize, out uint nameSize, out ulong displacement);
    [PreserveSig] int GetLineByOffset(ulong offset, out uint line, StringBuilder? fileBuffer, uint fileBufferSize, out uint fileSize, out ulong displacement);
    [PreserveSig] int GetOffsetByLine(uint line, [MarshalAs(UnmanagedType.LPStr)] string file, out ulong offset);
    [PreserveSig] int GetNumberModules(out uint loaded, out uint unloaded);
    [PreserveSig] int GetModuleByIndex(uint index, out ulong @base);
}

[ComImport, Guid("88f7dfab-3ea7-4c3a-aefb-c4e8106173aa"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDebugDataSpaces
{
    [PreserveSig] int ReadVirtual(ulong offset, byte[] buffer, uint bufferSize, out uint bytesRead);
    [PreserveSig] int WriteVirtual(ulong offset, byte[] buffer, uint bufferSize, out uint bytesWritten);
}

[ComImport, Guid("ce289126-9e84-45a7-937e-67bb18691493"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDebugRegisters
{
    [PreserveSig] int GetNumberRegisters(out uint number);
    [PreserveSig] int GetDescription(uint register, StringBuilder? nameBuffer, uint nameBufferSize, out uint nameSize, IntPtr description);
    [PreserveSig] int GetIndexByName([MarshalAs(UnmanagedType.LPStr)] string name, out uint index);
    [PreserveSig] int GetValue(uint register, IntPtr value);
    [PreserveSig] int SetValue(uint register, IntPtr value);
    [PreserveSig] int GetValues(uint count, uint[]? indices, uint start, IntPtr values);
    [PreserveSig] int SetValues(uint count, uint[]? indices, uint start, IntPtr values);
    [PreserveSig] int OutputRegisters(uint outputControl, uint flags);
    [PreserveSig] int GetInstructionOffset(out ulong offset);
    [PreserveSig] int GetStackOffset(out ulong offset);
    [PreserveSig] int GetFrameOffset(out ulong offset);
}

[ComImport, Guid("5bd9d474-5975-423a-b88b-65a8e7110e65"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDebugBreakpoint
{
    [PreserveSig] int GetId(out uint id);
    [PreserveSig] int GetType(out uint breakType, out uint procType);
    [PreserveSig] int GetAdder([MarshalAs(UnmanagedType.Interface)] out object adder);
    [PreserveSig] int GetFlags(out uint flags);
    [PreserveSig] int AddFlags(uint flags);
    [PreserveSig] int RemoveFlags(uint flags);
    [PreserveSig] int SetFlags(uint flags);
    [PreserveSig] int GetOffset(out ulong offset);
    [PreserveSig] int SetOffset(ulong offset);
    [PreserveSig] int GetDataParameters(out uint size, out uint accessType);
    [PreserveSig] int SetDataParameters(uint size, uint accessType);
    [PreserveSig] int GetPassCount(out uint count);
    [PreserveSig] int SetPassCount(uint count);
    [PreserveSig] int GetCurrentPassCount(out uint count);
    [PreserveSig] int GetMatchThreadId(out uint id);
    [PreserveSig] int SetMatchThreadId(uint thread);
    [PreserveSig] int GetCommand(StringBuilder? buffer, uint bufferSize, out uint commandSize);
    [PreserveSig] int SetCommand([MarshalAs(UnmanagedType.LPStr)] string command);
    [PreserveSig] int GetOffsetExpression(StringBuilder? buffer, uint bufferSize, out uint expressionSize);
    [PreserveSig] int SetOffsetExpression([MarshalAs(UnmanagedType.LPStr)] string expression);
    [PreserveSig] int GetParameters(IntPtr parameters);
}

/// <summary>呼びやすくするための薄い拡張。スパイクなので最低限だけ。</summary>
internal static class DbgExtensions
{
    public static int GetOffsetByName(this IDebugSymbols symbols, string name, out ulong offset) =>
        symbols.GetOffsetByName(name, out offset);

    public static int GetNameByOffset(this IDebugSymbols symbols, ulong offset, out string name)
    {
        var buffer = new StringBuilder(512);
        var hr = symbols.GetNameByOffset(offset, buffer, (uint)buffer.Capacity, out _, out var displacement);
        name = hr >= 0 ? $"{buffer}+0x{displacement:X}" : "(unknown)";
        return hr;
    }
}
