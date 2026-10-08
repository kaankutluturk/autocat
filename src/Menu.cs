using System;
using System.Collections.Generic;
using System.IO;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using AutoCat;
using AutoCat.Diagnostics;

sealed class MainForm : Form {
    Settings settings;
    readonly RatePolicy rates = new RatePolicy();
    Label dangerWarning;
    MicroButton dangerConfirm;
    readonly bool inspection;

    readonly string settingsPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AutoCat",
            "settings.xml");

    readonly TapEngine taps = new TapEngine();
    BridgeClient bridge;
    BindingMonitor bindings;
    bool running, closing;
    IntPtr previous;
    readonly Panel[] pages = new Panel[5];
    readonly MenuTabs tabs = new MenuTabs();
    readonly Label footer = new Label();
    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer {Interval = 1000};
    // Short enough that a quick tap is seen and a pause feels immediate; the Windows timer runs at about 15.6 ms.
    readonly System.Windows.Forms.Timer inputTimer = new System.Windows.Forms.Timer {Interval = 15};
    readonly EventWaitHandle reopen;
    MicroButton start, emoteStart;
    string notice = "";
    DateTime noticeUntil;
    ColorWheel themeWheel;
    TextBox themeHex;
    Panel themeSwatch;
    NoticeLink updateLink;
    bool updateStarted;
    Thread updateThread;
    int footerWidth;

    ITray tray;

    // Only a launch creates the notification area icon; inspection forms get one only from a test's own factory.
    internal static TrayFactory TrayFor(bool inspect, TrayFactory given) {
        if (given != null || inspect)return given;
        return (toggle, unload) => new NotifyTray(toggle, unload);
    }

#if AUTOCAT_PAWPASS_DRYRUN
    // The type name is how package.ps1 recognizes this build.
    sealed class PawPassDryRunBuild {
    }

    internal const string Title = "autocat (pawpass dry run)";
#else
    internal const string Title = "autocat";
