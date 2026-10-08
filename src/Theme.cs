using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace AutoCat {
static class Skin {
    public static readonly Color Background = Color.FromArgb(15, 16, 19), Panel = Color.FromArgb(22, 23, 27),
        Border = Color.FromArgb(58, 58, 64), Text = Color.FromArgb(202, 201, 206),
        Muted = Color.FromArgb(128, 126, 135);

    // The "auto" half of the wordmark, brighter than the body text.
    public static readonly Color LogoText = Color.FromArgb(238, 235, 232);

    public static readonly Color DefaultAccent = Color.FromArgb(224, 161, 137);
    public static Color Accent = DefaultAccent;

    public static Font Font = new Font("Tahoma", 11, FontStyle.Regular, GraphicsUnit.Pixel),
        Logo = new Font("Consolas", 18, FontStyle.Bold, GraphicsUnit.Pixel);

    public static float Scale = 1f;

    // Called before any control exists, so text metrics already match the scale Form.Scale applies later.
    public static void InitializeScale(float scale) {
        Scale = scale;
        Font = new Font("Tahoma", 11 * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        Logo = new Font("Consolas", 18 * scale, FontStyle.Bold, GraphicsUnit.Pixel);
    }

    public static int D(Graphics g, float value) { return D(value); }
    public static int D(float value) { return (int)Math.Round(value * Scale); }

    public static void TextAt(Graphics g, string text, Rectangle rect, Color color,
            TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter) {
        TextRenderer.DrawText(g, text, Font, rect, color,
            flags | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
    }

    // Two runs so each half has its own color; the second starts where the first one ends, so the word keeps
    // the spacing of a single run at every scale. The word is centered across the block and starts at its top.
    public static void DrawLogo(Graphics g, Rectangle block, Color first, Color second) {
        const TextFormatFlags flags = TextFormatFlags.NoPadding;
        int whole = TextRenderer.MeasureText(g, "autocat", Logo, Size.Empty, flags).Width;
        var at = new Point(block.Left + (block.Width - whole) / 2, block.Top);
        TextRenderer.DrawText(g, "auto", Logo, at, first, flags);
        int width = TextRenderer.MeasureText(g, "auto", Logo, Size.Empty, flags).Width;
        TextRenderer.DrawText(g, "cat", Logo, new Point(at.X + width, at.Y), second, flags);
    }

    // The dark context menu shared by the footer and the notification area icon.
    public static ContextMenuStrip DarkMenu() {
        return new ContextMenuStrip {
            Renderer = new DarkMenuRenderer(),
            ShowImageMargin = false,
            Font = Font,
            BackColor = Panel,
            ForeColor = Text
        };
    }

    public static string Hex(Color c) { return c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2"); }

    public static bool TryParseHex(string text, out Color color) {
        color = Accent;
        if (String.IsNullOrEmpty(text))return false;
        string t = text.Trim().TrimStart('#');
        if (t.Length != 6)return false;
        int v;
        if (!int.TryParse(t, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out v))return false;
        color = Color.FromArgb((v >> 16) & 255, (v >> 8) & 255, v & 255);
        return true;
    }

    public static Color HsvToRgb(float h, float s, float v) {
        h = ((h % 360) + 360) % 360;
        s = Math.Max(0, Math.Min(1, s));
        v = Math.Max(0, Math.Min(1, v));
        float c = v * s, x = c * (1 - Math.Abs((h / 60f) % 2 - 1)), m = v - c, r, gg, b;
        if (h < 60) {
            r = c;
            gg = x;
            b = 0;
        } else if (h < 120) {
            r = x;
            gg = c;
            b = 0;
        } else if (h < 180) {
            r = 0;
            gg = c;
            b = x;
        } else if (h < 240) {
            r = 0;
            gg = x;
            b = c;
        } else if (h < 300) {
            r = x;
            gg = 0;
            b = c;
        } else {
            r = c;
            gg = 0;
            b = x;
        }
        return Color.FromArgb(Clamp255((r + m) * 255), Clamp255((gg + m) * 255), Clamp255((b + m) * 255));
    }

    static int Clamp255(float v) { return Math.Max(0, Math.Min(255, (int)Math.Round(v))); }

    public static void RgbToHsv(Color c, out float h, out float s, out float v) {
        float r = c.R / 255f, g = c.G / 255f, b = c.B / 255f;
        float max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        v = max;
        float d = max - min;
        s = max <= 0 ? 0 : d / max;
        if (d <= 0.00001f)h = 0;
        else if (max == r)h = 60 * (((g - b) / d) % 6);
        else if (max == g)h = 60 * (((b - r) / d) + 2);
        else h = 60 * (((r - g) / d) + 4);
        if (h < 0)h += 360;
    }
}

sealed class ColorWheel : Control {
    public Action<Color> Changed;
    float hue, sat = 1, val = 1;
    Bitmap wheel;
    int cachedSize = -1;
    float cachedVal = -1;

    public ColorWheel() {
        Width = 112;
        Height = 112;
        TabStop = true;
        AccessibleRole = AccessibleRole.Graphic;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true);
    }

    public Color Value { get { return Skin.HsvToRgb(hue, sat, val); } }

    public void SetColor(Color c) {
        // A drag reports the wheel's own color back; keeping the exact hue and brightness keeps the image.
        if (c.ToArgb() == Value.ToArgb())return;
        float h, s, v;
        Skin.RgbToHsv(c, out h, out s, out v);
        hue = h;
        sat = s;
        val = Math.Max(0.05f, v);
        Invalidate();
    }

    protected override void Dispose(bool disposing) {
        if (disposing && wheel != null) {
            wheel.Dispose();
            wheel = null;
        }
        base.Dispose(disposing);
    }

    void EnsureWheel() {
        int size = Math.Min(Width, Height);
        if (wheel != null && cachedSize == size && cachedVal == val)return;
        var bmp = new Bitmap(size, size);
        float r = size / 2f;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++) {
                float dx = x + 0.5f - r, dy = y + 0.5f - r;
                float dist = (float)Math.Sqrt(dx * dx + dy * dy) / r;
                if (dist > 1) {
                    bmp.SetPixel(x, y, Color.Transparent);
                    continue;
                }
                float angle = (float)(Math.Atan2(dy, dx) * 180 / Math.PI);
                if (angle < 0)angle += 360;
                bmp.SetPixel(x, y, Skin.HsvToRgb(angle, Math.Min(1, dist), val));
            }
        if (wheel != null)wheel.Dispose();
        wheel = bmp;
        cachedSize = size;
        cachedVal = val;
    }

    protected override void OnPaint(PaintEventArgs e) {
        EnsureWheel();
        e.Graphics.DrawImage(wheel, 0, 0);
        float r = Math.Min(Width, Height) / 2f;
        float rad = (float)(hue * Math.PI / 180);
        float px = r + r * sat * (float)Math.Cos(rad), py = r + r * sat * (float)Math.Sin(rad);
        float outer = 6 * Skin.Scale, inner = 5 * Skin.Scale;
        using (var pen = new Pen(Color.Black, 3 * Skin.Scale))
            e.Graphics.DrawEllipse(pen, px - outer, py - outer, outer * 2, outer * 2);
        using (var pen = new Pen(Color.White, Skin.Scale))
            e.Graphics.DrawEllipse(pen, px - inner, py - inner, inner * 2, inner * 2);
        using (var p = new Pen(Focused ? Skin.Accent : Skin.Border))
            e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
    }

    void Pick(int x, int y) {
        float r = Math.Min(Width, Height) / 2f;
        float dx = x - r, dy = y - r;
        float dist = (float)Math.Sqrt(dx * dx + dy * dy) / r;
        float angle = (float)(Math.Atan2(dy, dx) * 180 / Math.PI);
        if (angle < 0)angle += 360;
        hue = angle;
        sat = Math.Max(0, Math.Min(1, dist));
        Invalidate();
        if (Changed != null)Changed(Value);
    }

    protected override void OnMouseDown(MouseEventArgs e) {
        if (e.Button == MouseButtons.Left) {
            Focus();
            Capture = true;
            Pick(e.X, e.Y);
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e) {
        if (e.Button == MouseButtons.Left)Pick(e.X, e.Y);
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e) {
        Capture = false;
        base.OnMouseUp(e);
    }

    protected override bool IsInputKey(Keys key) {
        return key == Keys.Left || key == Keys.Right || key == Keys.Up || key == Keys.Down ||
            base.IsInputKey(key);
    }

    protected override void OnKeyDown(KeyEventArgs e) {
        if (e.KeyCode == Keys.Left)hue -= 2;
        else if (e.KeyCode == Keys.Right)hue += 2;
        else if (e.KeyCode == Keys.Up)sat = Math.Min(1, sat + 0.02f);
        else if (e.KeyCode == Keys.Down)sat = Math.Max(0, sat - 0.02f);
        else {
            base.OnKeyDown(e);
            return;
        }
        hue = ((hue % 360) + 360) % 360;
        e.Handled = true;
        Invalidate();
        if (Changed != null)Changed(Value);
    }
}

sealed class FieldGroup : Panel {
    public FieldGroup(string title, int x, int y, int width, int height) {
        Text = title;
        SetBounds(x, y, width, height);
        BackColor = Skin.Panel;
        DoubleBuffered = true;
    }

    protected override void OnPaint(PaintEventArgs e) {
        base.OnPaint(e);
        var g = e.Graphics;
        int y = Skin.D(g, 7), x = Skin.D(g, 11);
        using (var pen = new Pen(Color.Black))g.DrawRectangle(pen, 0, y, Width - 1, Height - y - 1);
        using (var pen = new Pen(Skin.Border))g.DrawRectangle(pen, 1, y + 1, Width - 3, Height - y - 3);
        int width = TextRenderer.MeasureText(Text, Skin.Font).Width + Skin.D(g, 6);
        using (var brush = new SolidBrush(BackColor))
            g.FillRectangle(brush, x, y - Skin.D(g, 6), width, Skin.D(g, 14));
        Skin.TextAt(g, Text, new Rectangle(x + Skin.D(g, 3), 0, width, Skin.D(g, 15)), Skin.Text);
    }
}

class MicroButton : Control {
    bool hover, pressed;
    public Action Action;

    public MicroButton(string text, Action action) {
        Text = text;
        Action = action;
        Height = 23;
        Font = Skin.Font;
        TabStop = true;
        AccessibleRole = AccessibleRole.PushButton;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true);
        SetStyle(ControlStyles.StandardClick | ControlStyles.StandardDoubleClick, false);
    }

    protected override void OnMouseEnter(EventArgs e) {
        hover = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e) {
        hover = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e) {
        if (e.Button == MouseButtons.Left) {
            Focus();
            pressed = true;
            Capture = true;
            Invalidate();
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e) {
        bool click = pressed && ClientRectangle.Contains(e.Location);
        pressed = false;
        Capture = false;
        Invalidate();
        if (click)OnClick(EventArgs.Empty);
        base.OnMouseUp(e);
    }

    protected override void OnClick(EventArgs e) {
        if (Enabled && Action != null)Action();
        base.OnClick(e);
    }

    protected override void OnKeyDown(KeyEventArgs e) {
        if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) {
            OnClick(EventArgs.Empty);
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e) {
        Color top = pressed ? Color.FromArgb(32, 30, 35) : hover ? Color.FromArgb(54, 47, 44) :
            Color.FromArgb(44, 44, 49);
        using (var b = new LinearGradientBrush(ClientRectangle, top, Color.FromArgb(29, 29, 33), 90))
            e.Graphics.FillRectangle(b, ClientRectangle);
        using (var p = new Pen(Focused ? Skin.Accent : Skin.Border))
            e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
        Skin.TextAt(e.Graphics, Text, new Rectangle(5, 0, Width - 10, Height),
            Enabled ? Skin.Text : Skin.Muted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}

// One line of accent text in the footer. It is underlined while hovered or focused; Enter and Space activate it.
sealed class NoticeLink : Label {
    bool hover, pressed;
    public Action Action;

    public NoticeLink(Action action) {
        Action = action;
        AutoSize = false;
        UseMnemonic = false;
        TabStop = true;
        Cursor = Cursors.Hand;
        ForeColor = Skin.Accent;
        AccessibleRole = AccessibleRole.Link;
        SetStyle(ControlStyles.Selectable, true);
    }

    protected override void OnMouseEnter(EventArgs e) {
        hover = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e) {
        hover = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e) {
        if (e.Button == MouseButtons.Left)Focus();
        base.OnMouseDown(e);
    }

    protected override void OnGotFocus(EventArgs e) {
        Invalidate();
        base.OnGotFocus(e);
    }

    protected override void OnLostFocus(EventArgs e) {
        pressed = false;
        Invalidate();
        base.OnLostFocus(e);
    }

    protected override void OnClick(EventArgs e) {
        if (Enabled && Action != null)Action();
        base.OnClick(e);
    }

    // A held key repeats; it acts once, on the first press.
    protected override void OnKeyDown(KeyEventArgs e) {
        if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) {
            if (!pressed) {
                pressed = true;
                OnClick(EventArgs.Empty);
            }
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    protected override void OnKeyUp(KeyEventArgs e) {
        if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter)pressed = false;
        base.OnKeyUp(e);
    }

    protected override void OnPaint(PaintEventArgs e) {
        base.OnPaint(e);
        if (!hover && !Focused)return;
        int width = TextRenderer.MeasureText(e.Graphics, Text, Font, Size.Empty, TextFormatFlags.NoPadding).Width;
        using (var p = new Pen(ForeColor, Math.Max(1, Skin.D(1))))
            e.Graphics.DrawLine(p, 0, Font.Height, Math.Min(width, Width) - 1, Font.Height);
    }
}

sealed class MicroCheck : Control {
    readonly Func<bool> get;
    readonly Action<bool> set;

    public MicroCheck(string text, Func<bool> getter, Action<bool> setter) {
        Text = text;
        get = getter;
        set = setter;
        Height = 20;
        TabStop = true;
        AccessibleRole = AccessibleRole.CheckButton;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true);
    }

    protected override void OnMouseDown(MouseEventArgs e) {
        if (e.Button == MouseButtons.Left) {
            Focus();
            set(!get());
            Invalidate();
        }
        base.OnMouseDown(e);
    }

    protected override void OnKeyDown(KeyEventArgs e) {
        if (e.KeyCode == Keys.Space) {
            set(!get());
            Invalidate();
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics;
        int size = Skin.D(g, 10), y = (Height - size) / 2;
        using (var b = new SolidBrush(Color.FromArgb(29, 29, 33)))g.FillRectangle(b, 0, y, size, size);
        using (var p = new Pen(Focused ? Skin.Accent : Skin.Border))g.DrawRectangle(p, 0, y, size, size);
        if (get())
            using (var b = new LinearGradientBrush(new Rectangle(2, y + 2, size - 3, size - 3),
                    Color.FromArgb(243, 201, 180), Skin.Accent, 90))
                g.FillRectangle(b, 2, y + 2, size - 3, size - 3);
        Skin.TextAt(g, Text, new Rectangle(Skin.D(g, 18), 0, Width - Skin.D(g, 18), Height), Skin.Text);
    }
}

sealed class MicroSlider : Control {
    public Func<string> Display;
    public Action EditValue;
    readonly Func<double> get;
    readonly Action<double> set;
    readonly double min, max, step;
    readonly string format, suffix;
    bool dragging;

    public MicroSlider(string text, double min, double max, double step, string format, string suffix,
            Func<double> getter, Action<double> setter) {
        Text = text;
        this.min = min;
        this.max = max;
        this.step = step;
        this.format = format;
        this.suffix = suffix;
        get = getter;
        set = setter;
        Height = 36;
        TabStop = true;
        AccessibleRole = AccessibleRole.Slider;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true);
    }

    void Set(double value) {
        set(Math.Max(min, Math.Min(max, Math.Round(value / step) * step)));
        Invalidate();
    }

    void Position(int x) {
        int pad = (int)Math.Round(12 * DeviceScale());
        Set(min + (max - min) * Math.Max(0, Math.Min(1, (x - pad) / (double)Math.Max(1, Width - 2 * pad))));
    }

    float DeviceScale() { return Skin.Scale; }

    protected override void OnMouseDown(MouseEventArgs e) {
        if (e.Button == MouseButtons.Left) {
            if (EditValue != null && e.Y < Skin.D(20) && e.X >= Width - Skin.D(170)) {
                EditValue();
                return;
            }
            Focus();
            int pad = (int)(12 * DeviceScale());
            if (e.X < pad)Set(get() - step);
            else if (e.X >= Width - pad)Set(get() + step);
            else {
                dragging = true;
                Capture = true;
                Position(e.X);
            }
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e) {
        if (dragging)Position(e.X);
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e) {
        dragging = false;
        Capture = false;
        base.OnMouseUp(e);
    }

    protected override bool IsInputKey(Keys key) {
        return key == Keys.Left || key == Keys.Right || base.IsInputKey(key);
    }

    protected override void OnKeyDown(KeyEventArgs e) {
        if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Right) {
            Set(get() + (e.KeyCode == Keys.Right ? step : -step));
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics;
        int pad = Skin.D(g, 12), barY = Skin.D(g, 24), barH = Math.Max(3, Skin.D(g, 4));
        Skin.TextAt(g, Text, new Rectangle(pad, 0, Width - pad * 2, Skin.D(g, 19)), Skin.Text);
        Skin.TextAt(g, Display == null ? get().ToString(format) + suffix : Display(),
            new Rectangle(pad, 0, Width - pad * 2, Skin.D(g, 19)), Skin.Muted,
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        using (var b = new SolidBrush(Color.FromArgb(12, 12, 15)))
            g.FillRectangle(b, pad, barY, Width - pad * 2, barH);
        using (var b = new SolidBrush(Skin.Accent))
            g.FillRectangle(b, pad + 1, barY + 1,
                (int)((Width - pad * 2 - 2) * Math.Max(0, Math.Min(1, (get() - min) / (max - min)))),
                Math.Max(1, barH - 2));
        using (var p = new Pen(Focused ? Skin.Accent : Skin.Border))
            g.DrawRectangle(p, pad, barY, Width - pad * 2, barH);
        Skin.TextAt(g, "-", new Rectangle(0, barY - Skin.D(g, 6), pad, Skin.D(g, 16)), Skin.Muted);
        Skin.TextAt(g, "+", new Rectangle(Width - pad, barY - Skin.D(g, 6), pad, Skin.D(g, 16)), Skin.Muted,
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
    }
}

sealed class DarkMenuColors : ProfessionalColorTable {
    static Color Hover {
        get {
            return Color.FromArgb((Skin.Panel.R * 4 + Skin.Accent.R) / 5,
                (Skin.Panel.G * 4 + Skin.Accent.G) / 5, (Skin.Panel.B * 4 + Skin.Accent.B) / 5);
        }
    }

    public override Color ToolStripDropDownBackground { get { return Skin.Panel; } }
    public override Color ImageMarginGradientBegin { get { return Skin.Panel; } }
    public override Color ImageMarginGradientMiddle { get { return Skin.Panel; } }
    public override Color ImageMarginGradientEnd { get { return Skin.Panel; } }
    public override Color MenuBorder { get { return Skin.Border; } }
    public override Color MenuItemBorder { get { return Hover; } }
    public override Color MenuItemSelected { get { return Hover; } }
    public override Color MenuItemSelectedGradientBegin { get { return Hover; } }
    public override Color MenuItemSelectedGradientEnd { get { return Hover; } }
    public override Color MenuItemPressedGradientBegin { get { return Hover; } }
    public override Color MenuItemPressedGradientEnd { get { return Hover; } }
}

sealed class DarkMenuRenderer : ToolStripProfessionalRenderer {
    public DarkMenuRenderer() : base(new DarkMenuColors()) { RoundedEdges = false; }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e) {
        e.TextColor = e.Item.Selected ? Color.White : Skin.Text;
        base.OnRenderItemText(e);
    }
}

sealed class MenuTabs : Control {
    public readonly string[] Tabs = {"auto clicker", "automation", "experimental", "theme", "misc"};
    public int Selected;
    public Action<int> Change;

    public MenuTabs() {
        Height = 40;
        TabStop = true;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true);
    }

    // Uniform columns clip "experimental" at 5 tabs; size each to its own label, then stretch to fill Width.
    int[] ColumnWidths() {
        var widths = new int[Tabs.Length];
        int total = 0, pad = Skin.D(16);
        for (int i = 0; i < Tabs.Length; i++) {
            widths[i] = TextRenderer.MeasureText(Tabs[i], Skin.Font, Size.Empty,
                TextFormatFlags.NoPadding).Width + pad;
            total += widths[i];
        }
        if (total > 0 && Width > 0) {
            int assigned = 0;
            for (int i = 0; i < widths.Length; i++) {
                int share = i == widths.Length - 1 ? Width - assigned :
                    (int)Math.Round(widths[i] * (Width / (double)total));
                assigned += share;
                widths[i] = share;
            }
        }
        return widths;
    }

    public void Select(int index) {
        Selected = index;
        Invalidate();
        if (Change != null)Change(index);
    }

    protected override void OnMouseDown(MouseEventArgs e) {
        if (e.Button == MouseButtons.Left) {
            Focus();
            var widths = ColumnWidths();
            int x = 0;
            for (int i = 0; i < widths.Length; i++) {
                x += widths[i];
                if (e.X < x) {
                    Select(i);
                    break;
                }
            }
        }
        base.OnMouseDown(e);
    }

    protected override bool IsInputKey(Keys key) {
        return key == Keys.Left || key == Keys.Right || base.IsInputKey(key);
    }

    protected override void OnKeyDown(KeyEventArgs e) {
        if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Right) {
            Select((Selected + (e.KeyCode == Keys.Right ? 1 : Tabs.Length - 1)) % Tabs.Length);
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e) {
        var widths = ColumnWidths();
        int x = 0;
        for (int i = 0; i < Tabs.Length; i++) {
            var r = new Rectangle(x, 0, widths[i], Height - 5);
            Skin.TextAt(e.Graphics, Tabs[i], r, i == Selected ? Color.White : Skin.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            using (var p = new Pen(i == Selected ? Skin.Accent : Skin.Border, 2))
                e.Graphics.DrawLine(p, r.Left + 3, Height - 5, r.Right - 3, Height - 5);
            x += widths[i];
        }
    }
}
}
