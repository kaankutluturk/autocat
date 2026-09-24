using System;using System.Runtime.InteropServices;
static class Native {
 [DllImport("user32.dll")]public static extern bool SetProcessDPIAware();
 [DllImport("user32.dll")]public static extern short GetAsyncKeyState(int key);
 [DllImport("user32.dll")]public static extern bool ReleaseCapture();
 [DllImport("user32.dll")]public static extern IntPtr SendMessage(IntPtr h,uint message,IntPtr w,IntPtr l);
 [DllImport("user32.dll")]public static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll")]public static extern bool SetForegroundWindow(IntPtr h);
 [DllImport("user32.dll")]public static extern bool IsWindow(IntPtr h);
 // Per-Monitor-V2 (Windows 10 1703+) is required to receive WM_DPICHANGED; plain SetProcessDPIAware
 // only scales once at process start and Windows silently bitmap-stretches the window instead.
 [DllImport("user32.dll")]public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
 public static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2=new IntPtr(-4);
 [StructLayout(LayoutKind.Sequential)]public struct NativeRect{public int Left,Top,Right,Bottom;}
 [StructLayout(LayoutKind.Sequential)]public struct MonitorInfo{public int Size;public NativeRect Monitor,Work;public uint Flags;}
 [DllImport("user32.dll")]public static extern IntPtr MonitorFromWindow(IntPtr h,uint flags);
 [DllImport("user32.dll")]public static extern IntPtr MonitorFromRect(ref NativeRect r,uint flags);
 [DllImport("user32.dll")]public static extern bool GetMonitorInfo(IntPtr m,ref MonitorInfo info);
 [DllImport("user32.dll")]public static extern uint GetDpiForWindow(IntPtr h);
 // Effective DPI of the monitor itself; unlike GetDpiForWindow it doesn't wait for the window to receive WM_DPICHANGED.
 [DllImport("shcore.dll")]public static extern int GetDpiForMonitor(IntPtr monitor,int type,out uint dpiX,out uint dpiY);
 public const uint MONITOR_DEFAULTTONEAREST=2;
}
