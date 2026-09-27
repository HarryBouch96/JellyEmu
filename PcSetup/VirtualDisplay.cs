// Helpers for the JellyEmu launcher: find the Virtual Display Driver monitor and put windows on it.
// Loaded with Add-Type from jellyemu-launch.ps1. Coordinates are physical pixels (the launcher
// makes itself per-monitor DPI aware first).
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class JeDisplay
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields;
        public int dmPositionX, dmPositionY;
        public int dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplayDevices(string lpDevice, int iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, int dwFlags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll")] static extern int GetWindowThreadProcessId(IntPtr hWnd, out int pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int max);
    delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    public static void MakeDpiAware()
    {
        SetProcessDpiAwarenessContext(new IntPtr(-4)); // PER_MONITOR_AWARE_V2
    }

    /// <summary>"x,y,width,height,\\.\DISPLAYn" of the active Virtual Display Driver monitor, or null.</summary>
    public static string FindVirtualDisplay()
    {
        var dev = new DISPLAY_DEVICE { cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE)) };
        for (int i = 0; EnumDisplayDevices(null, i, ref dev, 0); i++)
        {
            bool attached = (dev.StateFlags & 0x1) != 0; // DISPLAY_DEVICE_ATTACHED_TO_DESKTOP
            if (attached && dev.DeviceString.IndexOf("Virtual Display Driver", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var mode = new DEVMODE { dmSize = (short)Marshal.SizeOf(typeof(DEVMODE)) };
                if (EnumDisplaySettings(dev.DeviceName, -1, ref mode)) // ENUM_CURRENT_SETTINGS
                    return mode.dmPositionX + "," + mode.dmPositionY + "," + mode.dmPelsWidth + "," + mode.dmPelsHeight + "," + dev.DeviceName;
            }
            dev.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
        }
        return null;
    }

    /// <summary>Largest visible top-level window of a process, or IntPtr.Zero.</summary>
    public static IntPtr FindWindow(int pid)
    {
        IntPtr best = IntPtr.Zero; long bestArea = 0;
        EnumWindows((h, l) =>
        {
            int owner; GetWindowThreadProcessId(h, out owner);
            if (owner == pid && IsWindowVisible(h))
            {
                RECT r; GetWindowRect(h, out r);
                long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
                if (area > bestArea) { bestArea = area; best = h; }
            }
            return true;
        }, IntPtr.Zero);
        return best;
    }

    public static void MoveTo(IntPtr hWnd, int x, int y, int w, int h)
    {
        SetWindowPos(hWnd, IntPtr.Zero, x, y, w, h, 0x0040); // SWP_SHOWWINDOW
        SetForegroundWindow(hWnd);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);

    /// <summary>
    /// "Borderless full screen" on a chosen monitor: remove the title bar and border, then cover
    /// the monitor exactly. Works for any emulator, unlike their own full-screen modes, which
    /// always pick the main screen.
    /// </summary>
    public static void CoverMonitor(IntPtr hWnd, int x, int y, int w, int h)
    {
        const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
        long style = GetWindowLongPtr(hWnd, GWL_STYLE).ToInt64();
        style &= ~(0x00C00000L | 0x00040000L | 0x00080000L | 0x00020000L | 0x00010000L); // CAPTION, THICKFRAME, SYSMENU, MINIMIZEBOX, MAXIMIZEBOX
        SetWindowLongPtr(hWnd, GWL_STYLE, new IntPtr(style));
        long ex = GetWindowLongPtr(hWnd, GWL_EXSTYLE).ToInt64();
        ex &= ~(0x00000001L | 0x00000200L | 0x00020000L | 0x00000100L); // DLGMODALFRAME, CLIENTEDGE, STATICEDGE, WINDOWEDGE
        SetWindowLongPtr(hWnd, GWL_EXSTYLE, new IntPtr(ex));
        SetWindowPos(hWnd, IntPtr.Zero, x, y, w, h, 0x0020 | 0x0040); // SWP_FRAMECHANGED | SWP_SHOWWINDOW
        // Crossing onto a monitor with different scaling makes the app shrink the window
        // (WM_DPICHANGED); once it's there, set the size again.
        System.Threading.Thread.Sleep(300);
        SetWindowPos(hWnd, IntPtr.Zero, x, y, w, h, 0x0040);
        SetForegroundWindow(hWnd);
    }

    public static string Rect(IntPtr hWnd)
    {
        RECT r; GetWindowRect(hWnd, out r);
        return r.Left + "," + r.Top + "," + (r.Right - r.Left) + "," + (r.Bottom - r.Top);
    }
}
