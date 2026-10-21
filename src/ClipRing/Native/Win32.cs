using System.Runtime.InteropServices;
using System.Text;

namespace ClipRing.Native;

internal static class Win32
{
    // ------------------------------------------------------------- user32
    public const int WM_HOTKEY = 0x0312;
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;

    public const int GWL_STYLE = -16;
    public const uint WS_CAPTION = 0x00C00000;
    public const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);

    public const uint MONITORINFOF_PRIMARY = 0x1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    public static extern bool GetMonitorInfoEx(IntPtr hMonitor, ref MONITORINFOEX info);

    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

    [DllImport("user32.dll")]
    public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    /// <summary>Monitors in EnumDisplayMonitors order (the order FFmpeg's gfxcapture uses for monitor_idx).</summary>
    public static List<(string Device, int Width, int Height, bool Primary)> EnumerateMonitors()
    {
        var list = new List<(string, int, int, bool)>();
        MonitorEnumProc callback = (IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data) =>
        {
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>(), szDevice = "" };
            if (GetMonitorInfoEx(hMonitor, ref info))
            {
                list.Add((info.szDevice, info.rcMonitor.Width, info.rcMonitor.Height,
                          (info.dwFlags & MONITORINFOF_PRIMARY) != 0));
            }
            return true;
        };
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        return list;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    public static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    // ------------------------------------------------------------- kernel32
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool QueryFullProcessImageName(IntPtr hProcess, uint flags, StringBuilder exeName, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    // ------------------------------------------------------------- winmm
    [DllImport("winmm.dll")]
    public static extern uint timeBeginPeriod(uint period);

    [DllImport("winmm.dll")]
    public static extern uint timeEndPeriod(uint period);

    // ------------------------------------------------------------- helpers
    public static string? GetProcessPath(uint pid)
    {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            return QueryFullProcessImageName(handle, 0, sb, ref size) ? sb.ToString(0, (int)size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    public static string GetClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        return GetClassName(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }

    /// <summary>True when the window exactly covers its monitor (borderless or exclusive fullscreen).</summary>
    public static bool IsFullscreenWindow(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var rect)) return false;
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return false;

        bool coversMonitor = rect.Left <= info.rcMonitor.Left && rect.Top <= info.rcMonitor.Top
                          && rect.Right >= info.rcMonitor.Right && rect.Bottom >= info.rcMonitor.Bottom;
        if (!coversMonitor) return false;

        // Maximized normal windows still have a caption; games in borderless mode do not.
        uint style = unchecked((uint)GetWindowLong(hwnd, GWL_STYLE));
        return (style & WS_CAPTION) != WS_CAPTION;
    }

    public static ulong TotalPhysicalMemory()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        return GlobalMemoryStatusEx(ref status) ? status.ullTotalPhys : 0;
    }
}
