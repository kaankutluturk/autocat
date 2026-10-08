using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using System.Runtime.InteropServices;

namespace AutoCat {
static class KeyNames {
    public static string Name(int key, int mods = 0) {
        string name;
        switch (key) {
            case 0:
                return "none";
            case 1:
                name = "mouse 1";
                break;
            case 2:
                name = "mouse 2";
                break;
            case 4:
                name = "mouse 3";
                break;
            case 5:
                name = "mouse 4";
                break;
            case 6:
                name = "mouse 5";
                break;
            case 16:
                name = "shift";
                break;
            case 17:
                name = "ctrl";
                break;
            case 18:
                name = "alt";
                break;
            case 256:
                name = "wheel up";
                break;
            case 257:
                name = "wheel down";
                break;
            case 258:
                name = "wheel left";
                break;
            case 259:
                name = "wheel right";
                break;
            default:
                name = ((Keys)key).ToString().ToLowerInvariant();
                break;
        }
        return ((mods & 2) != 0 ? "ctrl + " : "") + ((mods & 4) != 0 ? "shift + " : "") +
            ((mods & 1) != 0 ? "alt + " : "") + ((mods & 8) != 0 ? "win + " : "") + name;
    }
}

// What the bindings read from the system; tests supply their own.
interface IInputSource {
    bool IsDown(int key);
    bool ButtonsSwapped { get; }
    Point CursorPosition { get; }
    bool WatchWheel(Action<int, Point> handler);
}

sealed class SystemInput : IInputSource {
    delegate IntPtr Hook(int code, IntPtr w, IntPtr l);

    [StructLayout(LayoutKind.Sequential)]
    struct Mouse {
        public int X, Y;
        public uint Data, Flags, Time;
        public UIntPtr Extra;
    }

    [DllImport("user32.dll")]
    static extern IntPtr SetWindowsHookEx(int type, Hook callback, IntPtr module, uint thread);
    [DllImport("user32.dll")]
    static extern bool UnhookWindowsHookEx(IntPtr h);
    [DllImport("user32.dll")]
    static extern IntPtr CallNextHookEx(IntPtr h, int c, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")]
    static extern IntPtr GetModuleHandle(string name);
    readonly Hook mouse;
    IntPtr mh;
    Action<int, Point> wheel;

    public SystemInput() { mouse = MouseEvent; }

    public bool IsDown(int key) { return (Native.GetAsyncKeyState(key) & 0x8000) != 0; }

    public bool ButtonsSwapped { get { return Native.GetSystemMetrics(23) != 0; } }

    public Point CursorPosition { get { return Cursor.Position; } }

    // The only hook AutoCat installs: it exists while a handler is set, and null removes it.
    public bool WatchWheel(Action<int, Point> handler) {
        wheel = handler;
        if (handler == null) {
            if (mh != IntPtr.Zero)UnhookWindowsHookEx(mh);
            mh = IntPtr.Zero;
            return true;
        }
        if (mh == IntPtr.Zero)mh = SetWindowsHookEx(14, mouse, GetModuleHandle(null), 0);
        return mh != IntPtr.Zero;
    }

    IntPtr MouseEvent(int c, IntPtr w, IntPtr l) {
        int msg = w.ToInt32();
        if (c >= 0 && (msg == 0x20a || msg == 0x20e)) {
            var m = (Mouse)Marshal.PtrToStructure(l, typeof(Mouse));
            var handler = wheel;
            if ((m.Flags & 1) == 0 && handler != null) {
                int delta = (short)(m.Data >> 16);
                handler(msg == 0x20a ? (delta > 0 ? 256 : 257) : (delta > 0 ? 259 : 258), new Point(m.X, m.Y));
            }
        }
        return CallNextHookEx(mh, c, w, l);
    }
}

// Polls the two bound inputs for presses, and the modifier keys only when one of them goes down.
sealed class BindingMonitor : IDisposable {
    struct Watched {
        public int Key, Source;
        public bool Down;
    }

