using System.Diagnostics;
using SessionDeck.Interop;

namespace SessionDeck.Services;

public sealed record CandidateWindow(IntPtr Hwnd, string Title, string ProcessName);

public static class WindowEnumerator
{
    /// <summary>
    /// Top-level windows eligible for tiling: visible, titled, not tool windows,
    /// not cloaked (UWP ghosts), and not SessionDeck itself.
    /// </summary>
    public static List<CandidateWindow> GetCandidates()
    {
        var result = new List<CandidateWindow>();
        int ownPid = Environment.ProcessId;
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!IsEligible(hwnd, ownPid)) return true;
            result.Add(new CandidateWindow(hwnd, NativeMethods.GetWindowTextSafe(hwnd), GetProcessName(hwnd)));
            return true;
        }, IntPtr.Zero);
        return result;
    }

    public static bool IsEligible(IntPtr hwnd, int ownPid)
    {
        if (!NativeMethods.IsWindowVisible(hwnd)) return false;
        if (NativeMethods.GetWindowTextSafe(hwnd).Length == 0) return false;
        long exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        if ((exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0) return false;
        if (NativeMethods.IsCloaked(hwnd)) return false;
        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == ownPid) return false;
        return true;
    }

    public static int GetProcessId(IntPtr hwnd)
    {
        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        return (int)pid;
    }

    public static string GetProcessName(IntPtr hwnd)
    {
        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch
        {
            return "";
        }
    }
}
