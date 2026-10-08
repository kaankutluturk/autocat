using System;
using System.Drawing;
using System.Windows.Forms;
using AutoCat;

// The application icon at one exact size, read from the executable's own icon resource so that each size Windows
// asks for is drawn from the matching image instead of a scaled copy.
sealed class AppIcon : IDisposable {
    IntPtr handle;
    public readonly Icon Icon;

    AppIcon(IntPtr handle) {
        this.handle = handle;
        Icon = Icon.FromHandle(handle);
    }

    public IntPtr Handle { get { return handle; } }

    public static AppIcon Load(int size) {
        try {
            var icons = new IntPtr[1];
            var ids = new uint[1];
            if (Native.PrivateExtractIcons(Application.ExecutablePath, 0, size, size, icons, ids, 1, 0) == 1 &&
                    icons[0] != IntPtr.Zero)
                return new AppIcon(icons[0]);
        } catch (Exception) {
        }
        return null;
    }

    // The icon sizes of the primary display, where the taskbar and the notification area are.
    public static int SmallSize { get { return Metric(49); } }
    public static int LargeSize { get { return Metric(11); } }

    static int Metric(int index) {
        try {
            var monitor = Native.MonitorFromPoint(new Native.NativePoint(), Native.MONITOR_DEFAULTTOPRIMARY);
            uint x, y;
            if (Native.GetDpiForMonitor(monitor, 0, out x, out y) == 0 && x > 0) {
                int size = Native.GetSystemMetricsForDpi(index, x);
                if (size > 0)return size;
            }
        } catch (Exception) {
        }
        return Native.GetSystemMetrics(index);
    }

    public void Dispose() {
        if (handle == IntPtr.Zero)return;
        Native.DestroyIcon(handle);
        handle = IntPtr.Zero;
    }
}

// What the notification area icon shows; the status is limited to the connection states the footer reports, so a
// failure message never reaches a tooltip.
static class TrayText {
    // Windows shows at most 63 characters of an icon's tooltip.
    public const int Limit = 63;

    static readonly string[] Known = {
        "waiting for game",
        "attaching",
        "attached",
        "waiting for component",
        "waiting for game initialization"
    };

    public static string Toggle(bool visible) { return visible ? "hide menu" : "show menu"; }

    public static string Tooltip(string connection, bool running) {
        return "autocat\n" + (Array.IndexOf(Known, connection) >= 0 ? connection : "connection problem") + " · " +
            (running ? "running" : "paused");
    }
}

// The notification area icon as the menu uses it, so tests can drive the menu without a real icon.
interface ITray : IDisposable {
    string ToolTip { set; }
    string ToggleText { set; }
    Font MenuFont { set; }
    void DisplayChanged();
}

delegate ITray TrayFactory(Action toggle, Action unload);

sealed class NotifyTray : ITray {
    internal static int Created;
    readonly NotifyIcon icon = new NotifyIcon();
    readonly ContextMenuStrip menu;
    readonly ToolStripItem toggle;
    AppIcon image;
    string tip = "";
    bool disposed;

    public NotifyTray(Action onToggle, Action onUnload) {
        Created++;
        menu = Build(onToggle, onUnload, out toggle);
        try {
            icon.ContextMenuStrip = menu;
            icon.MouseClick += (s, e) => {
                if (e.Button == MouseButtons.Left)onToggle();
            };
            ToolTip = "autocat";
            SetIcon();
            icon.Visible = true;
            AppDomain.CurrentDomain.UnhandledException += Remove;
            AppDomain.CurrentDomain.ProcessExit += Remove;
        } catch (Exception) {
            Dispose();
            throw;
        }
    }

    internal static ContextMenuStrip Build(Action onToggle, Action onUnload, out ToolStripItem toggleItem) {
        var menu = Skin.DarkMenu();
        toggleItem = menu.Items.Add(TrayText.Toggle(false), null, (s, e) => onToggle());
        menu.Items.Add("unload", null, (s, e) => onUnload());
        return menu;
    }

    public string ToolTip {
        set {
            if (disposed || value == tip)return;
            tip = value;
            icon.Text = value;
        }
    }

    public string ToggleText {
        set {
            if (!disposed)toggle.Text = value;
        }
    }

    public Font MenuFont {
        set {
            if (!disposed)menu.Font = value;
        }
    }

    // Each size of the icon is read from the executable, so the notification area never has to scale one.
    void SetIcon() {
        var next = AppIcon.Load(AppIcon.SmallSize);
        icon.Icon = next != null ? next.Icon : SystemIcons.Application;
        if (image != null)image.Dispose();
        image = next;
    }

    public void DisplayChanged() {
        if (!disposed)SetIcon();
    }

    // Also runs when the process ends without closing the menu, so no icon is left behind.
    void Remove(object sender, EventArgs e) {
        try {
            icon.Visible = false;
        } catch (Exception) {
        }
    }

    public void Dispose() {
        if (disposed)return;
        disposed = true;
        AppDomain.CurrentDomain.UnhandledException -= Remove;
        AppDomain.CurrentDomain.ProcessExit -= Remove;
        try {
            icon.Visible = false;
        } finally {
            icon.Dispose();
            menu.Dispose();
            if (image != null)image.Dispose();
        }
    }
}