#endif

    public MainForm(bool inspect = false, Settings saved = null, IInputSource input = null,
            TrayFactory makeTray = null) {
        inspection = inspect;
        makeTray = TrayFor(inspect, makeTray);
        if (!inspect)
            reopen = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\autocat.xyz-show-menu");
        settings = saved ?? (inspection ? new Settings() : Settings.Read(settingsPath));
        rates.Restore(settings.ExtremeRates);
        taps.Rate = settings.Taps;
        Skin.Accent = Color.FromArgb((settings.AccentColor >> 16) & 255, (settings.AccentColor >> 8) & 255,
            settings.AccentColor & 255);
        Text = Title;
        FormBorderStyle = FormBorderStyle.None;
        UseTaskbar(!inspect);
        TopMost = true;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.None;
        ClientSize = new Size(480, 274);
        BackColor = Skin.Background;
        ForeColor = Skin.Text;
        Font = Skin.Font;
        DoubleBuffered = true;
        if (!inspection || input != null) {
            bindings = new BindingMonitor(input ?? new SystemInput());
            bindings.Pressed += OnBinding;
            ApplyBindings();
        }
        tabs.SetBounds(158, 5, 307, 40);
        tabs.Change = SelectPage;
        Controls.Add(tabs);
        for (int i = 0; i < 5; i++) {
            pages[i] = new Panel {
                Location = new Point(16, 57),
                Size = new Size(448, 179),
                BackColor = Skin.Background,
                Visible = i == 0
            };
            Controls.Add(pages[i]);
        }
        BuildClicker();
        BuildAutomation();
        BuildExperimental();
        BuildTheme();
        BuildMisc();
        footer.SetBounds(18, 245, 444, 17);
        footer.AutoEllipsis = false;
        footer.ForeColor = Skin.Muted;
        Controls.Add(footer);
        footer.MouseDown += (s, e) => DragMenu(e);
        if (Skin.Scale != 1f)Scale(new SizeF(Skin.Scale, Skin.Scale));
        footerWidth = footer.Width;
        bridge = new BridgeClient(inspection ? "" : EmbeddedRuntime.Extract(), taps);
        Apply();
        if (!inspection) {
            taps.Start();
            bridge.Start();
            timer.Tick += (s, e) => Tick();
            timer.Start();
            inputTimer.Tick += (s, e) => bindings.Poll();
            inputTimer.Start();
            displaySettle.Tick += (s, e) => SettleDisplay();
            saveTimer.Tick += (s, e) => {
                saveTimer.Stop();
                try {
                    Save();
                } catch (Exception ex) {
                    Diag.Fault("Settings", "autosave.failed", ex);
                }
            };
            var diagnostics = Skin.DarkMenu();
            diagnostics.Items.Add("open logs folder", null, (s, e) => {
                try {
                    Directory.CreateDirectory(DiagnosticSession.Folder);
                    System.Diagnostics.Process.Start(DiagnosticSession.Folder);
                } catch (Exception ex) {
                    Diag.Fault("Diagnostics", "open.failed", ex);
                    Notice("Could not open logs folder");
                }
            });
            diagnostics.Items.Add("copy session ID", null, (s, e) => CopyDiagnostic(Diag.Session));
            diagnostics.Items.Add("copy diagnostic summary", null,
                (s, e) => CopyDiagnostic(DiagnosticSession.Summary()));
            footer.ContextMenuStrip = diagnostics;
            Shown += (s, e) => BeginUpdateCheck();
            LogConfiguration();
            Diag.Write(LogLevel.Info, "Menu", "opened", "startup visible; automation paused");
        }
        FormClosing += (s, e) => Shutdown();
        if (makeTray != null)
            try {
                tray = makeTray(() => ToggleMenu(true), Unload);
            } catch (Exception) {
                Diag.Write(LogLevel.Warn, "Menu", "tray.unavailable", "Notification area icon could not be created");
            }
        UpdateText();
    }

    protected override void OnVisibleChanged(EventArgs e) {
        base.OnVisibleChanged(e);
        if (tray != null)tray.ToggleText = TrayText.Toggle(Visible);
    }

    void UpdateTray() {
        if (tray != null)
            tray.ToolTip = TrayText.Tooltip(bridge == null ? "waiting for game" : bridge.Connection, running);
    }

    void DisposeTray() {
        var gone = tray;
        tray = null;
        if (gone == null)return;
        try {
            gone.Dispose();
        } catch (Exception e) {
            Diag.Fault("Menu", "tray.dispose.failed", e);
        }
    }

    // The taskbar shows an app window while it is visible and none once it is hidden; without a taskbar the menu
    // stays a tool window, which never has a button. Inspection forms never get one.
    bool taskbarWindow;
    AppIcon smallIcon, largeIcon;
    internal Func<IntPtr, bool> setForeground = Native.SetForegroundWindow;

    internal void UseTaskbar(bool on) {
        taskbarWindow = on;
        ShowInTaskbar = on;
    }

    protected override CreateParams CreateParams {
        get {
            var p = base.CreateParams;
            if (taskbarWindow) {
                p.ExStyle = (p.ExStyle & ~Native.WS_EX_TOOLWINDOW) | Native.WS_EX_APPWINDOW;
                p.Style |= Native.WS_MINIMIZEBOX;
            } else p.ExStyle = (p.ExStyle & ~Native.WS_EX_APPWINDOW) | Native.WS_EX_TOOLWINDOW;
            p.Style &= ~Native.WS_MAXIMIZEBOX;
            return p;
        }
    }

    // Form sets its own icon at the end of CreateHandle, so this runs after it.
    protected override void CreateHandle() {
        base.CreateHandle();
        if (taskbarWindow)ApplyIcons();
    }

    // The window icon follows the primary display's icon sizes, so the taskbar and Alt+Tab never scale a small image up.
    void ApplyIcons() {
        var small = AppIcon.Load(AppIcon.SmallSize);
        var large = AppIcon.Load(AppIcon.LargeSize);
        if (small != null)Native.SendMessage(Handle, WM_SETICON, IntPtr.Zero, small.Handle);
        if (large != null)Native.SendMessage(Handle, WM_SETICON, new IntPtr(1), large.Handle);
        if (smallIcon != null)smallIcon.Dispose();
        if (largeIcon != null)largeIcon.Dispose();
        smallIcon = small;
        largeIcon = large;
    }

    protected override void Dispose(bool disposing) {
        if (disposing) {
            DisposeTray();
            if (smallIcon != null)smallIcon.Dispose();
            if (largeIcon != null)largeIcon.Dispose();
        }
        base.Dispose(disposing);
    }

    const int WM_DPICHANGED = 0x02E0, WM_DISPLAYCHANGE = 0x007E, WM_SYSCOMMAND = 0x0112, WM_SETICON = 0x0080,
        WM_SIZE = 0x0005, SC_MINIMIZE = 0xF020, SC_MAXIMIZE = 0xF030, SIZE_MINIMIZED = 1, SIZE_MAXIMIZED = 2;

    // What the taskbar sends when its button is used on the window that is in front.
    internal static bool IsMinimize(long command) { return (command & 0xFFF0) == SC_MINIMIZE; }

    internal static bool IsMaximize(long command) { return (command & 0xFFF0) == SC_MAXIMIZE; }

    // Anything else that minimizes or maximizes the window, such as the shell minimizing every window, is undone
    // after the fact: the menu is hidden like the binding hides it, or put back at its size.
    void Settle() {
        if (closing || IsDisposed || !IsHandleCreated)return;
        if (Native.IsZoomed(Handle))Native.ShowWindow(Handle, Native.SW_RESTORE);
        if (!Native.IsIconic(Handle))return;
        Native.ShowWindow(Handle, Native.SW_SHOWNOACTIVATE);
        HideMenu(false);
    }
    float dpiScale = EntryPoint.StartupDpiScale;
    // After a resolution switch Windows may change the monitor's scaling late, or without ever sending this window
    // WM_DPICHANGED, so a resolution change is applied immediately and once more after things settle.
    readonly System.Windows.Forms.Timer displaySettle = new System.Windows.Forms.Timer {Interval = 1000};

    // Monitor bounds come from the native API: WinForms' Screen list is cached and can still report the
    // previous resolution while a display change is being delivered.
    protected override void WndProc(ref Message m) {
        if (m.Msg == WM_SYSCOMMAND && IsMinimize((long)m.WParam)) {
            if (!closing)HideMenu();
            return;
        }
        if (m.Msg == WM_SYSCOMMAND && IsMaximize((long)m.WParam))return;
        if (m.Msg == WM_SIZE && ((int)m.WParam == SIZE_MINIMIZED || (int)m.WParam == SIZE_MAXIMIZED)) {
            base.WndProc(ref m);
            BeginInvoke(new Action(Settle));
            return;
        }
        if (inspection || (m.Msg != WM_DPICHANGED && m.Msg != WM_DISPLAYCHANGE)) {
            base.WndProc(ref m);
            return;
        }
        base.WndProc(ref m);
        try {
            if (m.Msg == WM_DPICHANGED) {
                dpiScale = ((int)((long)m.WParam & 0xFFFF)) / 96f;
                var suggested =
                    (Native.NativeRect)System.Runtime.InteropServices.Marshal.PtrToStructure(m.LParam,
                        typeof(Native.NativeRect));
                Location = new Point(suggested.Left, suggested.Top);
                ApplyMonitor(Native.MonitorFromRect(ref suggested, Native.MONITOR_DEFAULTTONEAREST), "dpi",
                    false);
            } else {
                ApplyMonitor(Native.MonitorFromWindow(Handle, Native.MONITOR_DEFAULTTONEAREST), "resolution",
                    true);
                displaySettle.Stop();
                displaySettle.Start();
            }
        } catch (Exception e) {
            Diag.Fault("Menu", "display.change.failed", e);
        }
    }

    void SettleDisplay() {
        displaySettle.Stop();
        try {
            ApplyMonitor(Native.MonitorFromWindow(Handle, Native.MONITOR_DEFAULTTONEAREST), "settled", true);
        } catch (Exception e) {
            Diag.Fault("Menu", "display.change.failed", e);
        }
    }

    void ApplyMonitor(IntPtr monitor, string source, bool readMonitorDpi) {
        if (readMonitorDpi && EntryPoint.DpiMode == "per-monitor-v2") {
            uint x, y;
            if (Native.GetDpiForMonitor(monitor, 0, out x, out y) == 0 && x > 0)dpiScale = x / 96f;
        }
        var info = new Native.MonitorInfo {
            Size = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.MonitorInfo))
        };
        if (!Native.GetMonitorInfo(monitor, ref info))return;
        ApplyDisplay(dpiScale,
            new Size(info.Monitor.Right - info.Monitor.Left, info.Monitor.Bottom - info.Monitor.Top),
            Rectangle.FromLTRB(info.Work.Left, info.Work.Top, info.Work.Right, info.Work.Bottom), source);
    }

    void ApplyDisplay(float dpi, Size screen, Rectangle work, string source) {
        float scale = EntryPoint.ComputeScale(dpi, screen);
        Diag.Write(LogLevel.Info, "Menu", "display.changed",
            "source=" + source + " dpi=" + (int)Math.Round(dpi * 96) + " screen=" + screen.Width + "x" +
            screen.Height + " scale=" +
            scale.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
        if (Math.Abs(scale - Skin.Scale) > 0.001f)RebuildUI(scale);
        if (!work.Contains(Bounds))
            Location = new Point(Math.Max(work.Left, work.Left + (work.Width - Width) / 2),
                Math.Max(work.Top, work.Top + (work.Height - Height) / 2));
        if (taskbarWindow && IsHandleCreated)ApplyIcons();
        if (tray != null)tray.DisplayChanged();
    }

    // Re-scaling an already-scaled tree drifts from rounding, so every DPI change rebuilds the
    // whole control tree fresh from the fixed 480x274 baseline instead, same as the startup scale.
    void RebuildUI(float newScale) {
        int selected = tabs.Selected;
        for (int i = 0; i < pages.Length; i++) {
            if (pages[i] != null) {
                Controls.Remove(pages[i]);
                pages[i].Dispose();
                pages[i] = null;
            }
        }
        Skin.InitializeScale(newScale);
        ClientSize = new Size(480, 274);
        Font = Skin.Font;
        tabs.SetBounds(158, 5, 307, 40);
        footer.SetBounds(18, 245, 444, 17);
        if (footer.ContextMenuStrip != null)footer.ContextMenuStrip.Font = Skin.Font;
        if (tray != null)tray.MenuFont = Skin.Font;
        for (int i = 0; i < 5; i++) {
            pages[i] = new Panel {
                Location = new Point(16, 57),
                Size = new Size(448, 179),
                BackColor = Skin.Background,
                Visible = i == selected
            };
            Controls.Add(pages[i]);
        }
        BuildClicker();
        BuildAutomation();
        BuildExperimental();
        BuildTheme();
        BuildMisc();
        if (Skin.Scale != 1f)Scale(new SizeF(Skin.Scale, Skin.Scale));
        footerWidth = footer.Width;
        LayoutFooter();
        tabs.Selected = selected;
        tabs.Invalidate();
        UpdateDanger();
        shownNormalTokens = shownEmoteTokens = long.MinValue;
        UpdateText();
        Invalidate(true);
        Diag.Write(LogLevel.Info, "Menu", "rescaled",
            "scale=" + newScale.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
    }

    // Inspection-mode forms (--ui-test/--render) never receive real WM_DPICHANGED; this exercises
    // the same rebuild path directly.
    internal void SimulateDpiChange(float scale) { RebuildUI(scale); }

    internal void SimulateDisplayChange(float dpi, Size screen, Rectangle work) {
        ApplyDisplay(dpi, screen, work, "test");
    }

    // The header to the left of the tabs, inside the frame line, where the wordmark is centered.
    Rectangle LogoBlock {
        get {
            int left = Skin.D(5) + 1, top = Skin.D(17);
            return new Rectangle(left, top, Math.Max(0, tabs.Left - left), Skin.Logo.Height);
        }
    }

    protected override void OnPaint(PaintEventArgs e) {
        base.OnPaint(e);
        var g = e.Graphics;
        int i3 = Skin.D(g, 3), i7 = Skin.D(g, 7), i10 = Skin.D(g, 10), i50 = Skin.D(g, 50), i20 = Skin.D(g, 20),
            i88 = Skin.D(g, 88), i5 = Skin.D(g, 5), i11 = Skin.D(g, 11);
        using (var p = new Pen(Color.Black))g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
        using (var p = new Pen(Skin.Border)) {
            g.DrawRectangle(p, i3, i3, Width - i7, Height - i7);
            g.DrawRectangle(p, i10, i50, Width - i20, Height - i88);
        }
        using (var p = new Pen(Color.FromArgb(39, 39, 44)))
            g.DrawRectangle(p, i5, i5, Width - i11, Height - i11);
        Skin.DrawLogo(g, LogoBlock, Skin.LogoText, Skin.Accent);
    }

    void DragMenu(MouseEventArgs e) {
        if (e.Button == MouseButtons.Left) {
            Native.ReleaseCapture();
            Native.SendMessage(Handle, 0xA1, new IntPtr(2), IntPtr.Zero);
        }
    }

    internal bool InDragZone(Point p) { return p.X < tabs.Left && p.Y < Skin.D(50); }

    protected override void OnMouseDown(MouseEventArgs e) {
        if (InDragZone(e.Location))DragMenu(e);
        base.OnMouseDown(e);
    }

    public void SelectPage(int index) {
        if (index < 0 || index >= 5)return;
        tabs.Selected = index;
        tabs.Invalidate();
        for (int i = 0; i < 5; i++)pages[i].Visible = i == index;
        Invalidate(true);
    }

    FieldGroup Group(int page, string title, int x, int w) {
        var g = new FieldGroup(title, x, 0, w, 148);
        pages[page].Controls.Add(g);
        return g;
    }

    Label Label(Control parent, string text, int x, int y, int w, int h = 18) {
        var l = new Label {
            Text = text,
            Location = new Point(x, y),
            Size = new Size(w, h),
            ForeColor = Skin.Muted,
            Font = Skin.Font,
            BackColor = Color.Transparent
        };
        parent.Controls.Add(l);
        return l;
    }

    MicroButton Button(Control p, string text, int x, int y, int w, Action action) {
        var b = new MicroButton(text, () => {
            try {
                Diag.Write(LogLevel.Debug, "Menu", "action", text);
                action();
            } catch (Exception e) {
                Diag.Fault("Menu", text, e);
                Notice(e.Message);
            }
        }) {Location = new Point(x, y), Size = new Size(w, 24)};
        p.Controls.Add(b);
        return b;
    }

    void Check(Control p, string text, int y, Func<bool> getter, Action<bool> setter) {
        var c = new MicroCheck(text, getter, v => {
            setter(v);
            Sync();
            p.Invalidate(true);
        }) {Location = new Point(14, y), Width = p.Width - 28};
        p.Controls.Add(c);
    }

    void BuildClicker() {
        var click = RateGroup(false);
        var emote = RateGroup(true);
        emote.Visible = false;
        Button(pages[0], "auto clicking", 0, 0, 218, () => {
            click.Visible = true;
            emote.Visible = false;
        });
        Button(pages[0], "auto emoting", 230, 0, 218, () => {
            click.Visible = false;
            emote.Visible = true;
        });
    }

    FieldGroup RateGroup(bool emote) {
        var c = new FieldGroup(emote ? "auto emoting" : "auto clicking", 0, 30, 448, 144);
        pages[0].Controls.Add(c);
        Func<int> rate = () => emote ? settings.EmoteRate : settings.Taps;
        Action<int> set = v => {
            v = rates.Clamp(v, emote);
            if (emote)settings.EmoteRate = v;
            else settings.Taps = v;
            Sync();
        };
        Check(c, "enabled", 24, () => emote ? settings.Emoting : settings.Clicker, v => {
            if (emote)settings.Emoting = v;
            else settings.Clicker = v;
        });
        int normalMaximum = emote ? RatePolicy.NormalEmoteMaximum : RatePolicy.NormalClickMaximum;
        var slider = new MicroSlider(emote ? "emote rate" : "click rate", 1, normalMaximum, 1, "0.0", "",
            () => Math.Min(normalMaximum, rate()), v => {
                set((int)v);
                c.Invalidate(true);
            }) {Location = new Point(12, 57), Width = 420, Display = () => rate().ToString("N0") + " / s"};
        c.Controls.Add(slider);

        var exact = new TextBox {
            Visible = false,
            TextAlign = HorizontalAlignment.Right,
            BackColor = Skin.Panel,
            ForeColor = Skin.Text,
            Font = Skin.Font,
            BorderStyle = BorderStyle.FixedSingle,
            Location = new Point(250, 55),
            Size = new Size(170, 23)
        };
        c.Controls.Add(exact);
        exact.BringToFront();
        bool ending = false;
        Action<bool> finish = commit => {
            if (!exact.Visible || ending)return;
            ending = true;
            int value;
            if (commit &&
                    int.TryParse(exact.Text,
                        System.Globalization.NumberStyles.Integer |
                        System.Globalization.NumberStyles.AllowThousands,
                        System.Globalization.CultureInfo.CurrentCulture, out value))set(value);
            exact.Hide();
            slider.Invalidate();
            ending = false;
        };
        slider.EditValue = () => {
            exact.Text = rate().ToString();
            exact.Show();
            exact.Focus();
            exact.SelectAll();
        };
        exact.KeyDown += (s, e) => {
            if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Escape) {
                finish(e.KeyCode == Keys.Enter);
                e.SuppressKeyPress = true;
                slider.Focus();
            }
        };
        exact.LostFocus += (s, e) => finish(true);
        var button = Button(c, "resume automation", 14, 108, 420, ToggleRunning);
        if (emote)emoteStart = button;
        else start = button;
        return c;
    }

    void BuildAutomation() {
        var gifts = Group(1, "gifts", 0, 218);
        Check(gifts, "auto collect", 24, () => settings.Gifts, v => settings.Gifts = v);
        Check(gifts, "auto claim pawpass", 48, () => settings.PawPass, v => settings.PawPass = v);
        var ex = Group(1, "duplicate exchanges", 230, 218);
        Check(ex, "auto-slot + exchange", 24, () => settings.Exchange, v => settings.Exchange = v);
    }

    long shownNormalTokens = long.MinValue, shownEmoteTokens = long.MinValue;
    Label tokenCounts;
    bool unlockAll;

    void BuildExperimental() {
        var gifts = Group(2, "insta gift", 0, 218);
        Check(gifts, "enabled", 24, () => settings.InstaGift, v => settings.InstaGift = v);
        tokenCounts = Label(gifts, "normal: —\nemote: —", 14, 58, 190, 36);
        var unlock = Group(2, "unlock all", 230, 218);
        Check(unlock, "enabled", 24, () => unlockAll, v => unlockAll = v);
        Label(unlock, "temporarily unlocks cosmetics\nand emotes", 14, 58, 190, 36);
    }

    void BuildMisc() {
        var binds = Group(4, "bindings", 0, 218);
        binds.Height = 100;
        Label(binds, "menu", 14, 29, 87);
        var menu = new BindingEditor(() => settings.MenuKey, () => settings.MenuModifiers,
            (k, m) => SetBinding(true, k, m)) {Location = new Point(105, 24), Width = 99};
        binds.Controls.Add(menu);
        Label(binds, "pause/resume", 14, 69, 87);
        var pause = new BindingEditor(() => settings.PauseKey, () => settings.PauseModifiers,
            (k, m) => SetBinding(false, k, m)) {Location = new Point(105, 64), Width = 99};
        binds.Controls.Add(pause);

        var app = Group(4, "app", 230, 218);
        app.Height = 100;
        Button(app, "reset settings", 14, 24, 190, ResetSettings);
        Button(app, "unload autocat", 14, 64, 190, Unload);
        var danger = new FieldGroup("danger zone", 0, 106, 448, 70);
        pages[4].Controls.Add(danger);
        Check(danger, "allow extreme rates", 23, () => rates.Enabled || rates.Pending, RequestDanger);
        danger.Controls[0].Width = 163;
        dangerWarning = Label(danger, "High rates may reduce FPS and\nunlock achievements immediately.", 182,
            22, 252, 34);
        dangerWarning.ForeColor = Skin.Accent;
        dangerConfirm = Button(danger, "enable", 14, 44, 150, ConfirmDanger);
        UpdateDanger();
    }

    void BuildTheme() {
        var colors = new FieldGroup("accent color", 0, 0, 448, 148);
        pages[3].Controls.Add(colors);
        themeWheel = new ColorWheel {Location = new Point(14, 24)};
        themeWheel.SetColor(Skin.Accent);
        colors.Controls.Add(themeWheel);
        themeHex = new TextBox {
            Location = new Point(150, 24),
            Size = new Size(90, 23),
            BackColor = Skin.Panel,
            ForeColor = Skin.Text,
            Font = Skin.Font,
            BorderStyle = BorderStyle.FixedSingle,
            MaxLength = 7,
            Text = Skin.Hex(Skin.Accent)
        };
        colors.Controls.Add(themeHex);
        themeSwatch = new Panel {
            Location = new Point(248, 24),
            Size = new Size(30, 23),
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Skin.Accent
        };
        colors.Controls.Add(themeSwatch);
        Button(colors, "reset", 284, 24, 80, () => ApplyAccent(Skin.DefaultAccent));
        themeWheel.Changed = ApplyAccent;
        themeHex.KeyDown += (s, e) => {
            if (e.KeyCode == Keys.Enter) {
                Color parsed;
                if (Skin.TryParseHex(themeHex.Text, out parsed))ApplyAccent(parsed);
                else themeHex.Text = Skin.Hex(Skin.Accent);
                e.SuppressKeyPress = true;
                themeWheel.Focus();
            } else if (e.KeyCode == Keys.Escape) {
                themeHex.Text = Skin.Hex(Skin.Accent);
                e.SuppressKeyPress = true;
                themeWheel.Focus();
            }
        };
        themeHex.LostFocus += (s, e) => {
            Color parsed;
            if (Skin.TryParseHex(themeHex.Text, out parsed))ApplyAccent(parsed);
            else themeHex.Text = Skin.Hex(Skin.Accent);
        };
    }

    void ApplyAccent(Color c) {
        Skin.Accent = c;
        settings.AccentColor = (c.R << 16) | (c.G << 8) | c.B;
        if (themeSwatch != null)themeSwatch.BackColor = c;
        if (updateLink != null)updateLink.ForeColor = c;
        if (themeHex != null)themeHex.Text = Skin.Hex(c);
        if (themeWheel != null)themeWheel.SetColor(c);
        Invalidate(true);
        QueueSave();
    }

    // Changes persist on their own shortly after the last edit (as well as on hide/close), so a crash or forced kill
    // loses at most a couple of seconds of changes; dragging a slider still produces a single write.
    readonly System.Windows.Forms.Timer saveTimer = new System.Windows.Forms.Timer {Interval = 2000};

    void QueueSave() {
        if (inspection || closing)return;
        saveTimer.Stop();
        saveTimer.Start();
    }

    void RequestDanger(bool value) {
        Diag.Write(LogLevel.Info, "DangerZone", value ? "extreme.requested" : "extreme.cancelled_or_disabled",
            "Two-action confirmation; pending state not persisted");
        rates.Request(value);
        settings.ExtremeRates = rates.Enabled;
        Sync();
        if (!value)Save();
        UpdateDanger();
        Invalidate(true);
    }

    void ConfirmDanger() {
        Diag.Write(LogLevel.Info, "DangerZone", "extreme.confirmed", "Second deliberate action");
        rates.Confirm();
        settings.ExtremeRates = rates.Enabled;
        Save();
        UpdateDanger();
        Invalidate(true);
        LogConfiguration();
    }

    void UpdateDanger() {
        if (dangerWarning != null) {
            dangerWarning.Text = rates.Pending ?
                "High rates may reduce FPS and\nunlock achievements immediately." :
                "allows custom click and emote rates";
            dangerWarning.ForeColor = rates.Pending ? Skin.Accent : Skin.Muted;
        }
        if (dangerConfirm != null)dangerConfirm.Visible = rates.Pending;
    }

    void SetBinding(bool menu, int key, int mods) {
        Diag.Values(LogLevel.Info, "Bindings", "requested", "menu={0} keyCode={1} modifiers={2}", menu ? 1 : 0,
            key, mods);
        if (key != 0 && (menu ? settings.PauseKey : settings.MenuKey) == key &&
                (menu ? settings.PauseModifiers : settings.MenuModifiers) == mods) {
            Diag.Write(LogLevel.Warn, "Bindings", "rejected", "Menu and pause bindings conflict");
            Notice("Choose a different binding for each action");
            ApplyBindings();
            return;
        }
        if (menu) {
            settings.MenuKey = key;
            settings.MenuModifiers = mods;
        } else {
            settings.PauseKey = key;
            settings.PauseModifiers = mods;
        }
        Diag.Write(LogLevel.Info, "Bindings", "applied", menu ? "menu" : "pause/resume");
        ApplyBindings();
        UpdateText();
        QueueSave();
    }

    void ApplyBindings() {
        if (bindings != null && !bindings.Configure(settings.MenuKey, settings.PauseKey))
            Notice("Cannot watch the mouse wheel; choose another binding");
    }

    public static bool Matches(int assigned, int modifiers, int key, int observed) {
        return assigned != 0 && assigned == key && modifiers == observed;
    }

    void OnBinding(int key, int mods, Point point) {
        if (closing || BindingEditor.Capturing)return;
        bool isMouse = key < 7 || key >= 256;
        if (isMouse && Visible && Bounds.Contains(point))return;
        bool menu = Matches(settings.MenuKey, settings.MenuModifiers, key, mods),
            pause = Matches(settings.PauseKey, settings.PauseModifiers, key, mods);
        if (!menu && !pause)return;
        BeginInvoke(new Action(() => {
            if (closing || BindingEditor.Capturing)return;
            if (menu)ToggleMenu();
            else ToggleRunning();
        }));
    }

    void ToggleMenu(bool fromTray = false) {
        if (closing)return;
        if (Visible)HideMenu();
        else ShowMenu(fromTray);
    }

    // The same close as the unload button: the menu closes, which stops automation and unloads the runtime.
    void Unload() { Close(); }

    void ToggleRunning() {
        running = !running;
        Diag.Write(LogLevel.Info, "Automation", running ? "resume.requested" : "pause.requested",
            "Insta Gift and Unlock All stay as toggled");
        Sync();
        UpdateText();
    }

    void Sync() {
        settings.Taps = rates.Clamp(settings.Taps, false);
        settings.EmoteRate = rates.Clamp(settings.EmoteRate, true);
        taps.Rate = settings.Taps;
        taps.Enabled = running && settings.Clicker;
        Apply();
        LogConfiguration();
        QueueSave();
    }

    // Runs at launch and on every change, so what the runtime is sent always follows the checkboxes and the
    // pause state. Paused means nothing new is clicked, emoted, claimed or exchanged; only Insta Gift, which
    // submits nothing itself, stays armed. Requests already in flight finish in the game component.
    void Apply() {
        bridge.UnlockAll = unlockAll;
        bridge.InstaGift = settings.InstaGift;
        bridge.Emoting = running && settings.Emoting;
        bridge.EmoteRate = settings.EmoteRate;
        bridge.Gifts = running && settings.Gifts;
        bridge.PawPass = running && settings.PawPass;
        bridge.Exchange = running && settings.Exchange;
        bridge.Running = running;
    }

    void Tick() {
        if (reopen != null && reopen.WaitOne(0))ShowMenu();
        if (Visible)UpdateText();
        else UpdateTray();
    }

    void UpdateText() {
        var s = bridge == null ? new GameState() : bridge.State;
        string connection = bridge == null ? "waiting for game" : bridge.Connection;
        if (tokenCounts != null && pages[2].Visible &&
                (s.NormalTokens != shownNormalTokens || s.EmoteTokens != shownEmoteTokens)) {
            shownNormalTokens = s.NormalTokens;
            shownEmoteTokens = s.EmoteTokens;
            string counts = "normal: " + (s.NormalTokens < 0 ? "—" : s.NormalTokens.ToString("N0")) +
                "\nemote: " + (s.EmoteTokens < 0 ? "—" : s.EmoteTokens.ToString("N0"));
            if (tokenCounts.Text != counts)tokenCounts.Text = counts;
        }
        string buttonText = running ? "pause automation" : "resume automation";
        if (start.Text != buttonText) {
            start.Text = buttonText;
            start.Invalidate();
        }
        if (emoteStart != null && emoteStart.Text != buttonText) {
            emoteStart.Text = buttonText;
            emoteStart.Invalidate();
        }
        bool emoteThrottled = running && settings.Emoting && s.EmoteBudget > 0 &&
            s.EmoteBudget < settings.EmoteRate - 0.01;
        string footerText = connection + " · " + (running ? "running" : "paused") +
            (DateTime.UtcNow < noticeUntil ? " · " + notice : "") +
            (!String.IsNullOrEmpty(s.Message) && s.Message != connection && s.Message != notice ? " · " +
                s.Message : "") + (!String.IsNullOrEmpty(s.PawPass) ? " · " + s.PawPass : "") +
            (emoteThrottled ? " · emote throttled: " + s.EmoteBudget.ToString("0.0") + "/s (avg " +
                s.FrameMs.ToString("0.0") + "ms, peak " + s.PeakMs.ToString("0.0") + "ms)" : "");
        if (footer.Text != footerText) {
            footer.Text = footerText;
            LayoutFooter();
        }
        UpdateTray();
    }

    // The update notice takes the right end of the footer line and shows only while the status text still fits
    // to its left, so it never shortens or hides what the footer reports.
    void LayoutFooter() {
        if (footerWidth <= 0)return;
        footer.Width = footerWidth;
        if (updateLink == null)return;
        int last = 0;
        foreach (Control c in Controls)
            if (c != updateLink && c.TabIndex > last)last = c.TabIndex;
        updateLink.TabIndex = last + 1;
        int width = updateLink.GetPreferredSize(Size.Empty).Width + 1;
        int left = footer.Right - width;
        int room = left - Skin.D(12) - footer.Left;
        bool fits = footer.GetPreferredSize(Size.Empty).Width <= room;
        updateLink.SetBounds(left, footer.Top, width, footer.Height);
        updateLink.Visible = fits;
        if (fits)footer.Width = room;
    }

    // One check per launch, off the UI thread: the menu, attachment and shutdown never wait for it.
    internal void BeginUpdateCheck(Version running = null) {
        if (updateStarted || closing || (inspection && UpdateCheck.Fetch == UpdateCheck.Network))return;
        updateStarted = true;
        updateThread = UpdateCheck.Start(UpdateCheck.Fetch,
            running ?? System.Reflection.Assembly.GetExecutingAssembly().GetName().Version, (outcome, latest) => {
                try {
                    BeginInvoke(new Action(() => UpdateChecked(outcome, latest)));
                } catch (InvalidOperationException) {
                } catch (Exception) {
                    Diag.Write(LogLevel.Info, "Update", "check", UpdateCheck.Describe(UpdateCheck.Outcome.Unreachable, null));
                }
            });
    }

    void UpdateChecked(UpdateCheck.Outcome outcome, Version latest) {
        if (closing || IsDisposed)return;
        Diag.Write(LogLevel.Info, "Update", "check", UpdateCheck.Describe(outcome, latest));
        if (outcome == UpdateCheck.Outcome.Newer)ShowUpdate(latest);
    }

    void ShowUpdate(Version latest) {
        if (updateLink == null) {
            updateLink = new NoticeLink(OpenReleasePage);
            Controls.Add(updateLink);
            updateLink.ContextMenuStrip = footer.ContextMenuStrip;
        }
        updateLink.Text = "update available: " + latest.ToString(3);
        LayoutFooter();
    }

    // Always the fixed release page; the reply decides only whether the notice appears and which version it names.
    void OpenReleasePage() {
        try {
            UpdateCheck.Open(UpdateCheck.ReleasePage);
        } catch (Exception e) {
            Diag.Fault("Update", "open.failed", e);
            Notice("Could not open the release page");
        }
    }

    void Notice(string message) {
        notice = message;
        noticeUntil = DateTime.UtcNow.AddSeconds(6);
        if (start != null)UpdateText();
    }

    void ResetSettings() {
        Diag.Write(LogLevel.Info, "Settings", "reset.requested",
            "Replacing saved preferences with defaults; lifetime/achievements untouched");
        rates.Request(false);
        UpdateDanger();
        running = false;
        unlockAll = false;
        settings = new Settings();
        ApplyBindings();
        ApplyAccent(Skin.DefaultAccent);
        Sync();
        Save();
        UpdateText();
        Invalidate(true);
        Notice("settings reset");
        Diag.Write(LogLevel.Info, "Settings", "reset.applied", "features off; rates=10; automation paused");
    }

    void Save() {
        settings.ExtremeRates = rates.Enabled;
        if (!inspection)settings.Save(settingsPath);
    }

    void HideMenu(bool giveFocusBack = true) {
        Save();
        Hide();
        Diag.Write(LogLevel.Info, "Menu", "hidden", "Automation state unchanged");
        if (giveFocusBack && Native.IsWindow(previous))Native.SetForegroundWindow(previous);
    }

    // From the notification area the window in front is the taskbar, which is no window to give focus back to.
    void ShowMenu(bool fromTray = false) {
        UpdateText();
        var front = Native.GetForegroundWindow();
        previous = fromTray ? IntPtr.Zero : front;
        var area = (fromTray ? Screen.FromPoint(Cursor.Position) : Screen.FromHandle(front)).WorkingArea;
        if (IsHandleCreated && (Native.IsIconic(Handle) || Native.IsZoomed(Handle)))
            Native.ShowWindow(Handle, Native.SW_RESTORE);
        if (!area.IntersectsWith(Bounds))
            Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 2);
        Show();
        Activate();
        setForeground(Handle);
        Diag.Write(LogLevel.Info, "Menu", "shown", "Automation state unchanged");
    }

    void Shutdown() {
        if (closing)return;
        try {
            Diag.Write(LogLevel.Info, "Session", "shutdown.requested", "Stopping automation, bindings and bridge");
            closing = true;
            running = false;
            Apply();
            timer.Stop();
            inputTimer.Stop();
            displaySettle.Stop();
            saveTimer.Stop();
            UpdateCheck.Cancel();
            taps.Enabled = false;
            if (bindings != null)bindings.Dispose();
            if (bridge != null)bridge.Dispose();
            taps.Dispose();
            try {
                Save();
            } catch (Exception e) {
                Diag.Fault("Settings", "shutdown.save.failed", e);
            }
            if (reopen != null)reopen.Dispose();
        } finally {
            DisposeTray();
        }
    }

    void CopyDiagnostic(string text) {
        try {
            Clipboard.SetText(text);
            Notice("copied");
        } catch (Exception e) {
            Diag.Fault("Diagnostics", "clipboard.failed", e);
            Notice("Could not copy diagnostics");
        }
    }

    int lastFlags = -1, lastTaps = -1, lastEmoteRate = -1;

    void LogConfiguration() {
        if (inspection)return;
        int flags = (settings.Clicker ? 1 : 0) | (settings.Emoting ? 2 : 0) | (settings.Gifts ? 4 : 0) |
            (settings.Exchange ? 8 : 0) | (settings.InstaGift ? 16 : 0) | (unlockAll ? 32 : 0) |
            (settings.ExtremeRates ? 64 : 0) | (running ? 128 : 0) | (settings.PawPass ? 256 : 0);
        if (flags != lastFlags) {
            string[] names = {
                "Clicks",
                "Emotes",
                "AutoCollect",
                "Exchange",
                "InstaGift",
                "UnlockAll",
                "ExtremeRates",
                "Automation",
                "PawPass"
            };
            for (int i = 0; i < names.Length; i++)
                if (lastFlags < 0 || ((lastFlags ^ flags) & (1 << i)) != 0)
                    Diag.Write(LogLevel.Info, names[i],
                        (flags & (1 << i)) != 0 ? "configured.on" : "configured.off",
                        "source=menu; game application recorded separately");
            lastFlags = flags;
        }
        if (lastTaps != settings.Taps || lastEmoteRate != settings.EmoteRate) {
            Diag.Values(LogLevel.Info, "Rates", "validated",
                "clickBefore={0} clickAfter={1} emoteBefore={2} emoteAfter={3}", lastTaps, settings.Taps,
                lastEmoteRate, settings.EmoteRate);
            lastTaps = settings.Taps;
            lastEmoteRate = settings.EmoteRate;
        }
    }

    public void Render(string directory) {
        Directory.CreateDirectory(directory);
        Opacity = 0;
        Show();
        for (int i = 0; i < 5; i++) {
            SelectPage(i);
            Application.DoEvents();
            using (var b = new Bitmap(Width, Height)) {
                DrawToBitmap(b, ClientRectangle);
                b.Save(Path.Combine(directory, new[] {
                    "auto-clicker.png",
                    "automation.png",
                    "experimental.png",
                    "theme.png",
                    "misc.png"
                } [i]));
            }
        }
        SelectPage(4);
        RequestDanger(true);
        Application.DoEvents();
        using (var b = new Bitmap(Width, Height)) {
            DrawToBitmap(b, ClientRectangle);
            b.Save(Path.Combine(directory, "misc-confirmation.png"));
        }
        ConfirmDanger();
        Application.DoEvents();
        using (var b = new Bitmap(Width, Height)) {
            DrawToBitmap(b, ClientRectangle);
            b.Save(Path.Combine(directory, "misc-enabled.png"));
        }
        SelectPage(3);
        SimulateDpiChange(1.5f);
        Application.DoEvents();
        using (var b = new Bitmap(Width, Height)) {
            DrawToBitmap(b, ClientRectangle);
            b.Save(Path.Combine(directory, "theme-live-rescale-150.png"));
        }
        Hide();
    }

    static List<BindingEditor> FindEditors(Control parent, List<BindingEditor> found = null) {
        found = found ?? new List<BindingEditor>();
        foreach (Control c in parent.Controls) {
            var editor = c as BindingEditor;
            if (editor != null)found.Add(editor);
            FindEditors(c, found);
        }
        return found;
    }

    static readonly float[] LogoScales = {1f, 1.25f, 4f / 3f, 1.5f, 1.75f, 2f, 2.25f, 2.5f, 3f};

    // The first and last pixel columns that differ from the background, or false when there are none.
    static bool Ink(Bitmap b, Rectangle area, Color background, out int first, out int last) {
        first = last = -1;
        for (int x = area.Left; x < area.Right; x++)
            for (int y = area.Top; y < area.Bottom; y++)
                if (b.GetPixel(x, y).ToArgb() != background.ToArgb()) {
                    if (first < 0)first = x;
                    last = x;
                    break;
                }
        return first >= 0;
    }

    static int InkTop(Bitmap b, Rectangle area, Color background) {
        for (int y = area.Top; y < area.Bottom; y++)
            for (int x = area.Left; x < area.Right; x++)
                if (b.GetPixel(x, y).ToArgb() != background.ToArgb())return y;
        return -1;
    }

    // The wordmark is two runs of text; at every scale it must come out pixel for pixel as one run would, in the
    // middle of its block, with its ink inside the block.
    static void VerifyLogo() {
        float before = Skin.Scale;
        try {
            foreach (float scale in LogoScales) {
                Skin.InitializeScale(scale);
                Color text = Skin.LogoText, accent = Color.FromArgb(224, 161, 137);
                if (text != Color.FromArgb(0xEE, 0xEB, 0xE8))throw new Exception("Logo: auto is #EEEBE8");
                if (Skin.Logo.Name != "Consolas" || !Skin.Logo.Bold || Skin.Logo.Unit != GraphicsUnit.Pixel ||
                        Math.Abs(Skin.Logo.Size - 18 * scale) > 0.01f)
                    throw new Exception("Logo: the font changed at scale " + scale);
                foreach (int extra in new[] {0, 1}) {
                    var block = new Rectangle(Skin.D(6), Skin.D(17), Skin.D(152) + extra, Skin.Logo.Height);
                    using (var whole = new Bitmap(Skin.D(170), Skin.D(50)))
                        using (var split = new Bitmap(Skin.D(170), Skin.D(50)))
                            using (var colored = new Bitmap(Skin.D(170), Skin.D(50))) {
                                Action<Bitmap, Action<Graphics>> draw = (b, paint) => {
                                    using (var g = Graphics.FromImage(b)) {
                                        g.Clear(Color.White);
                                        paint(g);
                                    }
                                };
                                int word;
                                using (var g = Graphics.FromImage(whole))
                                    word = TextRenderer.MeasureText(g, "autocat", Skin.Logo, Size.Empty,
                                        TextFormatFlags.NoPadding).Width;
                                var at = new Point(block.Left + (block.Width - word) / 2, block.Top);
                                draw(whole, g => TextRenderer.DrawText(g, "autocat", Skin.Logo, at, Color.Black,
                                    TextFormatFlags.NoPadding));
                                draw(split, g => Skin.DrawLogo(g, block, Color.Black, Color.Black));
                                draw(colored, g => Skin.DrawLogo(g, block, text, accent));
                                bool sawText = false, sawAccent = false;
                                for (int y = 0; y < whole.Height; y++)
                                    for (int x = 0; x < whole.Width; x++) {
                                        if (whole.GetPixel(x, y) != split.GetPixel(x, y))
                                            throw new Exception("Logo: not one word in the middle of its block at scale " +
                                                scale + " (x=" + x + ", y=" + y + ")");
                                        var c = colored.GetPixel(x, y);
                                        if (c == text)sawText = true;
                                        if (c == accent)sawAccent = true;
                                    }
                                if (!sawText || !sawAccent)throw new Exception("Logo: both colors must appear at " + scale);
                                int first, last;
                                if (!Ink(split, new Rectangle(0, 0, split.Width, split.Height), Color.White, out first,
                                        out last))throw new Exception("Logo: nothing drawn at " + scale);
                                double middle = (first + last + 1) / 2.0, wanted = block.Left + block.Width / 2.0;
                                if (first < block.Left || last >= block.Right || Math.Abs(middle - wanted) > 1)
                                    throw new Exception("Logo: ink " + first + ".." + last + " in block " + block.Left +
                                        ".." + block.Right + " at scale " + scale);
                            }
                }
            }
        } finally {
            Skin.InitializeScale(before);
        }
    }

    // The wordmark as the menu paints it: in the middle of the space left of the tabs at every scale, kept there
    // by a live rescale, in the logo colors, clear of the frame.
    static void VerifyLogoInMenu() {
        float before = Skin.Scale;
        try {
            using (var form = new MainForm(true)) {
                form.Opacity = 0;
                form.Show();
                Application.DoEvents();
                Func<float, string> check = scale => {
                    int left = Skin.D(5) + 1, right = form.tabs.Left;
                    var area = Rectangle.FromLTRB(left + 1, Skin.D(5) + 2, right - 1, Skin.D(50) - 2);
                    bool sawText = false, sawAccent = false;
                    int top;
                    using (var alone = new Bitmap(Skin.D(170), Skin.D(50)))
                        using (var g = Graphics.FromImage(alone)) {
                            g.Clear(Color.White);
                            Skin.DrawLogo(g, new Rectangle(Skin.D(6), 0, Skin.D(152), Skin.Logo.Height), Color.Black,
                                Color.Black);
                            top = InkTop(alone, new Rectangle(0, 0, alone.Width, alone.Height), Color.White);
                        }
                    using (var shot = Shot(form)) {
                        int first, last;
                        if (!Ink(shot, area, Skin.Background, out first, out last))return "no wordmark";
                        if (InkTop(shot, area, Skin.Background) != Skin.D(17) + top)return "moved vertically";
                        double middle = (first + last + 1) / 2.0, wanted = (left + right) / 2.0;
                        if (first <= left + 1 || last >= right - 2 || Math.Abs(middle - wanted) > 1)
                            return "ink " + first + ".." + last + " in " + left + ".." + right;
                        for (int x = area.Left; x < area.Right; x++)
                            for (int y = area.Top; y < area.Bottom; y++) {
                                var c = shot.GetPixel(x, y);
                                if (c == Skin.LogoText)sawText = true;
                                if (c == Skin.Accent)sawAccent = true;
                            }
                    }
                    return sawText && sawAccent ? null : "auto is not #EEEBE8 or cat is not the accent";
                };
                foreach (float scale in LogoScales) {
                    form.SimulateDpiChange(scale);
                    Application.DoEvents();
                    string problem = check(scale);
                    if (problem != null)throw new Exception("Logo in the menu at scale " + scale + ": " + problem);
                }
                form.SimulateDpiChange(1f);
                form.SimulateDpiChange(2.25f);
                Application.DoEvents();
                string again = check(2.25f);
                if (again != null)throw new Exception("Logo after a live rescale: " + again);
            }
        } finally {
            Skin.InitializeScale(before);
        }
    }
    static int Pixels(IntPtr icon, int size) {
        if (icon == IntPtr.Zero)return 0;
        using (var image = Icon.FromHandle(icon))
            using (var b = image.ToBitmap()) {
                if (b.Width != size)return 1;
                int sum = 17;
                for (int y = 0; y < b.Height; y++)
                    for (int x = 0; x < b.Width; x++)sum = unchecked(sum * 31 + b.GetPixel(x, y).ToArgb());
                return (sum & 0x3FFFFFFF) + 2;
            }
    }

    // What Windows needs to give the menu a taskbar button, and that the other forms get none. The window is
    // created but never shown, so a test run adds nothing to the taskbar.
    static void VerifyTaskbar() {
        float scaleBefore = Skin.Scale;
        try {
            using (var inspected = new MainForm(true)) {
                int ex = Native.GetWindowLong(inspected.Handle, Native.GWL_EXSTYLE);
                if (inspected.ShowInTaskbar || (ex & Native.WS_EX_TOOLWINDOW) == 0 || (ex & Native.WS_EX_APPWINDOW) != 0)
                    throw new Exception("Taskbar: an inspection form must stay a tool window with no button");
            }
            using (var form = new MainForm(true)) {
                form.UseTaskbar(true);
                var h = form.Handle;
                Func<int, int> style = index => Native.GetWindowLong(h, index);
                Action<string> window = step => {
                    int ex = style(Native.GWL_EXSTYLE), plain = style(Native.GWL_STYLE);
                    if ((ex & Native.WS_EX_APPWINDOW) == 0 || (ex & Native.WS_EX_TOOLWINDOW) != 0 ||
                            Native.GetWindow(h, Native.GW_OWNER) != IntPtr.Zero)
                        throw new Exception("Taskbar, " + step + ": the menu is not an unowned app window");
                    if ((ex & Native.WS_EX_TOPMOST) == 0 || !form.TopMost)
                        throw new Exception("Taskbar, " + step + ": the menu is no longer always on top");
                    if ((plain & Native.WS_CAPTION) != 0 || (plain & Native.WS_MINIMIZEBOX) == 0 ||
                            (plain & Native.WS_MAXIMIZEBOX) != 0 ||
                            form.FormBorderStyle != FormBorderStyle.None)
                        throw new Exception("Taskbar, " + step + ": the menu must stay borderless and minimizable");
                    if (form.Text != Title)throw new Exception("Taskbar, " + step + ": button title");
                    if (form.Visible)throw new Exception("Taskbar, " + step + ": the check must not show the window");
                    foreach (int big in new[] {0, 1}) {
                        int size = big == 1 ? AppIcon.LargeSize : AppIcon.SmallSize;
                        int shown = Pixels(Native.SendMessage(h, 0x7F, new IntPtr(big), IntPtr.Zero), size);
                        using (var expected = AppIcon.Load(size))
                            if (shown <= 1 || expected == null || shown != Pixels(expected.Handle, size))
                                throw new Exception("Taskbar, " + step + ": the window does not carry the application icon at " + size);
                    }
                };
                window("created");
                Skin.InitializeScale(scaleBefore);
                form.SimulateDpiChange(scaleBefore * 2);
                if (form.Handle != h)throw new Exception("Taskbar: a live rescale recreated the window");
                window("rescaled");
                form.SimulateDisplayChange(1f, new Size(1920, 1080), new Rectangle(0, 0, 1920, 1040));
                if (form.Handle != h)throw new Exception("Taskbar: a display change recreated the window");
                window("display change");
            }
        } finally {
            Skin.InitializeScale(scaleBefore);
        }
    }

    static TrayFactory Made(List<FakeTray> made) {
        return (toggle, unload) => {
            var tray = new FakeTray(toggle, unload);
            made.Add(tray);
            return tray;
        };
    }

    // Every way the menu is shown and hidden ends in the same place, with the menu in front and still on top, and
    // the notification area menu names what it does next.
    static void VerifyMenuRoutes() {
        var input = new FakeInput();
        var trays = new List<FakeTray>();
        BindingEditor.HeldModifiers = () => Keys.None;
        try {
            using (var form = new MainForm(true, new Settings(), input, Made(trays))) {
                var raised = new List<IntPtr>();
                form.setForeground = h => {
                    raised.Add(h);
                    return true;
                };
                form.Opacity = 0;
                form.Show();
                form.SelectPage(4);
                Application.DoEvents();
                if (trays.Count != 1)throw new Exception("Menu routes: one notification area icon expected");
                var tray = trays[0];
                Action<string, bool, int> expect = (step, visible, fronted) => {
                    if (form.Visible != visible || raised.Count != fronted)
                        throw new Exception("Menu routes, " + step + ": visible=" + form.Visible + " raised=" +
                            raised.Count);
                    if (form.WindowState != FormWindowState.Normal || !form.TopMost ||
                            (Native.GetWindowLong(form.Handle, Native.GWL_EXSTYLE) & Native.WS_EX_TOPMOST) == 0)
                        throw new Exception("Menu routes, " + step + ": the menu must stay on top");
                    if (raised.Count > 0 && raised[raised.Count - 1] != form.Handle)
                        throw new Exception("Menu routes, " + step + ": brought another window forward");
                    if (tray.ToggleText != (visible ? "hide menu" : "show menu"))
                        throw new Exception("Menu routes, " + step + ": the tray menu offers '" + tray.ToggleText + "'");
                };
                Action<int> tap = key => {
                    input.Down.Add(key);
                    form.bindings.Poll();
                    input.Down.Remove(key);
                    form.bindings.Poll();
                    Application.DoEvents();
                };
                Action<int> taskbar = command => {
                    Native.SendMessage(form.Handle, 0x112, new IntPtr(command), IntPtr.Zero);
                    Application.DoEvents();
                };
                expect("start", true, 0);
                tap(45);
                expect("binding hides", false, 0);
                tap(45);
                expect("binding shows", true, 1);
                taskbar(0xF020);
                expect("taskbar button hides", false, 1);
                tray.Click();
                expect("tray click shows", true, 2);
                if (form.previous != IntPtr.Zero)
                    throw new Exception("Menu routes: the taskbar is not a window to give focus back to");
                tray.Click();
                expect("tray click hides", false, 2);
                tray.ChooseToggle();
                expect("tray menu shows", true, 3);
                tray.ChooseToggle();
                expect("tray menu hides", false, 3);
                form.ShowMenu();
                expect("show request", true, 4);
                form.ShowMenu();
                expect("show request while visible", true, 5);
                taskbar(0xF022);
                expect("taskbar button, other minimize forms", false, 5);
                form.ShowMenu();
                taskbar(0xF120);
                expect("a restore request is left alone", true, 6);
                var editors = FindEditors(form.pages[4]);
                editors[1].Edit();
                if (!BindingEditor.Capturing)throw new Exception("Menu routes: capture did not start");
                taskbar(0xF020);
                expect("taskbar button while choosing a binding", false, 6);
                Native.SendMessage(editors[1].Handle, 0x0008, IntPtr.Zero, IntPtr.Zero);
                if (BindingEditor.Capturing)throw new Exception("Menu routes: losing focus does not end a binding choice");
                form.ShowMenu();
                form.SimulateDpiChange(Skin.Scale * 2);
                expect("rescaled", true, 7);
                if (trays.Count != 1 || tray.Disposed != 0 || !ReferenceEquals(tray.MenuFont, Skin.Font))
                    throw new Exception("Menu routes: a rescale must keep the one icon and size its menu");
                form.SimulateDpiChange(Skin.Scale / 2);
                int refreshed = tray.DisplayChanges;
                form.SimulateDisplayChange(1f, new Size(1920, 1080), new Rectangle(0, 0, 1920, 1040));
                if (tray.DisplayChanges != refreshed + 1)
                    throw new Exception("Menu routes: a display change must refresh the icon size");
                tap(45);
                expect("hidden after a rescale", false, 7);
                tap(45);
                expect("shown after a rescale", true, 8);
            }
            if (trays.Count != 1)throw new Exception("Menu routes: one notification area icon expected");
        } finally {
            BindingEditor.HeldModifiers = () => ModifierKeys;
        }
    }

    // The shell minimizes windows by calling ShowWindow, not by the system command the taskbar button sends. Any
    // such minimize is a hide, and the menu comes back normal, topmost and at its place; it can never be maximized.
    static void VerifyMinimize() {
        var input = new FakeInput();
        var trays = new List<FakeTray>();
        BindingEditor.HeldModifiers = () => Keys.None;
        try {
            using (var form = new MainForm(true, new Settings(), input, Made(trays))) {
                form.setForeground = h => true;
                form.Opacity = 0;
                form.Show();
                Application.DoEvents();
                var tray = trays[0];
                var handle = form.Handle;
                var before = form.Bounds;
                Action<string, bool> expect = (step, visible) => {
                    int ex = Native.GetWindowLong(handle, Native.GWL_EXSTYLE);
                    if (form.Visible != visible || Native.IsIconic(handle) || Native.IsZoomed(handle) ||
                            form.WindowState != FormWindowState.Normal)
                        throw new Exception("Minimize, " + step + ": visible=" + form.Visible + " iconic=" +
                            Native.IsIconic(handle) + " zoomed=" + Native.IsZoomed(handle));
                    if (!form.TopMost || (ex & Native.WS_EX_TOPMOST) == 0)
                        throw new Exception("Minimize, " + step + ": the menu must stay on top");
                    if (tray.ToggleText != (visible ? "hide menu" : "show menu"))
                        throw new Exception("Minimize, " + step + ": the tray menu offers '" + tray.ToggleText + "'");
                    // Hidden, the menu keeps the place it had; shown, it is there unless that place is off screen.
                    var now = form.Bounds;
                    bool onScreen = Screen.FromHandle(Native.GetForegroundWindow()).WorkingArea.IntersectsWith(before);
                    if (now.Size != before.Size || (onScreen && now.Location != before.Location))
                        throw new Exception("Minimize, " + step + ": the menu is at " + now + ", not " + before);
                };
                Action<string, int> shell = (step, command) => {
                    Native.ShowWindow(handle, command);
                    Application.DoEvents();
                    expect(step, false);
                };
                Action<int> tap = key => {
                    input.Down.Add(key);
                    form.bindings.Poll();
                    input.Down.Remove(key);
                    form.bindings.Poll();
                    Application.DoEvents();
                };
                expect("start", true);
                shell("minimized by the shell", Native.SW_MINIMIZE);
                tap(45);
                expect("binding after a shell minimize", true);
                shell("minimized without activation", Native.SW_SHOWMINNOACTIVE);
                tray.Click();
                expect("tray click after a shell minimize", true);
                tray.Click();
                expect("tray hides", false);
                tap(45);
                expect("binding shows", true);
                shell("minimized again", Native.SW_MINIMIZE);
                Native.SendMessage(handle, 0x112, new IntPtr(0xF020), IntPtr.Zero);
                form.ShowMenu();
                expect("show request after a shell minimize", true);
                Native.SendMessage(handle, 0x112, new IntPtr(0xF020), IntPtr.Zero);
                expect("taskbar button after the cycles", false);
                tap(45);
                expect("binding after the cycles", true);
                // A menu left iconic by some other route still shows normally.
                Native.ShowWindow(handle, Native.SW_MINIMIZE);
                form.Hide();
                if (!Native.IsIconic(handle))throw new Exception("Minimize: the check needs an iconic window");
                form.ShowMenu();
                expect("show request on an iconic window", true);
                Application.DoEvents();
                expect("queued settle after a direct show", true);
                // Never maximized, by the system command or by the shell.
                if ((Native.GetWindowLong(handle, Native.GWL_STYLE) & Native.WS_MAXIMIZEBOX) != 0)
                    throw new Exception("Minimize: the menu has a maximize box");
                Native.SendMessage(handle, 0x112, new IntPtr(0xF030), IntPtr.Zero);
                expect("maximize command", true);
                Native.ShowWindow(handle, Native.SW_MAXIMIZE);
                Application.DoEvents();
                expect("maximized by the shell", true);
                // Closing leaves the window alone and keeps the icon until the form is gone.
                form.Shutdown();
                Native.SendMessage(handle, 0x112, new IntPtr(0xF020), IntPtr.Zero);
                if (!form.Visible)throw new Exception("Minimize: the taskbar command hid a closing menu");
                Native.ShowWindow(handle, Native.SW_MINIMIZE);
                Application.DoEvents();
                if (!form.Visible || !Native.IsIconic(handle))
                    throw new Exception("Minimize: a closing menu must be left alone");
                Native.ShowWindow(handle, Native.SW_RESTORE);
            }
            // A tray that cannot be created does not stop the menu.
            using (var form = new MainForm(true, new Settings(), null, (toggle, unload) => {
                throw new InvalidOperationException("no tray");
            })) {
                form.Opacity = 0;
                form.Show();
                form.Hide();
                form.ToggleMenu(true);
                if (!form.Visible)throw new Exception("Tray: without an icon the menu must still show");
            }
        } finally {
            BindingEditor.HeldModifiers = () => ModifierKeys;
        }
    }
    // The notification area icon: its menu, its tooltip and its removal on every way out.
    static void VerifyTray() {
        if (NotifyTray.Created != 0)throw new Exception("Tray: a test mode created a notification area icon");
        TrayFactory own = (a, b) => null;
        if (MainForm.TrayFor(true, null) != null || MainForm.TrayFor(false, null) == null ||
                MainForm.TrayFor(true, own) != own || MainForm.TrayFor(false, own) != own)
            throw new Exception("Tray: only a launch gets the real icon, and a test's own factory is always used");
        int toggles = 0, unloads = 0;
        ToolStripItem toggle;
        using (var menu = NotifyTray.Build(() => toggles++, () => unloads++, out toggle)) {
            if (menu.Items.Count != 2 || !ReferenceEquals(toggle, menu.Items[0]) || menu.Items[0].Text != "show menu" ||
                    menu.Items[1].Text != "unload")
                throw new Exception("Tray: the menu offers show or hide, and unload");
            if (!(menu.Renderer is DarkMenuRenderer) || menu.ShowImageMargin || menu.BackColor != Skin.Panel ||
                    menu.ForeColor != Skin.Text || !ReferenceEquals(menu.Font, Skin.Font))
                throw new Exception("Tray: the menu does not match the footer menu");
            menu.Items[0].PerformClick();
            if (toggles != 1 || unloads != 0)throw new Exception("Tray: the first item must show or hide");
            menu.Items[1].PerformClick();
            if (toggles != 1 || unloads != 1)throw new Exception("Tray: the second item must unload");
        }

        // The text is the footer's connection and pause state, nothing else, and always fits the tooltip.
        foreach (string state in new[] {
            "waiting for game",
            "attaching",
            "attached",
            "waiting for component",
            "waiting for game initialization"
        })
            foreach (bool running in new[] {false, true}) {
                string text = TrayText.Tooltip(state, running);
                if (text != "autocat\n" + state + " · " + (running ? "running" : "paused") ||
                        text.Length > TrayText.Limit)
                    throw new Exception("Tray: tooltip for '" + state + "' is '" + text + "'");
            }
        foreach (string odd in new[] {
            null,
            "",
            @"D:\private\secret.log could not be read",
            new string('x', 300)
        }) {
            string text = TrayText.Tooltip(odd, true);
            if (text != "autocat\nconnection problem · running" || text.Length > TrayText.Limit)
                throw new Exception("Tray: tooltip for a failure is '" + text + "'");
        }
        if (TrayText.Toggle(true) != "hide menu" || TrayText.Toggle(false) != "show menu")
            throw new Exception("Tray: toggle text");

        var trays = new List<FakeTray>();
        using (var form = new MainForm(true, new Settings(), null, Made(trays))) {
            var tray = trays[0];
            form.Opacity = 0;
            form.Show();
            Application.DoEvents();
            if (tray.ToolTip != "autocat\nwaiting for game · paused")
                throw new Exception("Tray: first tooltip is '" + tray.ToolTip + "'");
            form.bridge.Connection = "attached";
            form.UpdateText();
            if (tray.ToolTip != "autocat\nattached · paused")throw new Exception("Tray: tooltip '" + tray.ToolTip + "'");
            form.ToggleRunning();
            if (tray.ToolTip != "autocat\nattached · running")throw new Exception("Tray: tooltip '" + tray.ToolTip + "'");
            form.Hide();
            form.bridge.Connection = "waiting for component";
            form.Tick();
            if (tray.ToolTip != "autocat\nwaiting for component · running")
                throw new Exception("Tray: a hidden menu must keep the tooltip current, got '" + tray.ToolTip + "'");
            form.bridge.Connection = @"D:\private\secret.log could not be read";
            form.Tick();
            if (tray.ToolTip.Contains("private") || tray.ToolTip.Length > TrayText.Limit)
                throw new Exception("Tray: a failure message reached the tooltip: '" + tray.ToolTip + "'");
            form.ResetSettings();
            if (tray.ToolTip != "autocat\nconnection problem · paused" || tray.Disposed != 0)
                throw new Exception("Tray: reset must pause and keep the icon");
        }

        Func<FakeInput, MainForm> launch = ours => new MainForm(true, new Settings(), ours, Made(trays));
        // Closing, unloading from either place, a failing shutdown and a form that is only disposed.
        trays.Clear();
        using (var form = launch(null)) {
            form.Opacity = 0;
            form.Show();
            form.Close();
            if (trays.Count != 1 || trays[0].Disposed != 1)throw new Exception("Tray: closing must remove the icon once");
        }
        if (trays[0].Disposed != 1)throw new Exception("Tray: the icon was removed twice");
        Func<bool, int> unloaded = fromTray => {
            int closing = 0, closed = 0;
            using (var form = launch(null)) {
                var tray = trays[trays.Count - 1];
                form.Opacity = 0;
                form.Show();
                form.Hide();
                form.FormClosing += (s, e) => closing++;
                form.FormClosed += (s, e) => closed++;
                if (fromTray)tray.ChooseUnload();
                else FindButton(form, "unload autocat").Action();
                if (!form.closing || tray.Disposed != 1 || closing != 1 || closed != 1)
                    throw new Exception("Tray: unload from " + (fromTray ? "the tray" : "the button") + " closing=" +
                        closing + " closed=" + closed + " icons removed=" + tray.Disposed);
            }
            return closing + closed;
        };
        if (unloaded(false) != unloaded(true))throw new Exception("Tray: unload differs from the unload button");
        var failing = new FakeInput {FailRemoval = true};
        using (var form = launch(failing)) {
            form.settings.PauseKey = 257;
            form.ApplyBindings();
            bool failed = false;
            try {
                form.Shutdown();
            } catch (InvalidOperationException) {
                failed = true;
            }
            if (!failed || trays[trays.Count - 1].Disposed != 1)
                throw new Exception("Tray: a failing shutdown must still remove the icon");
        }
        using (var form = launch(null)) {
            var tray = trays[trays.Count - 1];
            form.Dispose();
            if (tray.Disposed != 1)throw new Exception("Tray: disposing the menu must remove the icon");
        }
    }

    static MicroButton FindButton(Control parent, string text) {
        foreach (Control c in parent.Controls) {
            var button = c as MicroButton;
            if (button != null && button.Text == text)return button;
            button = FindButton(c, text);
            if (button != null)return button;
        }
        return null;
    }
    static int strayFetches;

    static Bitmap Shot(MainForm form) {
        var b = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(b, form.ClientRectangle);
        return b;
    }

    static void Call(Control target, string method, object argument) {
        typeof(Control).GetMethod(method, System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Instance).Invoke(target, new[] {argument});
    }

    // A fresh form, one update check through the seam, and the form once the result has been applied.
    static MainForm UpdateChecked(Func<UpdateCheck.Reply> read) {
        UpdateCheck.Fetch = read;
        var form = new MainForm(true);
        form.footer.ContextMenuStrip = new ContextMenuStrip();
        form.Opacity = 0;
        form.Show();
        Application.DoEvents();
        form.BeginUpdateCheck(new Version(1, 2, 0, 0));
        form.BeginUpdateCheck(new Version(1, 2, 0, 0));
        form.updateThread.Join(3000);
        for (int i = 0; i < 5; i++) {
            Application.DoEvents();
            Thread.Sleep(10);
        }
        return form;
    }

    // The update notice as a launch drives it, with the network read replaced; nothing here reaches the network.
    static void VerifyUpdateNotice() {
        const string tag = "https://github.com/kaankutluturk/autocat/releases/tag/";
        const string page = "https://github.com/kaankutluturk/autocat/releases/latest";
        float scaleBefore = Skin.Scale;
        var fetch = UpdateCheck.Fetch;
        var open = UpdateCheck.Open;
        int reads = 0;
        var opened = new List<string>();
        UpdateCheck.Open = a => opened.Add(a);
        Func<string, Func<UpdateCheck.Reply>> redirect = location => () => {
            Interlocked.Increment(ref reads);
            return new UpdateCheck.Reply {Status = 302, Location = location};
        };
        try {
            if (UpdateCheck.ReleasePage != page)throw new Exception("Update notice: release page address");
            if (strayFetches != 0)throw new Exception("Update notice: a test mode attempted a network read");
            var directory = Path.Combine(Path.GetTempPath(), "autocat-notice-" + Guid.NewGuid().ToString("N"));
            try {
                using (var rendered = new MainForm(true))rendered.Render(directory);
            } finally {
                if (Directory.Exists(directory))Directory.Delete(directory, true);
            }
            Thread.Sleep(100);
            if (strayFetches != 0)throw new Exception("Update notice: --render attempted a network read");
            UpdateCheck.Fetch = redirect(tag + "v9.9.9");
            using (var form = new MainForm(true)) {
                form.Opacity = 0;
                form.Show();
                Application.DoEvents();
                Thread.Sleep(100);
                Application.DoEvents();
                if (reads != 0 || form.updateThread != null || form.updateLink != null)
                    throw new Exception("Update notice: an inspection form must not start a check");
            }
            UpdateCheck.Fetch = UpdateCheck.Network;
            using (var form = new MainForm(true)) {
                form.Opacity = 0;
                form.Show();
                form.BeginUpdateCheck(new Version(1, 2, 0, 0));
                if (form.updateThread != null)throw new Exception("Update notice: the real network read in a test mode");
            }

            // State and text for each kind of reply.
            Action<string, string> shows = (location, text) => {
                using (var form = UpdateChecked(redirect(location))) {
                    if (text == null ? form.updateLink != null : form.updateLink == null || !form.updateLink.Visible ||
                            form.updateLink.Text != text || !form.updateLink.Enabled)
                        throw new Exception("Update notice: '" + location + "' gave " +
                            (form.updateLink == null ? "no notice" : form.updateLink.Text));
                }
            };
            int before = reads;
            using (var form = UpdateChecked(redirect(tag + "v1.2.1")))
                if (form.updateLink.ContextMenuStrip == null || form.updateLink.ContextMenuStrip != form.footer.ContextMenuStrip)
                    throw new Exception("Update notice: right-click must open the footer menu");
            before = reads;
            shows(tag + "v1.2.1", "update available: 1.2.1");
            if (reads != before + 1)throw new Exception("Update notice: one check, one read");
            shows(tag + "v1.3.0", "update available: 1.3.0");
            shows(tag + "v2.0.0", "update available: 2.0.0");
            shows(tag + "1.10.0", "update available: 1.10.0");
            foreach (string none in new[] {
                tag + "v1.2.0", tag + "v1.1.1", tag + "v1.0.0", tag + "v1.2", tag + "v1.2.3.4", tag + "v1.2.x",
                tag + "v01.2.3", tag + "v99999.0.0", tag + "v-1.2.3", tag + "", "https://example.org/kaankutluturk/autocat/releases/tag/v1.2.1",
                "https://github.com/someone/autocat/releases/tag/v1.2.1", "/kaankutluturk/autocat/releases/tag/v1.2.1",
                "http://github.com/kaankutluturk/autocat/releases/tag/v1.2.1", null
            })
                shows(none, null);
            foreach (var failing in new Func<UpdateCheck.Reply>[] {
                () => { throw new TimeoutException(); },
                () => { throw new InvalidOperationException("boom"); },
                () => { throw new System.ComponentModel.Win32Exception(5); },
                () => { throw new AccessViolationException(); },
                () => null,
                () => new UpdateCheck.Reply {Status = 200, Location = tag + "v1.2.1"},
                () => new UpdateCheck.Reply {Status = 404}
            })
                using (var form = UpdateChecked(failing))
                    if (form.updateLink != null)throw new Exception("Update notice: a failed check shows nothing");

            // Activating the notice opens the fixed page, whatever the reply held, by mouse and by keyboard.
            foreach (string version in new[] {"v1.2.1", "v7.7.7"})
                using (var form = UpdateChecked(redirect(tag + version))) {
                    opened.Clear();
                    Call(form.updateLink, "OnClick", EventArgs.Empty);
                    foreach (var key in new[] {Keys.Enter, Keys.Space, Keys.A}) {
                        Call(form.updateLink, "OnKeyDown", new KeyEventArgs(key));
                        Call(form.updateLink, "OnKeyUp", new KeyEventArgs(key));
                    }
                    if (opened.Count != 3 || opened.Exists(a => a != page))
                        throw new Exception("Update notice: opened " + String.Join(" ", opened.ToArray()));
                    opened.Clear();
                    for (int repeat = 0; repeat < 6; repeat++)Call(form.updateLink, "OnKeyDown", new KeyEventArgs(Keys.Enter));
                    if (opened.Count != 1)throw new Exception("Update notice: a held key opened " + opened.Count + " pages");
                    Call(form.updateLink, "OnKeyUp", new KeyEventArgs(Keys.Enter));
                    Call(form.updateLink, "OnKeyDown", new KeyEventArgs(Keys.Space));
                    Call(form.updateLink, "OnKeyUp", new KeyEventArgs(Keys.Space));
                    if (opened.Count != 2)throw new Exception("Update notice: a new press must act again");
                    Control reached = null;
                    form.ActiveControl = null;
                    for (int i = 0; i < 100 && reached != form.updateLink; i++) {
                        form.SelectNextControl(form.ActiveControl, true, true, true, true);
                        reached = form.ActiveControl;
                    }
                    if (reached != form.updateLink || !form.updateLink.TabStop ||
                            form.updateLink.AccessibleRole != AccessibleRole.Link)
                        throw new Exception("Update notice: not reachable with the keyboard");
                }
            using (var form = UpdateChecked(redirect(tag + "v1.2.1"))) {
                UpdateCheck.Open = a => {
                    throw new InvalidOperationException("no browser");
                };
                Call(form.updateLink, "OnClick", EventArgs.Empty);
                if (!form.footer.Text.Contains("Could not open the release page"))
                    throw new Exception("Update notice: a failure to open must be reported in the footer");
                UpdateCheck.Open = a => opened.Add(a);
            }

            // The notice leaves the footer's status alone at every scale and across live rescales.
            var messages = new[] {
                "waiting for game", "attached", "shop not ready",
                "emote throttled: 3.2/s (avg 16.7ms, peak 40.1ms) while the game is busy loading something",
                new string('w', 150)
            };
            using (var form = UpdateChecked(redirect(tag + "v1.2.1")))
                using (var plain = new MainForm(true)) {
                    plain.Opacity = 0;
                    plain.Show();
                    float start = Skin.Scale;
                    foreach (float scale in new[] {1f, 1.25f, 4f / 3f, 1.5f, 1.75f, 2f, 2.5f, 3f, 1f}) {
                        form.SimulateDpiChange(scale);
                        plain.SimulateDpiChange(scale);
                        if (form.updateLink == null || form.updateLink.Text != "update available: 1.2.1")
                            throw new Exception("Update notice: lost across a rescale to " + scale);
                        if (!form.updateLink.Visible || form.updateLink.Right != plain.footer.Right ||
                                form.updateLink.Left < form.footer.Right)
                            throw new Exception("Update notice: not laid out again after a rescale to " + scale);
                        foreach (Control other in form.Controls)
                            if (other != form.updateLink && other.TabIndex >= form.updateLink.TabIndex)
                                throw new Exception("Update notice: not the last tab stop after a rescale to " + scale);
                        if (form.SelectNextControl(form.updateLink, true, true, true, false))
                            throw new Exception("Update notice: a tab stop follows the notice after a rescale to " + scale);
                        // The longest status that still leaves room for the notice is the tightest fit.
                        string edge = null;
                        for (int n = 10; n < 200; n++) {
                            form.bridge.State = new GameState {Message = new string('w', n)};
                            form.UpdateText();
                            if (!form.updateLink.Visible)break;
                            edge = new string('w', n);
                        }
                        for (int m = 0; m < 6; m++) {
                            string status = m < 5 ? messages[m] : edge;
                            string step = "scale " + scale + ", status " + m + ": ";
                            form.bridge.State = new GameState {Message = status};
                            plain.bridge.State = new GameState {Message = status};
                            form.UpdateText();
                            plain.UpdateText();
                            var link = form.updateLink;
                            int need = plain.footer.GetPreferredSize(Size.Empty).Width;
                            if (form.footer.Left != plain.footer.Left || form.footer.Top != plain.footer.Top ||
                                    form.footer.Height != plain.footer.Height || form.footer.Text != plain.footer.Text)
                                throw new Exception("Update notice, " + step + "footer moved");
                            if (!link.Visible) {
                                if (m < 3 || m == 5 || form.footer.Width != plain.footer.Width)
                                    throw new Exception("Update notice, " + step + "hidden or footer changed");
                                continue;
                            }
                            int preferred = link.GetPreferredSize(Size.Empty).Width;
                            if (m == 4 || form.footer.Width < need || form.footer.Right > link.Left || link.Width < preferred ||
                                    link.Bounds.IntersectsWith(form.footer.Bounds) || link.Top != form.footer.Top ||
                                    link.Height != form.footer.Height || !form.ClientRectangle.Contains(link.Bounds) ||
                                    link.Bottom > form.ClientSize.Height - Skin.D(6) ||
                                    link.Right > form.ClientSize.Width - Skin.D(18) + 1 || link.Font.Height >= link.Height)
                                throw new Exception("Update notice, " + step + "layout " + link.Bounds + " footer " +
                                    form.footer.Bounds);
                            using (var a = Shot(form))
                                using (var b = Shot(plain)) {
                                    for (int y = form.footer.Top; y < form.footer.Bottom; y++)
                                        for (int x = form.footer.Left; x < form.footer.Left + Math.Min(need, form.footer.Width); x++)
                                            if (a.GetPixel(x, y) != b.GetPixel(x, y))
                                                throw new Exception("Update notice, " + step + "status text differs at " + x + "," + y);
                                    bool ink = false;
                                    for (int y = link.Top; y < link.Bottom; y++) {
                                        if (a.GetPixel(link.Right - 1, y) != Skin.Background)
                                            throw new Exception("Update notice, " + step + "text reaches the edge of the notice");
                                        for (int x = link.Left; x < link.Right; x++)
                                            if (a.GetPixel(x, y) != Skin.Background)ink = true;
                                    }
                                    if (!ink)throw new Exception("Update notice, " + step + "nothing drawn");
                                }
                        }
                        form.bridge.State = new GameState();
                        plain.bridge.State = new GameState();
                    }
                    var colour = Color.FromArgb(10, 200, 30);
                    form.ApplyAccent(colour);
                    form.SimulateDpiChange(2f);
                    if (form.updateLink.ForeColor != colour)throw new Exception("Update notice: follows the accent");
                    form.ApplyAccent(Skin.DefaultAccent);
                    form.SimulateDpiChange(start);
                }
            Skin.InitializeScale(scaleBefore);

            // Closing the menu aborts a request that is still in flight.
            var silent = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            silent.Start();
            int savedTimeout = UpdateCheck.TimeoutMs;
            try {
                UpdateCheck.TimeoutMs = 20000;
                Exception ended = null;
                var call = new Thread(() => {
                    try {
                        UpdateCheck.Send(new Uri("http://127.0.0.1:" + ((System.Net.IPEndPoint)silent.LocalEndpoint).Port + "/"));
                    } catch (Exception e) {
                        ended = e;
                    }
                }) {IsBackground = true};
                call.Start();
                using (var accepted = silent.AcceptTcpClient())
                using (var closing = new MainForm(true)) {
                    Thread.Sleep(200);
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    closing.Shutdown();
                    if (!call.Join(3000) || ended == null || watch.ElapsedMilliseconds > 2000)
                        throw new Exception("Update notice: closing must abort the request in flight");
                }
            } finally {
                UpdateCheck.TimeoutMs = savedTimeout;
                silent.Stop();
            }

            // Closing while the check is still running neither waits for it nor lets it touch the form later.
            foreach (bool dispose in new[] {false, true}) {
                var gate = new ManualResetEvent(false);
                var started = new ManualResetEvent(false);
                UpdateCheck.Fetch = () => {
                    started.Set();
                    gate.WaitOne();
                    return new UpdateCheck.Reply {Status = 302, Location = tag + "v1.2.1"};
                };
                Exception stray = null;
                ThreadExceptionEventHandler seen = (s, e) => stray = e.Exception;
                Application.ThreadException += seen;
                var form = new MainForm(true);
                try {
                    form.Opacity = 0;
                    form.Show();
                    Application.DoEvents();
                    form.BeginUpdateCheck(new Version(1, 2, 0, 0));
                    if (!started.WaitOne(3000))throw new Exception("Update notice: the check did not start");
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    form.Shutdown();
                    if (dispose)form.Dispose();
                    if (watch.ElapsedMilliseconds > 500 || !form.updateThread.IsBackground || !form.updateThread.IsAlive)
                        throw new Exception("Update notice: closing waited for the check");
                    gate.Set();
                    if (!form.updateThread.Join(3000))throw new Exception("Update notice: the check never ended");
                    for (int i = 0; i < 5; i++) {
                        Application.DoEvents();
                        Thread.Sleep(10);
                    }
                    if (form.updateLink != null || stray != null)
                        throw new Exception("Update notice: a check finishing after close touched the form " + stray);
                } finally {
                    Application.ThreadException -= seen;
                    gate.Set();
                    form.Dispose();
                }
            }
        } finally {
            UpdateCheck.Fetch = fetch;
            UpdateCheck.Open = open;
            Skin.InitializeScale(scaleBefore);
        }
    }

    static Message Msg(Control target, int message, long w, long l = 0) {
        return Message.Create(target.Handle, message, new IntPtr(w), new IntPtr(l));
    }

    // The bindings as the menu runs them, with the system replaced by a recording source.
    static void VerifyBindingFlows() {
        var input = new FakeInput();
        BindingEditor.HeldModifiers = () => Keys.None;
        try {
            using (var form = new MainForm(true, new Settings(), input)) {
                form.Opacity = 0;
                form.Show();
                form.SelectPage(4);
                Application.DoEvents();
                Action<int> tap = key => {
                    input.Down.Add(key);
                    form.bindings.Poll();
                    input.Down.Remove(key);
                    form.bindings.Poll();
                    Application.DoEvents();
                };
                if (input.Installs != 0 || input.Wheel != null)throw new Exception("Bindings: default has a hook");
                tap(36);
                if (!form.running)throw new Exception("Bindings: the pause key does not resume");
                input.Down.Add(36);
                for (int i = 0; i < 4; i++)form.bindings.Poll();
                Application.DoEvents();
                input.Down.Remove(36);
                form.bindings.Poll();
                if (form.running)throw new Exception("Bindings: a held pause key repeated or was missed");
                tap(36);
                if (!form.running)throw new Exception("Bindings: the pause key does not pause and resume");
                var editors = FindEditors(form.pages[4]);
                if (editors.Count != 2)throw new Exception("Bindings: two editors expected");
                editors[1].Edit();
                tap(36);
                if (!form.running || !BindingEditor.Capturing)
                    throw new Exception("Bindings: a binding fires while one is being chosen");
                Native.SendMessage(editors[1].Handle, 0x0008, IntPtr.Zero, IntPtr.Zero);
                if (BindingEditor.Capturing)throw new Exception("Bindings: losing focus does not cancel");
                tap(36);
                if (form.running)throw new Exception("Bindings: the pause key is dead after a cancelled choice");
                tap(45);
                if (form.Visible)throw new Exception("Bindings: the menu key does not hide the menu");
                tap(36);
                if (!form.running)throw new Exception("Bindings: bindings must work while the menu is hidden");
                tap(36);
                form.Show();
                form.settings.PauseKey = 1;
                form.ApplyBindings();
                input.Cursor = new Point(form.Left + form.Width / 2, form.Top + form.Height / 2);
                tap(1);
                if (form.running)throw new Exception("Bindings: a click on the visible menu must not fire");
                input.Cursor = new Point(form.Right + 40, form.Bottom + 40);
                tap(1);
                if (!form.running)throw new Exception("Bindings: a mouse binding outside the menu does not fire");
                form.Hide();
                input.Cursor = new Point(form.Left + 5, form.Top + 5);
                tap(1);
                if (form.running)throw new Exception("Bindings: a mouse binding while hidden does not fire");
                form.Show();
                form.settings.PauseKey = 257;
                form.ApplyBindings();
                if (input.Installs != 1 || input.Wheel == null)
                    throw new Exception("Bindings: a wheel binding does not install the hook");
                input.Wheel(257, new Point(form.Left + 5, form.Top + 5));
                Application.DoEvents();
                if (form.running)throw new Exception("Bindings: the wheel over the visible menu must not fire");
                input.Wheel(257, new Point(form.Right + 40, form.Bottom + 40));
                Application.DoEvents();
                if (!form.running)throw new Exception("Bindings: a wheel binding does not fire");
                form.ResetSettings();
                if (input.Wheel != null || input.Removals != 1 || form.settings.PauseKey != 36)
                    throw new Exception("Bindings: reset must remove the wheel hook");
                form.settings.MenuKey = 258;
                form.ApplyBindings();
                if (input.Wheel == null)throw new Exception("Bindings: second wheel install");
                form.Shutdown();
                if (input.Wheel != null || input.Removals != 2)
                    throw new Exception("Bindings: closing must remove the wheel hook");
            }
            input = new FakeInput();
            using (var form = new MainForm(true, new Settings(), input)) {
                form.Opacity = 0;
                form.Show();
                form.SelectPage(4);
                Application.DoEvents();
                var editors = FindEditors(form.pages[4]);
                input.Down.Add(116);
                editors[1].Edit();
                var press = Msg(editors[1], 0x100, 116);
                if (!editors[1].PreFilterMessage(ref press))throw new Exception("Capture: key not taken");
                Application.DoEvents();
                if (form.settings.PauseKey != 116 || BindingEditor.Capturing)
                    throw new Exception("Capture: the pause binding was not set");
                form.bindings.Poll();
                Application.DoEvents();
                if (form.running)throw new Exception("Capture: the key that set a binding must not fire it");
                input.Down.Remove(116);
                form.bindings.Poll();
                input.Down.Add(36);
                form.bindings.Poll();
                Application.DoEvents();
                if (form.running)throw new Exception("Capture: the old binding still fires");
                input.Down.Remove(36);
                input.Down.Add(116);
                form.bindings.Poll();
                Application.DoEvents();
                if (!form.running)throw new Exception("Capture: the new binding does not fire");
                input.Down.Remove(116);
                form.bindings.Poll();
                editors[0].Edit();
                press = Msg(editors[0], 0x100, 116);
                editors[0].PreFilterMessage(ref press);
                Application.DoEvents();
                if (form.settings.MenuKey != 45 || BindingEditor.Capturing)
                    throw new Exception("Capture: a conflicting binding must be rejected");
                form.Hide();
            }
            // The key that chooses a binding is still down when the choice lands; it must be released and pressed
            // again before it can fire, whether or not it was already watched.
            Action<string, int, int, int, bool, string, bool> choose = (step, editor, menuKey, key, ctrl, bound,
                    resumes) => {
                var ours = new FakeInput();
                using (var form = new MainForm(true, new Settings {MenuKey = menuKey}, ours)) {
                    form.Opacity = 0;
                    form.Show();
                    form.SelectPage(4);
                    Application.DoEvents();
                    var editors = FindEditors(form.pages[4]);
                    BindingEditor.HeldModifiers = () => ctrl ? Keys.Control : Keys.None;
                    if (ctrl)ours.Down.Add(162);
                    ours.Down.Add(key);
                    editors[editor].Edit();
                    var press = Msg(editors[editor], 0x100, key);
                    editors[editor].PreFilterMessage(ref press);
                    Application.DoEvents();
                    form.bindings.Poll();
                    Application.DoEvents();
                    string now = form.settings.MenuKey + "/" + form.settings.MenuModifiers + "/" +
                        form.settings.PauseKey + "/" + form.settings.PauseModifiers;
                    if (now != bound)throw new Exception("Choosing, " + step + ": bindings are " + now);
                    if (form.running || !form.Visible)
                        throw new Exception("Choosing, " + step + ": choosing the binding also fired it");
                    ours.Down.Remove(key);
                    form.bindings.Poll();
                    ours.Down.Add(key);
                    form.bindings.Poll();
                    Application.DoEvents();
                    if (form.running != resumes || form.Visible != resumes)
                        throw new Exception("Choosing, " + step + ": the binding is dead after it was chosen");
                    form.Hide();
                }
            };
            choose("the pause key again", 1, 45, 36, false, "45/0/36/0", true);
            choose("ctrl + the pause key", 1, 45, 36, true, "45/0/36/2", true);
            choose("ctrl + the menu key", 0, 45, 45, true, "45/2/36/0", false);
            choose("the menu key for pause", 1, 45, 45, false, "45/0/36/0", false);
            choose("escape clears pause while it is the menu key", 1, 27, 27, false, "27/0/0/0", false);
        } finally {
            BindingEditor.HeldModifiers = () => ModifierKeys;
        }
    }

    // Choosing a binding takes the menu's own input messages only.
    static void VerifyCapture() {
        int boundKey = -1, boundMods = -1;
        var held = Keys.None;
        BindingEditor.HeldModifiers = () => held;
        try {
            using (var host = new Form {Opacity = 0, ShowInTaskbar = false})
                using (var other = new BindingEditor(() => 0, () => 0, (k, m) => { })) {
                    var editor = new BindingEditor(() => boundKey, () => boundMods, (k, m) => {
                        boundKey = k;
                        boundMods = m;
                    });
                    host.Controls.Add(editor);
                    host.Controls.Add(other);
                    host.Show();
                    Application.DoEvents();
                    Func<int, long, long, bool> send = (message, w, l) => {
                        var m = Msg(editor, message, w, l);
                        return editor.PreFilterMessage(ref m);
                    };
                    Action<string, int, int> expect = (step, key, mods) => {
                        Application.DoEvents();
                        if (boundKey != key || boundMods != mods || BindingEditor.Capturing ||
                                editor.Filtering)
                            throw new Exception("Capture, " + step + ": got " + boundKey + "/" + boundMods +
                                (BindingEditor.Capturing ? " (still choosing)" : ""));
                        boundKey = boundMods = -1;
                    };
                    Action<string> still = step => {
                        Application.DoEvents();
                        if (boundKey != -1 || !BindingEditor.Capturing)
                            throw new Exception("Capture, " + step + ": must keep choosing without a result");
                    };
                    if (send(0x100, 65, 0) || send(0x201, 0, 0) || send(0x20a, 120 << 16, 0))
                        throw new Exception("Capture: input is taken while not choosing");
                    editor.Edit();
                    if (!BindingEditor.Capturing || !editor.Filtering)throw new Exception("Capture: not started");
                    if (!send(0x100, 65, 0))throw new Exception("Capture: key not taken");
                    expect("letter", 65, 0);
                    Action pump = () => {
                        host.BeginInvoke(new Action(() => { }));
                        Application.DoEvents();
                    };
                    int calls = BindingEditor.FilterCalls;
                    pump();
                    if (BindingEditor.FilterCalls != calls)
                        throw new Exception("Capture: a finished choice stays registered");
                    editor.Edit();
                    pump();
                    if (BindingEditor.FilterCalls == calls)
                        throw new Exception("Capture: the message loop must reach the filter");
                    held = Keys.Control;
                    editor.Edit();
                    send(0x100, 45, 0);
                    expect("ctrl + key", 45, 2);
                    held = Keys.Control | Keys.Shift | Keys.Alt;
                    editor.Edit();
                    send(0x104, 116, 0);
                    expect("ctrl + shift + alt + key", 116, 7);
                    held = Keys.None;
                    editor.Edit();
                    send(0x100, 91, 0);
                    still("win held");
                    send(0x100, 45, 0);
                    expect("win + key", 45, 8);
                    held = Keys.Control;
                    editor.Edit();
                    send(0x100, 27, 0);
                    expect("escape clears", 0, 0);
                    held = Keys.None;
                    foreach (int modifier in new[] {16, 17, 18, 91, 92}) {
                        editor.Edit();
                        send(0x100, modifier, 0);
                        still("modifier " + modifier + " down");
                        send(0x101, modifier, 0);
                        expect("modifier " + modifier + " alone", modifier, 0);
                    }
                    editor.Edit();
                    send(0x104, 18, 0);
                    send(0x105, 18, 0);
                    expect("alt alone, system messages", 18, 0);
                    held = Keys.Control;
                    editor.Edit();
                    send(0x100, 17, 0);
                    send(0x100, 45, 0);
                    expect("ctrl then key", 45, 2);
                    if (send(0x101, 17, 0))throw new Exception("Capture: release after the choice is taken");
                    held = Keys.None;
                    editor.Edit();
                    send(0x100, 17, 0);
                    send(0x100, 16, 0);
                    send(0x101, 17, 0);
                    still("earlier modifier released");
                    send(0x101, 16, 0);
                    expect("the last modifier pressed decides", 16, 0);
                    foreach (var button in new[] {
                        new[] {0x201, 0, 1},
                        new[] {0x203, 0, 1},
                        new[] {0x204, 0, 2},
                        new[] {0x206, 0, 2},
                        new[] {0x207, 0, 4},
                        new[] {0x209, 0, 4},
                        new[] {0x20b, 1, 5},
                        new[] {0x20d, 1, 5},
                        new[] {0x20b, 2, 6},
                        new[] {0x20d, 2, 6},
                        new[] {0x20a, 120, 256},
                        new[] {0x20a, -120, 257},
                        new[] {0x20e, -120, 258},
                        new[] {0x20e, 120, 259}
                    }) {
                        editor.Edit();
                        if (!send(button[0], (long)(ushort)(short)button[1] << 16, 0))
                            throw new Exception("Capture: message " + button[0].ToString("x") + " not taken");
                        expect("message " + button[0].ToString("x") + " " + button[1], button[2], 0);
                    }
                    held = Keys.Control;
                    editor.Edit();
                    send(0x20a, 120 << 16, 0);
                    expect("ctrl + wheel", 256, 2);
                    held = Keys.None;
                    editor.Edit();
                    send(0x100, 65, 0x40000000);
                    send(0x100, 229, 0);
                    still("key repeat and IME keys");
                    send(0x101, 44, 0);
                    expect("print screen", 44, 0);
                    editor.Edit();
                    send(0x100, 65, 0);
                    send(0x100, 66, 0);
                    send(0x201, 0, 0);
                    expect("one choice per capture", 65, 0);
                    editor.Edit();
                    var elsewhere = Message.Create(IntPtr.Zero, 0x100, new IntPtr(65), IntPtr.Zero);
                    if (editor.PreFilterMessage(ref elsewhere))throw new Exception("Capture: foreign window message");
                    still("foreign message");
                    Native.SendMessage(editor.Handle, 0x0008, IntPtr.Zero, IntPtr.Zero);
                    if (BindingEditor.Capturing || editor.Filtering || boundKey != -1)
                        throw new Exception("Capture: losing focus must cancel");
                    editor.Edit();
                    editor.Visible = false;
                    if (BindingEditor.Capturing || editor.Filtering || boundKey != -1)
                        throw new Exception("Capture: hiding must cancel");
                    editor.Visible = true;
                    editor.Edit();
                    other.Edit();
                    if (editor.Filtering || !other.Filtering)throw new Exception("Capture: one choice at a time");
                    other.Dispose();
                    if (BindingEditor.Capturing || other.Filtering)
                        throw new Exception("Capture: dispose must cancel");
                    // A setter that fails is reported and must not leave the menu swallowing its input.
                    Exception thrown = null;
                    ThreadExceptionEventHandler reported = (s, e) => thrown = e.Exception;
                    Application.ThreadException += reported;
                    try {
                        using (var failing = new BindingEditor(() => 0, () => 0,
                                (k, m) => { throw new InvalidOperationException("setter"); })) {
                            host.Controls.Add(failing);
                            failing.Edit();
                            var taken = Msg(failing, 0x100, 65);
                            failing.PreFilterMessage(ref taken);
                            Application.DoEvents();
                            if (thrown == null || thrown.Message != "setter")
                                throw new Exception("Capture: a failing setter must not be swallowed");
                            if (BindingEditor.Capturing || failing.Filtering)
                                throw new Exception("Capture: a failing setter must end the choice");
                        }
                    } finally {
                        Application.ThreadException -= reported;
                    }
                }
        } finally {
            BindingEditor.HeldModifiers = () => ModifierKeys;
        }
    }

    // The PawPass toggle: off unless checked, sent only while running, its status in the footer, and room for
    // it in the gifts group at every scale.
    static void VerifyPawPass() {
        Func<MainForm, string, MicroCheck> find = (form, text) => {
            foreach (Control group in form.pages[1].Controls)
                foreach (Control c in group.Controls)
                    if (c is MicroCheck && c.Text == text)return (MicroCheck)c;
            throw new Exception("PawPass toggle: no \"" + text + "\" on the automation tab");
        };
        using (var form = new MainForm(true, new Settings())) {
            form.Opacity = 0;
            form.Show();
            form.SelectPage(1);
            float start = Skin.Scale;
            if (form.settings.PawPass || form.bridge.PawPass || form.bridge.Command() != "SET|0|0|0|0|10|0|0|0")
                throw new Exception("PawPass toggle: must be off by default");
            Call(find(form, "auto claim pawpass"), "OnKeyDown", new KeyEventArgs(Keys.Space));
            if (!form.settings.PawPass || form.bridge.Command() != "SET|0|0|0|0|10|0|0|0")
                throw new Exception("PawPass toggle: checked while paused must not be sent");
            form.ToggleRunning();
            if (form.bridge.Command() != "SET|1|0|0|0|10|0|0|1")
                throw new Exception("PawPass toggle: resume must send it, sent " + form.bridge.Command());
            form.ToggleRunning();
            if (form.bridge.Command() != "SET|0|0|0|0|10|0|0|0")
                throw new Exception("PawPass toggle: pause must withdraw it");
            string line = "OK|100|101|10|1|0|0|12|2|3|4||5|6|1|16.7|20|20|6|12";
            form.bridge.Connection = "attached";
            form.bridge.State = GameState.Parse(line + "|" +
                Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("open pawpass once")));
            form.UpdateText();
            if (form.footer.Text != "attached · paused · open pawpass once")
                throw new Exception("PawPass toggle: footer status, got " + form.footer.Text);
            form.bridge.State = GameState.Parse(line);
            form.UpdateText();
            if (form.footer.Text != "attached · paused")
                throw new Exception("PawPass toggle: a state line without the status must show none");
            form.bridge.State = new GameState();
            form.bridge.Connection = "waiting for game";
            foreach (float scale in new[] {1f, 1.25f, 4f / 3f, 1.5f, 1.75f, 2f, 2.5f, 3f, 1f}) {
                form.SimulateDpiChange(scale);
                var claim = find(form, "auto claim pawpass");
                var collect = find(form, "auto collect");
                var group = claim.Parent;
                string at = "PawPass toggle, scale " + scale + ": ";
                if (!form.settings.PawPass)throw new Exception(at + "setting lost across a rescale");
                if (group != collect.Parent || group.Text != "gifts" || form.pages[1].Controls.Count != 2)
                    throw new Exception(at + "must sit in the gifts group of the two-group layout");
                if (claim.Left != collect.Left || claim.Width != collect.Width || claim.Height != collect.Height ||
                        claim.Top < collect.Bottom || claim.TabIndex <= collect.TabIndex)
                    throw new Exception(at + "must follow auto collect in the same column");
                if (!new Rectangle(Skin.D(1), Skin.D(8), group.Width - Skin.D(2), group.Height - Skin.D(9))
                        .Contains(claim.Bounds) || !form.pages[1].ClientRectangle.Contains(group.Bounds))
                    throw new Exception(at + "outside its group");
                int text = TextRenderer.MeasureText(claim.Text, Skin.Font).Width;
                if (Skin.D(18) + text > claim.Width)throw new Exception(at + "label is clipped");
                using (var shot = new Bitmap(claim.Width, claim.Height)) {
                    claim.DrawToBitmap(shot, claim.ClientRectangle);
                    bool ink = false;
                    for (int y = 0; y < shot.Height; y++) {
                        if (shot.GetPixel(shot.Width - 1, y).ToArgb() != shot.GetPixel(shot.Width - 1, 0).ToArgb())
                            throw new Exception(at + "label reaches the edge of the control");
                        for (int x = Skin.D(18); x < shot.Width; x++)
                            if (shot.GetPixel(x, y).ToArgb() != shot.GetPixel(shot.Width - 1, 0).ToArgb())ink = true;
                    }
                    if (!ink)throw new Exception(at + "label not drawn");
                }
            }
            form.SimulateDpiChange(start);
            form.ResetSettings();
            if (form.settings.PawPass || form.bridge.PawPass || form.bridge.Command() != "SET|0|0|0|0|10|0|0|0")
                throw new Exception("PawPass toggle: reset must turn it off");
        }
    }

    public void Verify(string path) {
        UpdateCheck.Fetch = () => {
            Interlocked.Increment(ref strayFetches);
            throw new InvalidOperationException("network");
        };
        if (ShowInTaskbar || FormBorderStyle != FormBorderStyle.None || !TopMost || pages.Length != 5)
            throw new Exception("Window style");
        Func<Control, string, bool> has = null;
        has = (c, t) => {
            foreach (Control x in c.Controls)
                if (x.Text == t || has(x, t))return true;
            return false;
        };
        using (var launched = new MainForm(true, new Settings {
            Clicker = true,
            Gifts = true,
            Exchange = true,
            Emoting = true,
            InstaGift = true,
            PawPass = true,
            EmoteRate = 7
        })) {
            Action<string, string, bool> sent = (step, command, clicking) => {
                // Every poll on every connection sends this line, built from the same fields.
                if (launched.bridge.Command() != command || launched.taps.Enabled != clicking)
                    throw new Exception("Pause model, " + step + ": sent " + launched.bridge.Command());
            };
            if (!launched.settings.Gifts || !launched.settings.Exchange || !launched.settings.PawPass ||
                    launched.running || launched.unlockAll)throw new Exception("Pause model: saved toggles must be restored paused");
            sent("launch", "SET|0|0|0|0|7|1|0|0", false);
            launched.ToggleRunning();
            sent("resume", "SET|1|1|1|1|7|1|0|1", true);
            launched.ToggleRunning();
            sent("pause", "SET|0|0|0|0|7|1|0|0", false);
            launched.ToggleRunning();
            launched.ResetSettings();
            sent("reset", "SET|0|0|0|0|10|0|0|0", false);
        }
        using (var closed = new MainForm(true, new Settings {Gifts = true, PawPass = true})) {
            closed.ToggleRunning();
            closed.Shutdown();
            if (closed.bridge.Command() != "SET|0|0|0|0|10|0|0|0")
                throw new Exception("Pause model, shutdown: sent " + closed.bridge.Command());
        }
        if (has(pages[4], "save settings") || !has(pages[4], "reset settings"))
            throw new Exception("Misc app group: save button must be gone, reset kept");
        if (Text != Title || !has(pages[4], "unload autocat") || has(pages[4], "unload autocat.xyz"))
            throw new Exception("Visible name: window title and unload button read autocat");
        settings.Clicker = settings.Gifts = settings.Exchange = settings.Emoting = settings.InstaGift =
            settings.PawPass = true;
        ToggleRunning();
        if (!taps.Enabled)throw new Exception("Shared-feature blocker");
        Show();
        Sync();
        if (!taps.Enabled)throw new Exception("Open menu pauses clicks");
        Hide();
        ToggleRunning();
        if (taps.Enabled)throw new Exception("Pause failed");
        SelectPage(3);
        if (tabs.Selected != 3)throw new Exception("Theme page navigation");
        ApplyAccent(Color.FromArgb(10, 20, 30));
        if (settings.AccentColor != ((10 << 16) | (20 << 8) | 30) ||
                Skin.Accent.ToArgb() != Color.FromArgb(10, 20, 30).ToArgb())
            throw new Exception("Accent apply");
        SelectPage(0);
        ResetSettings();
        if (settings.Taps != 10 || settings.MenuKey != 45 || settings.PauseKey != 36 || settings.Clicker ||
                settings.Gifts || settings.Exchange || settings.Emoting || settings.InstaGift ||
                settings.PawPass || settings.EmoteRate != 10 || running || taps.Enabled || settings.AccentColor != 0xE0A189 ||
                Skin.Accent.ToArgb() != Skin.DefaultAccent.ToArgb())throw new Exception("Reset defaults");
        RequestDanger(true);
        if (rates.Enabled || !rates.Pending)throw new Exception("Danger confirmation");
        ConfirmDanger();
        settings.Taps = 1000;
        settings.EmoteRate = 1000;
        Sync();
        if (taps.Rate != 1000 || settings.EmoteRate != 1000 || !settings.ExtremeRates)
            throw new Exception("Custom rate");
        RequestDanger(false);
        if (taps.Rate != 200 || settings.Taps != 200 || settings.EmoteRate != 100 || settings.ExtremeRates ||
                rates.Enabled)throw new Exception("Immediate clamp");
        SelectPage(2);
        ApplyAccent(Color.FromArgb(11, 22, 33));
        ToggleRunning();
        bool runningBefore = running;
        float before = Skin.Scale;
        SimulateDpiChange(before * 2);
        if (Math.Abs(Skin.Scale - before * 2) > 0.01f || tabs.Selected != 2 || pages.Length != 5 ||
                pages[2] == null)throw new Exception("Live rescale: scale/page-selection not applied");
        if (Skin.Accent.ToArgb() != Color.FromArgb(11, 22, 33).ToArgb())
            throw new Exception("Live rescale: accent lost across rebuild");
        if (running != runningBefore)throw new Exception("Live rescale: automation state lost");
        SimulateDpiChange(before);
        if (Math.Abs(Skin.Scale - before) > 0.01f)
            throw new Exception("Live rescale: restore to prior scale failed");
        SimulateDisplayChange(1f, new Size(3840, 2160), new Rectangle(0, 0, 3840, 2120));
        if (Math.Abs(Skin.Scale - 2f) > 0.01f)
            throw new Exception("Display change: 4K at 100% must scale to 2x");
        SimulateDisplayChange(1f, new Size(2560, 1440), new Rectangle(0, 0, 2560, 1400));
        if (Math.Abs(Skin.Scale - 4f / 3f) > 0.01f)
            throw new Exception("Display change: 1440p at 100% must scale to 1.33x");
        Location = new Point(5000, 5000);
        var work1080 = new Rectangle(0, 0, 1920, 1040);
        SimulateDisplayChange(1f, new Size(1920, 1080), work1080);
        if (Math.Abs(Skin.Scale - 1f) > 0.01f || !work1080.Contains(Bounds))
            throw new Exception("Display change: back to 1080p must return to 1x and stay on screen");
        var work1440 = new Rectangle(0, 0, 2560, 1400);
        SimulateDisplayChange(2f, new Size(2560, 1440), work1440);
        if (Math.Abs(Skin.Scale - 2f) > 0.01f)throw new Exception("Display change: stale 200% at 1440p");
        SimulateDisplayChange(1.25f, new Size(2560, 1440), work1440);
        if (Math.Abs(Skin.Scale - 4f / 3f) > 0.01f)
            throw new Exception("Display change: settled 125% at 1440p must correct the scale to 1.33x");
        SimulateDpiChange(2f);
        if (!InDragZone(new Point(200, 80)) || InDragZone(new Point(400, 30)))
            throw new Exception("Drag zone must follow the 2x scale");
        SimulateDpiChange(1f);
        if (InDragZone(new Point(200, 80)) || !InDragZone(new Point(100, 30)))
            throw new Exception("Drag zone at 1x");
        SimulateDpiChange(before);
        SelectPage(2);
        Show();
        bridge.State = GameState.Parse("OK|100|101|10|1|0|0|12|2|3|4||5|6|1|16.7|20|20|6|12");
        UpdateText();
        SimulateDpiChange(before * 2);
        string tokensAfterRebuild = tokenCounts.Text;
        SimulateDpiChange(before);
        bridge.State = new GameState();
        if (!tokensAfterRebuild.Contains("normal: 6") || !tokensAfterRebuild.Contains("emote: 12"))
            throw new Exception("Live rescale: token counts not shown after rebuild");
        SelectPage(3);
        var wheelImage = typeof(ColorWheel).GetField("wheel",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var wheelPick = typeof(ColorWheel).GetMethod("Pick",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        using (var b = new Bitmap(themeWheel.Width, themeWheel.Height)) {
            themeWheel.DrawToBitmap(b, themeWheel.ClientRectangle);
            var dragged = (Bitmap)wheelImage.GetValue(themeWheel);
            for (int step = 1; step <= 3; step++) {
                wheelPick.Invoke(themeWheel,
                    new object[] {themeWheel.Width / 2 + step * 6, themeWheel.Height / 2 + step});
                themeWheel.DrawToBitmap(b, themeWheel.ClientRectangle);
            }
            if (dragged == null || !ReferenceEquals(dragged, wheelImage.GetValue(themeWheel)))
                throw new Exception("Color wheel: image regenerated while dragging");
            ApplyAccent(Color.FromArgb(40, 20, 10));
            themeWheel.DrawToBitmap(b, themeWheel.ClientRectangle);
            var darker = (Bitmap)wheelImage.GetValue(themeWheel);
            SimulateDpiChange(before * 2);
            SimulateDpiChange(before);
            foreach (var replaced in new[] {dragged, darker}) {
                bool disposed = false;
                try {
                    disposed = replaced.Width < 0;
                } catch (ArgumentException) {
                    disposed = true;
                }
                if (ReferenceEquals(dragged, darker) || !disposed)
                    throw new Exception("Color wheel: replaced image not disposed");
            }
        }
        Hide();
        ResetSettings();
        VerifyBindingFlows();
        VerifyCapture();
        VerifyLogo();
        VerifyLogoInMenu();
        VerifyTaskbar();
        VerifyMenuRoutes();
        VerifyMinimize();
        VerifyTray();
        VerifyUpdateNotice();
        VerifyPawPass();
        if (NotifyTray.Created != 0)throw new Exception("Tray: a test mode created a notification area icon");
        File.WriteAllText(path,
            "PASS: inline danger confirmation and independent 200 click / 100 emote clamps\nPASS: reset restores clean defaults and pauses automation\nPASS: borderless, topmost five-section menu whose inspection form is a tool window with no taskbar entry\nPASS: all feature toggles coexist\nPASS: taps remain enabled with menu open\nPASS: pause and resume\nPASS: theme page navigation and accent apply/reset\nPASS: live DPI rescale rebuilds the control tree without losing scale, selected tab, accent, or running state\nPASS: resolution changes (1080p to 4K to 1440p to 1080p) rescale live and keep the window on screen\nPASS: a late scaling change after a resolution switch corrects the scale; drag area follows the scale\nPASS: misc app group has reset and unload but no redundant save button\nPASS: token counts are shown again after a live rescale\nPASS: color wheel image is reused while dragging and disposed when replaced or rebuilt\nPASS: paused sends only Insta Gift at launch and after pause; resume sends every checked feature; reset sends none; shutdown sends none\nPASS: bindings in the menu: one action per press, none while choosing a binding, none for a click on the visible menu, working while hidden, new binding replaces the old one, choosing a binding never fires it\nPASS: the wheel hook follows the wheel bindings through binding changes, reset and shutdown, and wheel input over the visible menu is ignored\nPASS: choosing a binding takes the menu's own key, mouse button and wheel messages, applies the modifier rules, and ends on a choice, lost focus, hide, dispose or a failing setter\nPASS: the autocat wordmark draws auto in #EEEBE8 and cat in the accent color, pixel for pixel like one word in the middle of its block (within a pixel, ink inside the block) at scales 1 to 3, in the same font\nPASS: the wordmark in the menu is centered in the space left of the tabs at the same height, unclipped and in the logo colors at scales 1 to 3 and after live rescales\nPASS: window title and unload button read autocat\nPASS: the menu is an unowned, borderless, minimizable, always-on-top app window with the application icon at the primary display's small and large sizes, and stays one through live rescales and display changes; inspection forms stay tool windows with no taskbar button\nPASS: the menu stays on top and is brought forward by every route that shows it (binding, tray click, tray menu, show request); hiding by binding, tray click, tray menu or taskbar minimize (also while choosing a binding) leaves one visible state, the tray menu offers show or hide to match, and a rescale or display change keeps the single icon\nPASS: the tray menu has show or hide and unload in the dark footer style, the tooltip carries only the connection and pause state within 63 characters, also while hidden and after reset, and a failure message never reaches it\nPASS: the tray icon is removed once on close, on unload from the tray or the button (which close the same way), when shutdown fails, and when the menu is only disposed\nPASS: a minimize by ShowWindow or the taskbar command, by any route, is a hide with settings saved and the tray icon kept; binding, tray and show requests then give a normal, topmost window at its size and place, also when the window was left iconic; the menu cannot be maximized; a closing menu is left alone; a tray that cannot be created does not stop the menu\nPASS: no test or inspection mode creates a notification area icon\nPASS: update notice: shown only for a newer version and named from the validated numbers, nothing for equal, older, malformed, foreign, unreachable or non-redirect replies, one read per launch and none in test, render or inspection modes\nPASS: update notice opens only the fixed release page, by mouse and keyboard, and is reachable with Tab\nPASS: update notice does not move, clip or overlap the footer status at scales 1 to 3 and across live rescales, and gives way to a status that needs the room\nPASS: closing while an update check is in flight is immediate and the late result does not touch the form\nPASS: auto claim pawpass is off by default and after reset, is sent to the game component only while running, shows its status in the footer, and fits the gifts group under auto collect at scales 1 to 3 and across live rescales\n");
    }
}

static class EntryPoint {
    [STAThread]
    static void Main(string[] args) {
        // Per-Monitor-V2 first (needed for live WM_DPICHANGED); older Windows falls back to the legacy
        // system-aware call, correct at launch but not live.
        bool perMonitor = false;
        try {
            perMonitor =
                Native.SetProcessDpiAwarenessContext(Native.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        } catch (Exception) {
        }
        if (perMonitor)DpiMode = "per-monitor-v2";
        else
            try {
                if (Native.SetProcessDPIAware())DpiMode = "system";
            } catch (Exception) {
            }
        try {
            using (var g = Graphics.FromHwnd(IntPtr.Zero))StartupDpiScale = g.DpiX / 96f;
        } catch (Exception) {
        }
        Size screenSize;
        try {
            screenSize = Screen.PrimaryScreen.Bounds.Size;
        } catch (Exception) {
            screenSize = new Size(1920, 1080);
        }
        Skin.InitializeScale(ComputeScale(StartupDpiScale, screenSize));
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        if (args.Length >= 2 && args[0] == "--self-test") {
            Tests.Run(args[1]);
            return;
        }
        if (args.Length >= 2 && (args[0] == "--render" || args[0] == "--ui-test")) {
            using (var form = new MainForm(true)) {
                if (args[0] == "--render")form.Render(args[1]);
                else
                    try {
                        form.Verify(args[1]);
                    } catch (Exception e) {
                        // As --self-test does: a failing check leaves a FAIL line and a non-zero exit code instead of
                        // an unhandled exception that ends the process without any output.
                        File.WriteAllText(args[1], "FAIL: " + e);
                        Environment.ExitCode = 1;
                    }
            }
            return;
        }
        bool created;
        using (var mutex = new Mutex(true, "Local\\autocat.xyz-single-instance", out created)) {
            if (!created) {
                try {
                    using (var signal = EventWaitHandle.OpenExisting("Local\\autocat.xyz-show-menu"))
                        signal.Set();
                } catch (WaitHandleCannotBeOpenedException) {
                    MessageBox.Show("Unload the older autocat version before opening this update.");
                }
                return;
            }
            DiagnosticSession.Start(args);
            try {
                using (var menu = new MainForm())Application.Run(menu);
            } catch (Exception e) {
                Diag.Fault("Session", "startup_or_loop.failed", e);
                throw;
            } finally {
                DiagnosticSession.Stop();
            }
        }
    }

    // Resolution and DPI/scaling are independent in Windows: a 4K display at 100% scaling still
    // reports a normal 96 DPI. 1920x1080 is the "1x" reference; whichever signal asks for more
    // scale wins, so raised Windows scaling and a high-res display don't stack and double-scale.
    internal static string DpiMode = "unaware";
    internal static float StartupDpiScale = 1f;

    internal static float ComputeScale(float dpiScale, Size screenPixels) {
        float resolutionScale = Math.Min(screenPixels.Width / 1920f, screenPixels.Height / 1080f);
        return Math.Max(1f, Math.Min(3f, Math.Max(dpiScale, resolutionScale)));
    }
}