    readonly IInputSource input;
    int first, second;
    Watched[] watched = new Watched[0];
    bool watchingWheel;
    public Action<int, int, Point> Pressed;

    public BindingMonitor(IInputSource source) { input = source; }

    static bool IsWheel(int key) { return key >= 256 && key <= 259; }

    // Modifiers are polled as their left and right keys, so pressing the second shift is a press of its own.
    static int[] Sources(int key) {
        if (key == 16)return new[] {160, 161};
        if (key == 17)return new[] {162, 163};
        if (key == 18)return new[] {164, 165};
        if (key <= 0 || key >= 256 || (key >= 160 && key <= 165))return new int[0];
        return new[] {key};
    }

    // Mouse 1 and 2 are the primary and secondary buttons, while the system reports physical ones.
    bool Read(int key) {
        if ((key == 1 || key == 2) && input.ButtonsSwapped)key ^= 3;
        return input.IsDown(key);
    }

    // Starts watching these bindings; a key already down, watched before or not, is not a press. False if a
    // wheel hook is needed and could not be installed.
    public bool Configure(int menuKey, int pauseKey) {
        first = menuKey;
        second = pauseKey;
        var next = new List<Watched>();
        foreach (int key in new[] {menuKey, pauseKey})
            foreach (int source in Sources(key)) {
                if (next.Exists(w => w.Source == source))continue;
                next.Add(new Watched {Key = key, Source = source, Down = Read(source)});
            }
        watched = next.ToArray();
        bool wheel = IsWheel(menuKey) || IsWheel(pauseKey);
        if (wheel && !watchingWheel)watchingWheel = input.WatchWheel(Wheel);
        else if (!wheel && watchingWheel) {
            input.WatchWheel(null);
            watchingWheel = false;
        }
        return watchingWheel == wheel;
    }

    int Mods(int key) {
        int m = 0;
        if (key != 17 && (input.IsDown(162) || input.IsDown(163)))m |= 2;
        if (key != 16 && (input.IsDown(160) || input.IsDown(161)))m |= 4;
        if (key != 18 && (input.IsDown(164) || input.IsDown(165)))m |= 1;
        if (key != 91 && key != 92 && (input.IsDown(91) || input.IsDown(92)))m |= 8;
        return m;
    }

    public void Poll() {
        for (int i = 0; i < watched.Length; i++) {
            int key = watched[i].Key;
            bool down = Read(watched[i].Source), pressed = down && !watched[i].Down;
            watched[i].Down = down;
            if (pressed && Pressed != null)Pressed(key, Mods(key), key < 7 ? input.CursorPosition : Point.Empty);
        }
    }

    void Wheel(int key, Point point) {
        if ((key == first || key == second) && Pressed != null)Pressed(key, Mods(key), point);
    }

    public void Dispose() {
        if (watchingWheel)input.WatchWheel(null);
        watchingWheel = false;
    }
}

sealed class BindingEditor : Control, IMessageFilter {
    readonly Func<int> key, mods;
    readonly Action<int, int> set;
    static BindingEditor active;
    public static bool Capturing { get { return active != null; } }
    // Ctrl, Shift and Alt as held right now; replaceable for tests.
    internal static Func<Keys> HeldModifiers = () => ModifierKeys;
    int modifier, windows;
    bool finishing, filtering;
    internal bool Filtering { get { return filtering; } }
    internal static int FilterCalls;

    public BindingEditor(Func<int> getKey, Func<int> getMods, Action<int, int> setter) {
        key = getKey;
        mods = getMods;
        set = setter;
        Height = 23;
        TabStop = true;
        AccessibleRole = AccessibleRole.PushButton;
        SetStyle(ControlStyles.Selectable | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
            ControlStyles.OptimizedDoubleBuffer, true);
        Cursor = Cursors.Hand;
    }

