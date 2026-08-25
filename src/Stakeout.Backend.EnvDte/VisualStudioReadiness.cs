using System.Runtime.InteropServices;
using System.Text;
using Stakeout.Core;
using Stakeout.Rpc;

namespace Stakeout.Backend.EnvDte;

/// <param name="Ready">DTE を呼べる状態か。</param>
/// <param name="Reason">呼べない理由。呼べるときは null。</param>
/// <param name="BlockingWindow">操作を止めているダイアログのタイトル。無ければ null。</param>
public sealed record VisualStudioReadiness(bool Ready, string? Reason, string? BlockingWindow);

/// <summary>
/// Visual Studio が DTE 呼び出しを受けられる状態かを Win32 で判定する（ADR 0004）。
///
/// **COM で判定しない。** COM が応答しない状況こそ判定したい対象であり、
/// COM で問い合わせると同じ理由でブロックされる。実測では、モーダルダイアログが
/// 出ている間の DTE 呼び出しは 60 秒リトライしても回復しなかった。
/// </summary>
internal static class VisualStudioReadinessCheck
{
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hwnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowEnabled(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint command);

    private const uint GwOwner = 4;

    private sealed record TopLevelWindow(IntPtr Handle, string Title, bool Enabled, bool Owned);

    /// <summary>指定プロセスの可視トップレベルウィンドウから、DTE を呼べる状態かを判定する。</summary>
    public static VisualStudioReadiness Check(int processId)
    {
        var windows = TopLevelWindows(processId);

        if (windows.Count == 0)
        {
            return new VisualStudioReadiness(
                false,
                "Visual Studio のウィンドウがまだありません。起動中か、スタートウィンドウのままです。",
                null);
        }

        // オーナーを持たない = メインウィンドウ候補
        var main = windows.FirstOrDefault(w => !w.Owned && w.Title.Length > 0)
                   ?? windows.FirstOrDefault(w => !w.Owned);

        if (main is null)
        {
            return new VisualStudioReadiness(
                false,
                "Visual Studio のメインウィンドウが見つかりません。起動中です。",
                null);
        }

        if (main.Enabled)
        {
            return new VisualStudioReadiness(true, null, null);
        }

        // メインウィンドウが無効 = モーダルが出ている。有効な別ウィンドウがそれ
        var modal = windows.FirstOrDefault(w => w.Enabled && w.Handle != main.Handle);

        return new VisualStudioReadiness(
            false,
            "Visual Studio がモーダルダイアログを表示しているため操作できません。",
            modal?.Title is { Length: > 0 } title ? title : "(タイトル不明)");
    }

    /// <summary>呼べない状態なら例外にする。hint に「何を閉じればよいか」を入れる。</summary>
    public static void EnsureReady(int processId)
    {
        var readiness = Check(processId);
        if (readiness.Ready)
        {
            return;
        }

        var hint = readiness.BlockingWindow is { } window
            ? $"ダイアログ「{window}」を閉じてから再実行してください。"
            : "Visual Studio でソリューションまたはファイルを開き、スタートウィンドウを抜けてください。";

        throw new BackendException(
            ErrorCodes.Precondition,
            $"{readiness.Reason} (pid {processId})",
            hint);
    }

    private static List<TopLevelWindow> TopLevelWindows(int processId)
    {
        var result = new List<TopLevelWindow>();

        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var owner);
            if (owner != (uint)processId || !IsWindowVisible(hwnd))
            {
                return true;
            }

            var buffer = new StringBuilder(512);
            GetWindowTextW(hwnd, buffer, buffer.Capacity);

            result.Add(new TopLevelWindow(
                hwnd,
                buffer.ToString(),
                IsWindowEnabled(hwnd),
                GetWindow(hwnd, GwOwner) != IntPtr.Zero));

            return true;
        }, IntPtr.Zero);

        return result;
    }
}
