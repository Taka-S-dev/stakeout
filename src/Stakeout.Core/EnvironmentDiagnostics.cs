using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Stakeout.Rpc;

namespace Stakeout.Core;

/// <summary>
/// 環境まわりの確認項目（design.md §3.2 の Q1, Q5, Q6, Q7, Q8）。
///
/// design.md §3.2 は「実装前に必ず確認すること」を表にしたまま、
/// 埋めるのを人に任せていた。**道具が自分で答えられるものは、道具が答えるべきである。**
/// 実機で 1 コマンド打てば埋まるようにする。
/// </summary>
public static class EnvironmentDiagnostics
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWow64Process2(IntPtr process, out ushort processMachine, out ushort nativeMachine);

    private const ushort ImageFileMachineUnknown = 0;
    private const ushort ImageFileMachineI386 = 0x014C;
    private const ushort ImageFileMachineAmd64 = 0x8664;
    private const ushort ImageFileMachineArm64 = 0xAA64;

    /// <summary>Q1: Target は単一プロセスか複数プロセスか。</summary>
    public static Diagnosis ProcessCount(IReadOnlyList<TargetInfo> allowedTargets)
    {
        if (allowedTargets.Count == 0)
        {
            return new Diagnosis("Q1", "Target は単一プロセスか複数プロセスか",
                DiagnosisStatus.Skipped,
                "allowProcesses に一致するプロセスがありません。",
                null,
                "stakeout.json の allowProcesses に Target を追加してから実行してください。");
        }

        if (allowedTargets.Count == 1)
        {
            return new Diagnosis("Q1", "Target は単一プロセスか複数プロセスか",
                DiagnosisStatus.Answered,
                "単一プロセスです。",
                $"{allowedTargets[0].Name} (pid {allowedTargets[0].Pid}) の 1 つだけが一致しました。",
                null);
        }

        return new Diagnosis("Q1", "Target は単一プロセスか複数プロセスか",
            DiagnosisStatus.Caution,
            $"{allowedTargets.Count} 個のプロセスが一致します。",
            string.Join(", ", allowedTargets.Select(t => $"{t.Name}({t.Pid})")),
            "同じ製品の複数プロセスなら、design.md 付録 C（複数プロセス対応）の検討が要ります。" +
            "無関係なプロセスが混ざっているだけなら allowProcesses を絞ってください。");
    }

    /// <summary>Q5: Target の bitness。</summary>
    public static Diagnosis Bitness(int? targetPid)
    {
        if (targetPid is not { } pid)
        {
            return new Diagnosis("Q5", "Target の bitness（x86 / x64）",
                DiagnosisStatus.Skipped, "アタッチしていません。", null,
                "stakeout attach してから実行してください。");
        }

        var bitness = TargetBitness(pid);

        return bitness is null
            ? new Diagnosis("Q5", "Target の bitness（x86 / x64）",
                DiagnosisStatus.Unknown, "判定できませんでした。",
                $"pid {pid} のプロセス情報を取得できません。", null)
            : new Diagnosis("Q5", "Target の bitness（x86 / x64）",
                DiagnosisStatus.Answered, bitness,
                $"pid {pid} を IsWow64Process2 で判定しました。", null);
    }

    /// <summary>
    /// プロセスの bitness を返す。判定できなければ null。
    /// **デバッガ自身の bitness で代用しない。** WOW64 の Target を見誤る。
    /// </summary>
    public static string? TargetBitness(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);

            if (!IsWow64Process2(process.Handle, out var processMachine, out var nativeMachine))
            {
                return null;
            }

            // processMachine が UNKNOWN なら、そのプロセスはネイティブで動いている
            var machine = processMachine == ImageFileMachineUnknown ? nativeMachine : processMachine;

            return machine switch
            {
                ImageFileMachineI386 => "x86",
                ImageFileMachineAmd64 => "x64",
                ImageFileMachineArm64 => "arm64",
                _ => $"0x{machine:X4}",
            };
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return null;
        }
    }

    /// <summary>Q6: cdb.exe / dbgeng.dll の有無。</summary>
    public static Diagnosis DebuggingTools(DbgEngConfig config, string backend)
    {
        var found = new List<string>();

        foreach (var directory in CandidateDirectories(config))
        {
            var dbgeng = Path.Combine(directory, "dbgeng.dll");
            if (File.Exists(dbgeng))
            {
                found.Add(dbgeng);
            }
        }

        return DebuggingTools(found, Path.Combine(Environment.SystemDirectory, "dbgeng.dll"), backend);
    }

    /// <summary>
    /// 見つかった dbgeng.dll から Q6 に答える。
    /// **使っていない Backend の制約を「注意」として出さない。** envdte で調べているのに
    /// dbgeng の注意が並ぶと、今の接続が失敗しているように読める（実機でそう読まれた）。
    /// </summary>
    public static Diagnosis DebuggingTools(IReadOnlyList<string> found, string systemDbgEng, string backend)
    {
        if (found.Count == 0)
        {
            return new Diagnosis("Q6", "cdb.exe / dbgeng.dll の有無",
                DiagnosisStatus.Answered, "ありません。",
                null,
                "Phase 5（DbgEng Backend）には Debugging Tools for Windows が要ります。" +
                "現状は EnvDTE Backend だけで動きます。");
        }

        var onlySystem = found.All(f => string.Equals(f, systemDbgEng, StringComparison.OrdinalIgnoreCase));

        if (onlySystem && !string.Equals(backend, "dbgeng", StringComparison.OrdinalIgnoreCase))
        {
            return new Diagnosis("Q6", "cdb.exe / dbgeng.dll の有無",
                DiagnosisStatus.Answered, "System32 の同梱版だけがあります。",
                string.Join(", ", found),
                $"今の Backend は {backend} なので、この調査には影響しません。" +
                "--backend dbgeng を使うときは Debugging Tools for Windows が要ります（ADR 0019）。");
        }

        return new Diagnosis("Q6", "cdb.exe / dbgeng.dll の有無",
            onlySystem ? DiagnosisStatus.Caution : DiagnosisStatus.Answered,
            onlySystem ? "System32 の同梱版だけがあります。" : "あります。",
            string.Join(", ", found),
            onlySystem
                ? "System32 の dbgeng では、呼び出し側から停止を捉えられません（ADR 0019）。" +
                  "Phase 5 に進むなら Debugging Tools for Windows を入れ、" +
                  "spike/DbgEngProbe で D2/D3 が OK になることを先に確かめてください。"
                : null);
    }

    /// <summary>Q7: デバッガと Visual Studio の昇格レベルが揃っているか。</summary>
    public static Diagnosis Elevation(bool daemonElevated, int? visualStudioPid)
    {
        if (visualStudioPid is not { } pid)
        {
            return new Diagnosis("Q7", "Visual Studio と昇格レベルが揃っているか",
                DiagnosisStatus.Skipped, "Visual Studio に接続していません。", null, null);
        }

        // 昇格したプロセスは、非昇格からはハンドルを開けない。
        // 開けないこと自体が「相手のほうが昇格している」手がかりになる
        var accessible = CanOpenProcess(pid);

        if (accessible)
        {
            return new Diagnosis("Q7", "Visual Studio と昇格レベルが揃っているか",
                DiagnosisStatus.Answered,
                daemonElevated ? "揃っています（どちらも昇格）。" : "揃っています（どちらも非昇格）。",
                $"stakeout elevated={daemonElevated}, Visual Studio (pid {pid}) を開けました。",
                null);
        }

        return new Diagnosis("Q7", "Visual Studio と昇格レベルが揃っているか",
            DiagnosisStatus.Caution,
            "揃っていない可能性があります。",
            $"stakeout elevated={daemonElevated} ですが、Visual Studio (pid {pid}) のハンドルを開けません。",
            "Visual Studio が昇格している場合、stakeout も同じレベルで起動してください。" +
            "揃っていないと COM 呼び出しが無言で失敗します。");
    }

    /// <summary>Q8: ASan（/fsanitize=address）が使えるツールセットか。</summary>
    public static Diagnosis AddressSanitizer()
    {
        var versions = MsvcVersions();

        if (versions.Count == 0)
        {
            return new Diagnosis("Q8", "ASan が使えるツールセットか",
                DiagnosisStatus.Unknown, "MSVC ツールセットが見つかりません。",
                null,
                "この機械でビルドしないなら、ビルド機で確認してください。");
        }

        // ASan は Visual Studio 16.9 以降（MSVC 14.28.29910 以降）で使える
        var newest = versions.Max()!;
        var usable = CompareVersions(newest, "14.28") >= 0;

        return new Diagnosis("Q8", "ASan が使えるツールセットか",
            usable ? DiagnosisStatus.Answered : DiagnosisStatus.Caution,
            usable ? "使えます。" : "古いツールセットです。",
            $"見つかった MSVC: {string.Join(", ", versions)}",
            usable
                ? "検出ビルド（/fsanitize=address）を用意すると、メモリ破壊の調査が大幅に短くなります。"
                : "ASan には Visual Studio 16.9 以降が要ります。ページヒープ（gflags）で代用してください。");
    }

    private static IEnumerable<string> CandidateDirectories(DbgEngConfig config)
    {
        if (config.Path is { Length: > 0 } configured)
        {
            yield return configured;
        }

        yield return Environment.SystemDirectory;

        foreach (var root in new[] { "Program Files (x86)", "Program Files" })
        {
            var path = Path.Combine(
                Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\",
                root, "Windows Kits", "10", "Debuggers", "x64");

            if (Directory.Exists(path))
            {
                yield return path;
            }
        }
    }

    private static bool CanOpenProcess(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            _ = process.Handle;
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }

    private static IReadOnlyList<string> MsvcVersions()
    {
        try
        {
            var vswhere = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Microsoft Visual Studio", "Installer", "vswhere.exe");

            if (!File.Exists(vswhere))
            {
                return Array.Empty<string>();
            }

            var info = new ProcessStartInfo(vswhere)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            info.ArgumentList.Add("-latest");
            info.ArgumentList.Add("-prerelease");
            info.ArgumentList.Add("-property");
            info.ArgumentList.Add("installationPath");

            using var process = Process.Start(info);
            if (process is null)
            {
                return Array.Empty<string>();
            }

            var installation = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(10000);

            var toolsets = Path.Combine(installation, "VC", "Tools", "MSVC");

            return Directory.Exists(toolsets)
                ? Directory.GetDirectories(toolsets).Select(Path.GetFileName).OfType<string>().Order().ToArray()
                : Array.Empty<string>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>"14.44.35207" と "14.28" を、先頭から数値として比べる。</summary>
    public static int CompareVersions(string left, string right)
    {
        var a = Numbers(left);
        var b = Numbers(right);

        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            if (a[i] != b[i])
            {
                return a[i].CompareTo(b[i]);
            }
        }

        return 0;
    }

    private static int[] Numbers(string version) =>
        Regex.Matches(version, @"\d+").Select(m => int.Parse(m.Value)).ToArray();
}
