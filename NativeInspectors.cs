using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace WorkManager;

/// <summary>Enumereert zichtbare toplevel-vensters met bijbehorende procesnaam.</summary>
internal static class WindowInspector
{
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private const uint WM_CLOSE = 0x0010;

    public static List<(string ProcessName, string Title, IntPtr Handle)> GetVisibleWindows()
    {
        var processNames = new Dictionary<uint, string>();
        foreach (var p in Process.GetProcesses())
        {
            processNames[(uint)p.Id] = p.ProcessName;
        }

        var windows = new List<(string, string, IntPtr)>();
        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd))
            {
                return true;
            }

            var length = GetWindowTextLength(hWnd);
            if (length == 0)
            {
                return true;
            }

            var sb = new StringBuilder(length + 1);
            GetWindowText(hWnd, sb, sb.Capacity);
            GetWindowThreadProcessId(hWnd, out var pid);

            if (processNames.TryGetValue(pid, out var name))
            {
                windows.Add((name, sb.ToString(), hWnd));
            }
            return true;
        }, IntPtr.Zero);

        return windows;
    }

    /// <summary>Vraagt het venster netjes te sluiten (WM_CLOSE); de app kan nog om opslaan vragen.</summary>
    public static void CloseWindow(IntPtr hWnd) => PostMessage(hWnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
}

/// <summary>Verplaatst vensters naar een specifiek scherm en maximaliseert ze daar.</summary>
internal static class WindowPositioner
{
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int cmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extraInfo);

    private const int SW_RESTORE = 9;
    private const int SW_MAXIMIZE = 3;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const byte VK_MENU = 0x12;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    /// <summary>Schermen geordend van links naar rechts.</summary>
    public static int ScreenCount => Screen.AllScreens.Length;

    /// <summary>Maximaliseert het venster op het opgegeven scherm (1 = meest links).</summary>
    public static bool MaximizeOnMonitor(IntPtr hWnd, int monitor)
    {
        var screens = Screen.AllScreens.OrderBy(s => s.Bounds.X).ThenBy(s => s.Bounds.Y).ToArray();
        if (monitor < 1 || monitor > screens.Length)
        {
            return false;
        }

        // Eerst herstellen en naar het doelscherm verplaatsen; maximaliseren gebeurt daarna
        // op het scherm waar het venster zich dan bevindt.
        var area = screens[monitor - 1].WorkingArea;
        ShowWindow(hWnd, SW_RESTORE);
        SetWindowPos(hWnd, IntPtr.Zero, area.X, area.Y, area.Width, area.Height, SWP_NOZORDER | SWP_NOACTIVATE);
        ShowWindow(hWnd, SW_MAXIMIZE);
        return true;
    }

    /// <summary>Maximaliseert het venster op het scherm waar het nu staat.</summary>
    public static void Maximize(IntPtr hWnd) => ShowWindow(hWnd, SW_MAXIMIZE);

    /// <summary>
    /// Haalt het venster naar de voorgrond. Windows staat SetForegroundWindow vanuit een
    /// achtergrondproces alleen toe na recente input; een loze Alt-toets omzeilt dat.
    /// </summary>
    public static void BringToFront(IntPtr hWnd)
    {
        keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
        keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        SetForegroundWindow(hWnd);
    }
}

/// <summary>
/// Leest de working directory van een ander proces via de PEB
/// (RTL_USER_PROCESS_PARAMETERS.CurrentDirectory). Alleen x64→x64, zelfde gebruiker.
/// </summary>
internal static class ProcessInspector
{
    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2_0;
        public IntPtr Reserved2_1;
        public IntPtr UniqueProcessId;
        public IntPtr Reserved3;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle, int processInformationClass,
        ref PROCESS_BASIC_INFORMATION processInformation, int processInformationLength, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(
        IntPtr process, IntPtr baseAddress, byte[] buffer, IntPtr size, out IntPtr bytesRead);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    private const uint PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint PROCESS_VM_READ = 0x0010;

    // x64-offsets: PEB.ProcessParameters = +0x20, RTL_USER_PROCESS_PARAMETERS.CurrentDirectory = +0x38,
    // RTL_USER_PROCESS_PARAMETERS.CommandLine = +0x70.
    private const int PebProcessParametersOffset = 0x20;
    private const int CurrentDirectoryOffset = 0x38;
    private const int CommandLineOffset = 0x70;

    public static string? GetWorkingDirectory(int pid) => ReadProcessParameterString(pid, CurrentDirectoryOffset);

    public static string? GetCommandLine(int pid) => ReadProcessParameterString(pid, CommandLineOffset);

    /// <summary>Pid van het ouderproces (InheritedFromUniqueProcessId uit de PEB), of null.</summary>
    public static int? GetParentProcessId(int pid)
    {
        var handle = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, pid);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var pbi = new PROCESS_BASIC_INFORMATION();
            if (NtQueryInformationProcess(handle, 0, ref pbi, Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(), out _) != 0)
            {
                return null;
            }
            return (int)pbi.Reserved3;
        }
        catch
        {
            return null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static string? ReadProcessParameterString(int pid, int parameterOffset)
    {
        var handle = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, pid);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var pbi = new PROCESS_BASIC_INFORMATION();
            if (NtQueryInformationProcess(handle, 0, ref pbi, Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(), out _) != 0
                || pbi.PebBaseAddress == IntPtr.Zero)
            {
                return null;
            }

            var processParameters = ReadPointer(handle, pbi.PebBaseAddress + PebProcessParametersOffset);
            if (processParameters == IntPtr.Zero)
            {
                return null;
            }

            // Beide parameters beginnen met een UNICODE_STRING { ushort Length; ushort MaxLength; pad; ptr Buffer }.
            var unicodeString = new byte[16];
            if (!ReadBytes(handle, processParameters + parameterOffset, unicodeString))
            {
                return null;
            }

            var length = BitConverter.ToUInt16(unicodeString, 0);
            var buffer = (IntPtr)BitConverter.ToInt64(unicodeString, 8);
            if (length == 0 || buffer == IntPtr.Zero)
            {
                return null;
            }

            var chars = new byte[length];
            return ReadBytes(handle, buffer, chars) ? Encoding.Unicode.GetString(chars) : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static IntPtr ReadPointer(IntPtr process, IntPtr address)
    {
        var buffer = new byte[8];
        return ReadBytes(process, address, buffer) ? (IntPtr)BitConverter.ToInt64(buffer, 0) : IntPtr.Zero;
    }

    private static bool ReadBytes(IntPtr process, IntPtr address, byte[] buffer)
    {
        return ReadProcessMemory(process, address, buffer, (IntPtr)buffer.Length, out var read)
            && read == (IntPtr)buffer.Length;
    }
}