    protected override void OnPaint(PaintEventArgs e) {
        using (var p = new Pen(active == this ? Skin.Accent : Skin.Border))
            e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
        Skin.TextAt(e.Graphics, active == this ? "" : KeyNames.Name(key(), mods()), ClientRectangle, Skin.Text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    internal void Edit() {
        if (active != null)active.End();
        Focus();
        active = this;
        modifier = windows = 0;
        finishing = false;
        if (!filtering) {
            Application.AddMessageFilter(this);
            filtering = true;
        }
        Invalidate();
    }

    void End() {
        if (filtering) {
            Application.RemoveMessageFilter(this);
            filtering = false;
        }
        if (active == this)active = null;
        modifier = 0;
        Invalidate();
    }

    static bool IsModifier(int k) { return k == 16 || k == 17 || k == 18 || k == 91 || k == 92; }

    int Mods(int k) {
        var keys = HeldModifiers();
        return ((keys & Keys.Control) != 0 && k != 17 ? 2 : 0) | ((keys & Keys.Shift) != 0 && k != 16 ? 4 : 0) |
            ((keys & Keys.Alt) != 0 && k != 18 ? 1 : 0) | (windows != 0 && k != 91 && k != 92 ? 8 : 0);
    }

    void OnPress(int k) {
        if (active != this || finishing)return;
        if (IsModifier(k)) {
            modifier = k;
            return;
        }
        Accept(k == 27 ? 0 : k, k == 27 ? 0 : Mods(k));
    }

    void OnRelease(int k) {
        if (active == this && !finishing && k == modifier)Accept(k, 0);
    }

    void Accept(int k, int m) {
        finishing = true;
        BeginInvoke(new Action(() => {
            if (active != this)return;
            try {
                set(k, m);
            } finally {
                End();
            }
        }));
    }

    // While choosing, the menu's own keyboard, mouse button and wheel messages are the input, and the menu
    // consumes them so they do nothing else.
    public bool PreFilterMessage(ref Message m) {
        FilterCalls++;
        if (active != this)return false;
        var target = FromChildHandle(m.HWnd);
        if (target == null || target.FindForm() != FindForm())return false;
        long w = (long)m.WParam;
        int k = 0;
        switch (m.Msg) {
            case 0x100:
            case 0x104:
                k = (int)(w & 0xFFFF);
                if (k == 91 || k == 92)windows |= k - 90;
                if (((long)m.LParam & 0x40000000) == 0 && k > 0 && k < 256 && k != 229)OnPress(k);
                return true;
            case 0x101:
            case 0x105:
                k = (int)(w & 0xFFFF);
                if (k == 91 || k == 92)windows &= ~(k - 90);
                // Windows delivers Print Screen to a window as a key-up only.
                if (k == 44)OnPress(k);
                else OnRelease(k);
                return true;
            case 0x201:
            case 0x203:
                OnPress(1);
                return true;
            case 0x204:
            case 0x206:
                OnPress(2);
                return true;
            case 0x207:
            case 0x209:
                OnPress(4);
                return true;
            case 0x20b:
            case 0x20d:
                OnPress((w >> 16 & 0xFFFF) == 1 ? 5 : 6);
                return true;
            case 0x20a:
                OnPress((short)(w >> 16) > 0 ? 256 : 257);
                return true;
            case 0x20e:
                OnPress((short)(w >> 16) > 0 ? 259 : 258);
                return true;
        }
        return false;
    }

    protected override void OnMouseDown(MouseEventArgs e) {
        if (active != this && e.Button == MouseButtons.Left)Edit();
        base.OnMouseDown(e);
    }

    protected override bool ProcessCmdKey(ref Message message, Keys data) {
        if (active == this)return true;
        if (data == Keys.Enter || data == Keys.Space) {
            Edit();
            return true;
        }
        return base.ProcessCmdKey(ref message, data);
    }

    protected override void OnLostFocus(EventArgs e) {
        if (active == this && !finishing)End();
        base.OnLostFocus(e);
    }

    protected override void OnVisibleChanged(EventArgs e) {
        if (!Visible && active == this)End();
        base.OnVisibleChanged(e);
    }

    protected override void Dispose(bool disposing) {
        if (disposing)End();
        base.Dispose(disposing);
    }
}
}
