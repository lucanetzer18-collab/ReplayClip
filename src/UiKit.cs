using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ReplayClip
{
    /// <summary>Markiert selbst gezeichnete Controls (werden von Theme.Apply nicht umgefärbt).</summary>
    public interface IFxControl { }

    // ======================= Animation =======================

    /// <summary>
    /// Gemeinsamer Animationstakt, an die Bildwiederholrate gekoppelt (DwmFlush = wartet auf den nächsten
    /// Bildaufbau von Windows). Dadurch kommt jede Animation genau einmal pro Monitorbild – kein Ruckeln durch
    /// ungleichmäßige Timer-Abstände. Läuft nur, solange etwas animiert.
    /// </summary>
    public static class Anim
    {
        static readonly List<Func<bool>> jobs = new List<Func<bool>>();
        static readonly Stopwatch sw = Stopwatch.StartNew();
        static readonly System.Threading.AutoResetEvent wake = new System.Threading.AutoResetEvent(false);
        static System.Threading.Thread thread;
        static Control marshal;
        static volatile bool active;
        static int posted;
        static double last, frameStamp;
        static bool dwmOk = true;
        static Action tickAction;
        public static float Dt = 0.016f;
        public static double Now { get { return sw.Elapsed.TotalSeconds; } }
        public static bool Enabled { get { return Theme.Animations < 2; } }
        /// <summary>Reduziert = schneller.</summary>
        public static float Factor { get { return Theme.Animations == 1 ? 2.2f : 1f; } }

        [DllImport("dwmapi.dll")] static extern int DwmFlush();
        [DllImport("user32.dll")] static extern bool RedrawWindow(IntPtr hWnd, IntPtr rect, IntPtr rgn, uint flags);
        static bool redrawOk = true;

        public static void Run(Func<bool> step)
        {
            jobs.Add(step);
            if (marshal == null)
            {
                marshal = new Control();
                var h = marshal.Handle; // Handle auf dem UI-Thread erzeugen
                tickAction = Tick;
                try { Native.timeBeginPeriod(1); } catch { }
                thread = new System.Threading.Thread(Loop) { IsBackground = true, Name = "Anim", Priority = System.Threading.ThreadPriority.AboveNormal };
                thread.Start();
            }
            if (!active) { last = Now; active = true; wake.Set(); }
        }

        /// <summary>Hintergrund-Thread: wartet auf den Bildaufbau und stößt dann einen Schritt auf dem UI-Thread an.</summary>
        static void Loop()
        {
            double prev = Now;
            while (true)
            {
                if (!active) { wake.WaitOne(); prev = Now; continue; }
                bool waited = false;
                if (dwmOk)
                {
                    try
                    {
                        double t0 = Now;
                        waited = DwmFlush() == 0 && Now - t0 > 0.0008;
                    }
                    catch { dwmOk = false; }
                }
                if (!waited)
                {
                    // Kein DWM-Takt (oder Fenster verdeckt): selbst auf ~60 Hz takten.
                    double next = prev + 1 / 60.0;
                    while (Now < next - 0.0015) System.Threading.Thread.Sleep(1);
                    while (Now < next) System.Threading.Thread.SpinWait(50);
                }
                prev = Now;
                frameStamp = prev;
                // Nur einen Schritt gleichzeitig in der Warteschlange: ist der UI-Thread beschäftigt, wird ein Bild ausgelassen statt aufgestaut.
                if (System.Threading.Interlocked.CompareExchange(ref posted, 1, 0) == 0)
                {
                    try { marshal.BeginInvoke(tickAction); }
                    catch { posted = 0; System.Threading.Thread.Sleep(15); }
                }
            }
        }

        static void Tick()
        {
            try { TickCore(); }
            finally { posted = 0; } // erst jetzt den nächsten Schritt erlauben – kein Stau, wenn ein Bild länger dauert
        }

        static void TickCore()
        {
            double n = frameStamp > 0 ? frameStamp : Now;
            if (n <= last) n = Now;
            Dt = (float)Math.Min(0.1, Math.Max(0.001, n - last));
            last = n;
            var copy = jobs.ToArray();
            foreach (var j in copy)
            {
                bool keep;
                try { keep = j(); } catch { keep = false; }
                if (!keep) jobs.Remove(j);
            }
            // Alles, was in diesem Schritt ungültig wurde, sofort zeichnen – in JEDEM Fenster.
            // Sonst gewinnt bei Windows immer dasselbe Fenster (z.B. die Bibliothek beim Hovern) und
            // Kopfleiste/Pegel/Pufferbalken bleiben stehen, bis die Maus ruht.
            if (redrawOk)
            {
                try
                {
                    var forms = new List<Form>();
                    foreach (Form f in Application.OpenForms) forms.Add(f);
                    foreach (var f in forms)
                        if (f.Visible && f.IsHandleCreated && f.WindowState != FormWindowState.Minimized)
                            RedrawWindow(f.Handle, IntPtr.Zero, IntPtr.Zero, 0x0100 /*UPDATENOW*/ | 0x0080 /*ALLCHILDREN*/);
                }
                catch { redrawOk = false; }
            }
            if (jobs.Count == 0) active = false;
        }

        /// <summary>Wert in 'seconds' von 0 nach 1 laufen lassen (mit Easing).</summary>
        public static void Over(Control owner, float seconds, Action<float> step, Action done = null)
        {
            if (!Enabled || seconds <= 0) { step(1f); if (done != null) done(); return; }
            double start = Now;
            Run(() =>
            {
                if (owner.IsDisposed) return false;
                float t = (float)Math.Min(1, (Now - start) * Factor / seconds);
                step(EaseOut(t));
                if (t >= 1) { if (done != null) done(); return false; }
                return true;
            });
        }

        public static float EaseOut(float t) { t = 1 - t; return 1 - t * t * t; }
        public static float EaseInOut(float t) { return t < 0.5f ? 4 * t * t * t : 1 - (float)Math.Pow(-2 * t + 2, 3) / 2; }
    }

    /// <summary>
    /// Ersatz für den WinForms-Timer: WM_TIMER hat bei Windows die niedrigste Priorität und fällt aus, solange
    /// andere Animationen laufen (z.B. Hover in der Bibliothek) – Pegel/Puffer/Statuszeile blieben dann stehen.
    /// Dieser Timer tickt im Hintergrund und reiht den Aufruf normal in die Nachrichtenschlange ein.
    /// </summary>
    public sealed class PostTimer
    {
        readonly Control owner;
        System.Threading.Timer t;
        int posted;
        public int Interval = 100;
        public event EventHandler Tick;
        public PostTimer(Control owner) { this.owner = owner; }

        public void Start()
        {
            Stop();
            t = new System.Threading.Timer(_ =>
            {
                if (System.Threading.Interlocked.CompareExchange(ref posted, 1, 0) != 0) return;
                try
                {
                    if (owner.IsDisposed || !owner.IsHandleCreated) { posted = 0; return; }
                    owner.BeginInvoke(new Action(() =>
                    {
                        posted = 0;
                        if (t == null) return;
                        var h = Tick;
                        if (h != null) h(this, EventArgs.Empty);
                    }));
                }
                catch { posted = 0; }
            }, null, Interval, Interval);
        }

        public void Stop()
        {
            var x = t;
            t = null;
            if (x != null) x.Dispose();
        }
    }

    /// <summary>Weicher Übergang eines Werts zu einem Ziel (exponentiell).</summary>
    public sealed class Tween
    {
        public float Value, Target;
        public float Rate;
        readonly Control owner;
        bool running;
        public Action Changed;

        public Tween(Control owner, float initial, float rate)
        {
            this.owner = owner; Value = Target = initial; Rate = rate;
        }

        public void To(float t)
        {
            Target = t;
            if (!Anim.Enabled || owner == null || !owner.IsHandleCreated || !owner.Visible) { Value = t; Fire(); return; }
            if (!running && Math.Abs(Value - t) > 0.0005f) { running = true; Anim.Run(Step); }
        }

        public void Set(float v) { Value = Target = v; Fire(); }

        bool Step()
        {
            if (owner.IsDisposed) { running = false; return false; }
            float k = 1f - (float)Math.Exp(-Rate * Anim.Factor * Anim.Dt);
            Value += (Target - Value) * k;
            bool done = Math.Abs(Target - Value) < 0.002f;
            if (done) Value = Target;
            Fire();
            if (done) running = false;
            return !done;
        }

        void Fire()
        {
            if (Changed != null) Changed();
            else if (owner != null && !owner.IsDisposed) owner.Invalidate();
        }
    }

    // ======================= Zeichenhilfen =======================

    public static class Gfx
    {
        public static GraphicsPath Round(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            rad = Math.Max(0.5f, Math.Min(rad, Math.Min(r.Width, r.Height) / 2f));
            float d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static Color Lerp(Color a, Color b, float t)
        {
            t = Math.Max(0, Math.Min(1, t));
            return Color.FromArgb(
                (int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        public static Color Alpha(Color c, int a) { return Color.FromArgb(Math.Max(0, Math.Min(255, a)), c.R, c.G, c.B); }
        public static Color Light(Color c, float t) { return Lerp(c, Color.White, t); }
        public static float Lum(Color c) { return (0.299f * c.R + 0.587f * c.G + 0.114f * c.B) / 255f; }

        public static void FillRound(Graphics g, Color c, RectangleF r, float rad)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            using (var p = Round(r, rad))
            using (var b = new SolidBrush(c)) g.FillPath(b, p);
        }

        public static void FillRound(Graphics g, Brush b, RectangleF r, float rad)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            using (var p = Round(r, rad)) g.FillPath(b, p);
        }

        public static void DrawRound(Graphics g, Color c, RectangleF r, float rad, float width)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            using (var p = Round(r, rad))
            using (var pen = new Pen(c, width)) g.DrawPath(pen, p);
        }

        public static LinearGradientBrush Gradient(RectangleF r, Color a, Color b, float angle)
        {
            var rr = r;
            if (rr.Width < 1) rr.Width = 1;
            if (rr.Height < 1) rr.Height = 1;
            return new LinearGradientBrush(rr, a, b, angle);
        }

        /// <summary>Weicher Schatten/Glow aus mehreren halbtransparenten Ringen.</summary>
        public static void Glow(Graphics g, RectangleF r, float rad, Color c, int size, int alpha)
        {
            for (int i = size; i >= 1; i--)
            {
                var rr = r;
                rr.Inflate(i, i);
                int a = (int)(alpha * (1f - i / (float)(size + 1)) / size * 2.2f);
                FillRound(g, Alpha(c, a), rr, rad + i);
            }
        }

        /// <summary>Bild mit runden Ecken (Ecken werden mit der Hintergrundfarbe übermalt).</summary>
        public static void RoundedImage(Graphics g, Image img, Rectangle r, float rad, Color bg, bool bottom = true)
        {
            if (img != null) g.DrawImage(img, r);
            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var b = new SolidBrush(bg))
            {
                float x = r.X - 0.5f, y = r.Y - 0.5f, w = r.Width + 1, h = r.Height + 1, d = rad * 2;
                using (var p = new GraphicsPath())
                {
                    p.AddLine(x, y, x + rad, y); p.AddArc(x, y, d, d, 270, -90); p.CloseFigure();
                    p.AddLine(x + w, y, x + w, y + rad); p.AddArc(x + w - d, y, d, d, 0, -90); p.CloseFigure();
                    if (bottom)
                    {
                        p.AddLine(x + w, y + h, x + w - rad, y + h); p.AddArc(x + w - d, y + h - d, d, d, 90, -90); p.CloseFigure();
                        p.AddLine(x, y + h, x, y + h - rad); p.AddArc(x, y + h - d, d, d, 180, -90); p.CloseFigure();
                    }
                    g.FillPath(b, p);
                }
            }
            g.SmoothingMode = old;
        }

        public static void Chevron(Graphics g, PointF c, float size, Color col, float angleDeg, float width)
        {
            var st = g.Save();
            g.TranslateTransform(c.X, c.Y);
            g.RotateTransform(angleDeg);
            using (var p = new Pen(col, width))
            {
                p.StartCap = p.EndCap = LineCap.Round;
                p.LineJoin = LineJoin.Round;
                g.DrawLines(p, new[] { new PointF(-size, -size / 2), new PointF(0, size / 2), new PointF(size, -size / 2) });
            }
            g.Restore(st);
        }

        public static void Check(Graphics g, RectangleF r, Color c, float width)
        {
            using (var p = new Pen(c, width))
            {
                p.StartCap = p.EndCap = LineCap.Round;
                p.LineJoin = LineJoin.Round;
                g.DrawLines(p, new[]
                {
                    new PointF(r.X + r.Width * 0.18f, r.Y + r.Height * 0.52f),
                    new PointF(r.X + r.Width * 0.42f, r.Y + r.Height * 0.76f),
                    new PointF(r.X + r.Width * 0.84f, r.Y + r.Height * 0.28f)
                });
            }
        }

        public const TextFormatFlags Center = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
        public const TextFormatFlags Left = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;

        public static void Icon(Graphics g, string name, float size, Rectangle r, Color c)
        {
            TextRenderer.DrawText(g, Theme.Icon(name), Theme.IconFont(size), r, c, Center);
        }

        public static Size Measure(string s, Font f)
        {
            if (string.IsNullOrEmpty(s)) return new Size(0, f.Height);
            return TextRenderer.MeasureText(s, f, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        }
    }

    // ======================= Button =======================

    public enum FxKind { Normal, Primary, Ghost, Danger, Chip }

    /// <summary>Button mit Hover-/Klick-Animation, Ripple-Effekt, Icon und optionalem Hinweis (z.B. Tastenkürzel).</summary>
    public class FxButton : Button, IFxControl
    {
        FxKind kind = FxKind.Normal;
        string icon, hint;
        bool toggled;
        public Color? Tint;
        public bool LeftAlign;
        public float IconSize = 10.5f;
        public float Radius = 7;
        readonly Tween hover, press, tog;
        float ripple = -1;
        PointF ripplePt;

        public FxKind Kind { get { return kind; } set { kind = value; Invalidate(); } }
        public string IconName { get { return icon; } set { icon = value; if (AutoSize) PerformLayoutSafe(); Invalidate(); } }
        public string Hint { get { return hint; } set { hint = value; if (AutoSize) PerformLayoutSafe(); Invalidate(); } }
        public bool Toggled { get { return toggled; } set { if (toggled == value) return; toggled = value; tog.To(value ? 1 : 0); } }

        void PerformLayoutSafe() { if (Parent != null) Parent.PerformLayout(); }

        public FxButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            FlatStyle = FlatStyle.Flat;
            UseVisualStyleBackColor = false;
            Cursor = Cursors.Hand;
            Font = Theme.Medium(9f);
            hover = new Tween(this, 0, 16);
            press = new Tween(this, 0, 24);
            tog = new Tween(this, 0, 14);
            Height = Theme.S(32);
            Margin = new Padding(Theme.S(3));
            Padding = new Padding(Theme.S(12), 0, Theme.S(12), 0);
        }

        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); if (Enabled) hover.To(1); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover.To(0); press.To(0); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); if (!Enabled) { hover.Set(0); press.Set(0); } Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || !Enabled) return;
            press.To(1);
            if (Anim.Enabled)
            {
                ripplePt = e.Location;
                bool start = ripple < 0;
                ripple = 0;
                if (start)
                    Anim.Run(() =>
                    {
                        if (IsDisposed) return false;
                        ripple += Anim.Dt * Anim.Factor / 0.5f;
                        Invalidate();
                        if (ripple >= 1) { ripple = -1; return false; }
                        return true;
                    });
            }
        }

        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); press.To(0); }

        public override Size GetPreferredSize(Size proposed)
        {
            int w = Padding.Horizontal;
            var ts = Gfx.Measure(Text, Font);
            if (!string.IsNullOrEmpty(icon)) w += Gfx.Measure(Theme.Icon(icon), Theme.IconFont(IconSize)).Width + (string.IsNullOrEmpty(Text) ? 0 : Theme.S(7));
            w += ts.Width;
            if (!string.IsNullOrEmpty(hint)) w += Theme.S(10) + Gfx.Measure(hint, Theme.F(8f)).Width;
            int h = Math.Max(Theme.S(32), ts.Height + Theme.S(14));
            if (string.IsNullOrEmpty(Text) && !string.IsNullOrEmpty(icon)) w = Math.Max(w, h);
            return new Size(w, h);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            bool painted = false;
            if (Parent is HeaderBar)
            {
                // Verlauf der Kopfzeile durchscheinen lassen
                try { ButtonRenderer.DrawParentBackground(g, ClientRectangle, this); painted = true; } catch { }
            }
            if (!painted) g.Clear(Theme.EffectiveBack(Parent));
            float h = hover.Value, p = press.Value, t = tog.Value;
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            if (p > 0) { float d = p * 1.3f * Theme.Dpi; r.Inflate(-d, -d * 0.7f); }
            float rad = Radius * Theme.Dpi;

            Color bg, border, fg;
            Brush brush = null;
            switch (kind)
            {
                case FxKind.Primary:
                    {
                        var a = Gfx.Light(Theme.Accent, h * 0.12f);
                        var b = Gfx.Light(Gfx.Lerp(Theme.Accent, Theme.Secondary, 0.55f), h * 0.12f);
                        if (!Enabled) { a = Gfx.Lerp(Theme.Panel2, Theme.Accent, 0.25f); b = a; }
                        if (Enabled && h > 0.01f) Gfx.Glow(g, r, rad, Theme.Accent, Theme.S(4), (int)(70 * h));
                        brush = Gfx.Gradient(r, a, b, 0f);
                        bg = a;
                        border = Color.Transparent;
                        fg = Enabled ? Theme.OnAccent : Theme.TextDim;
                        break;
                    }
                case FxKind.Ghost:
                    bg = Gfx.Alpha(Theme.Hover, (int)(255 * Math.Max(h, p)));
                    border = Color.Transparent;
                    fg = Gfx.Lerp(Theme.TextDim, Theme.Text, Math.Max(h, t));
                    break;
                case FxKind.Danger:
                    bg = Gfx.Lerp(Theme.Panel2, Gfx.Lerp(Theme.Panel2, Theme.Danger, 0.35f), h);
                    border = Gfx.Lerp(Theme.Border, Theme.Danger, 0.4f + 0.4f * h);
                    fg = Gfx.Lerp(Theme.Danger, Color.White, h * 0.4f);
                    break;
                case FxKind.Chip:
                    {
                        var tint = Tint ?? Theme.Panel2;
                        bg = Gfx.Lerp(tint, Theme.Hover, h * 0.5f);
                        border = Color.Transparent;
                        fg = Theme.Text;
                        rad = Height / 2f;
                        break;
                    }
                default:
                    bg = Gfx.Lerp(Theme.Panel2, Theme.Hover, h);
                    border = Gfx.Lerp(Theme.Border, Gfx.Lerp(Theme.Border, Theme.Accent, 0.45f), h * 0.6f);
                    fg = Theme.Text;
                    break;
            }
            if (t > 0.001f && kind != FxKind.Primary)
            {
                bg = Gfx.Lerp(bg, Gfx.Lerp(Theme.Panel2, Theme.Accent, 0.26f + 0.08f * h), t);
                border = Gfx.Lerp(border, Theme.Accent, t);
                fg = Gfx.Lerp(fg, Color.White, t);
            }
            if (!Enabled && kind != FxKind.Primary)
            {
                bg = Gfx.Lerp(bg, Theme.Panel, 0.5f);
                border = Gfx.Lerp(border, Theme.Panel, 0.5f);
                fg = Gfx.Lerp(Theme.TextDim, Theme.Panel, 0.15f);
            }

            if (brush != null) { Gfx.FillRound(g, brush, r, rad); brush.Dispose(); }
            else if (bg.A > 0) Gfx.FillRound(g, bg, r, rad);
            if (border.A > 0) Gfx.DrawRound(g, border, r, rad, 1f);

            if (ripple >= 0)
            {
                using (var path = Gfx.Round(r, rad))
                {
                    var st = g.Save();
                    g.SetClip(path);
                    float maxR = (float)Math.Sqrt(Width * Width + Height * Height);
                    float rr = maxR * Anim.EaseOut(ripple);
                    using (var b = new SolidBrush(Color.FromArgb((int)(70 * (1 - ripple)), 255, 255, 255)))
                        g.FillEllipse(b, ripplePt.X - rr, ripplePt.Y - rr, rr * 2, rr * 2);
                    g.Restore(st);
                }
            }
            if (Focused && ShowFocusCues)
            {
                var fr = r; fr.Inflate(-2, -2);
                Gfx.DrawRound(g, Gfx.Alpha(Theme.Accent, 200), fr, rad - 1, 1.5f);
            }

            // Inhalt
            var iconFont = Theme.IconFont(IconSize);
            string ic = string.IsNullOrEmpty(icon) ? null : Theme.Icon(icon);
            var ts = Gfx.Measure(Text, Font);
            var isz = ic != null ? Gfx.Measure(ic, iconFont) : Size.Empty;
            var hf = Theme.F(8f);
            var hs = string.IsNullOrEmpty(hint) ? Size.Empty : Gfx.Measure(hint, hf);
            int gap = (ic != null && !string.IsNullOrEmpty(Text)) ? Theme.S(7) : 0;
            int total = isz.Width + gap + ts.Width + (hs.Width > 0 ? Theme.S(10) + hs.Width : 0);
            int x = LeftAlign ? Padding.Left : (Width - total) / 2;
            int cy = Height / 2;
            if (ic != null)
            {
                Color icCol = fg;
                TextRenderer.DrawText(g, ic, iconFont, new Rectangle(x, cy - isz.Height / 2 + (Theme.HasIcons ? 1 : 0), isz.Width, isz.Height), icCol, Gfx.Center);
                x += isz.Width + gap;
            }
            if (!string.IsNullOrEmpty(Text))
            {
                TextRenderer.DrawText(g, Text, Font, new Rectangle(x, cy - ts.Height / 2, ts.Width + 2, ts.Height), fg, Gfx.Left);
                x += ts.Width;
            }
            if (hs.Width > 0)
            {
                x += Theme.S(10);
                var hr = new RectangleF(x - Theme.S(4), cy - hs.Height / 2 - Theme.S(2), hs.Width + Theme.S(8), hs.Height + Theme.S(4));
                Gfx.FillRound(g, Gfx.Alpha(Color.Black, kind == FxKind.Primary ? 40 : 60), hr, Theme.S(4));
                TextRenderer.DrawText(g, hint, hf, new Rectangle(x, cy - hs.Height / 2, hs.Width + 2, hs.Height),
                    kind == FxKind.Primary ? Gfx.Alpha(Theme.OnAccent, 210) : Theme.TextDim, Gfx.Left);
            }
        }
    }

    // ======================= Schalter / Optionsfeld =======================

    /// <summary>Animierter Ein/Aus-Schalter (ersetzt CheckBox).</summary>
    public class FxToggle : CheckBox, IFxControl
    {
        readonly Tween t, hover;

        public FxToggle()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            t = new Tween(this, 0, 16);
            hover = new Tween(this, 0, 16);
            Cursor = Cursors.Hand;
            AutoSize = true;
            Font = Theme.F(9f);
            Margin = new Padding(Theme.S(3), Theme.S(4), Theme.S(3), Theme.S(4));
        }

        protected override void OnCheckedChanged(EventArgs e)
        {
            if (IsHandleCreated && Visible) t.To(Checked ? 1 : 0); else t.Set(Checked ? 1 : 0);
            base.OnCheckedChanged(e);
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); t.Set(Checked ? 1 : 0); }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover.To(1); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover.To(0); }
        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }

        int SwW { get { return Theme.S(36); } }
        int SwH { get { return Theme.S(20); } }

        public override Size GetPreferredSize(Size proposed)
        {
            int avail = MaximumSize.Width > 0 ? MaximumSize.Width - SwW - Theme.S(10) : int.MaxValue;
            var ts = string.IsNullOrEmpty(Text) ? Size.Empty
                : TextRenderer.MeasureText(Text, Font, new Size(avail, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            return new Size(SwW + (ts.Width > 0 ? Theme.S(10) + ts.Width + 2 : 0), Math.Max(SwH + Theme.S(4), ts.Height + Theme.S(4)));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.EffectiveBack(Parent));
            float v = t.Value, h = hover.Value;
            int y = (Height - SwH) / 2;
            if (!string.IsNullOrEmpty(Text) && Height > SwH + Theme.S(12)) y = Theme.S(2);
            var track = new RectangleF(0.5f, y + 0.5f, SwW - 1, SwH - 1);
            Color off = Gfx.Lerp(Theme.Panel2, Theme.Hover, h);
            Color on = Gfx.Light(Theme.Accent, h * 0.1f);
            if (v > 0.01f)
            {
                using (var br = Gfx.Gradient(track, Gfx.Lerp(off, on, v), Gfx.Lerp(off, Gfx.Lerp(on, Theme.Secondary, 0.4f), v), 0))
                    Gfx.FillRound(g, br, track, SwH / 2f);
            }
            else Gfx.FillRound(g, off, track, SwH / 2f);
            Gfx.DrawRound(g, Gfx.Lerp(Theme.Border, Gfx.Alpha(Theme.Accent, 0), v), track, SwH / 2f, 1f);
            float kd = SwH - Theme.S(6);
            float kx = track.X + Theme.S(3) + (SwW - Theme.S(6) - kd - 1) * v;
            var knob = new RectangleF(kx, y + Theme.S(3), kd, kd);
            using (var sh = new SolidBrush(Color.FromArgb(70, 0, 0, 0))) g.FillEllipse(sh, knob.X, knob.Y + 1, knob.Width, knob.Height);
            using (var b = new SolidBrush(Gfx.Lerp(Theme.TextDim, Color.White, 0.4f + 0.6f * v))) g.FillEllipse(b, knob);
            if (!Enabled) using (var b = new SolidBrush(Gfx.Alpha(Theme.EffectiveBack(Parent), 140))) g.FillRectangle(b, 0, 0, SwW + 1, Height);
            if (Focused && ShowFocusCues) Gfx.DrawRound(g, Gfx.Alpha(Theme.Accent, 200), new RectangleF(track.X - 2, track.Y - 2, track.Width + 4, track.Height + 4), SwH / 2f + 2, 1.2f);
            if (!string.IsNullOrEmpty(Text))
            {
                var fc = ForeColor == SystemColors.ControlText || ForeColor == Theme.Text ? Theme.Text : ForeColor;
                if (!Enabled) fc = Theme.TextDim;
                var tr = new Rectangle(SwW + Theme.S(10), 0, Width - SwW - Theme.S(10), Height);
                bool multi = Height > SwH + Theme.S(12);
                TextRenderer.DrawText(g, Text, Font, tr, fc,
                    TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | (multi ? TextFormatFlags.WordBreak | TextFormatFlags.Top : TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine));
            }
        }
    }

    /// <summary>Optionsfeld mit animiertem Punkt.</summary>
    public class FxRadio : RadioButton, IFxControl
    {
        readonly Tween t, hover;

        public FxRadio()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            t = new Tween(this, 0, 16);
            hover = new Tween(this, 0, 16);
            Cursor = Cursors.Hand;
            AutoSize = true;
            Font = Theme.F(9f);
        }

        protected override void OnCheckedChanged(EventArgs e)
        {
            if (IsHandleCreated && Visible) t.To(Checked ? 1 : 0); else t.Set(Checked ? 1 : 0);
            base.OnCheckedChanged(e);
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); t.Set(Checked ? 1 : 0); }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover.To(1); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover.To(0); }

        public override Size GetPreferredSize(Size proposed)
        {
            var ts = Gfx.Measure(Text, Font);
            return new Size(Theme.S(18) + Theme.S(8) + ts.Width + 2, Math.Max(Theme.S(24), ts.Height + Theme.S(4)));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.EffectiveBack(Parent));
            float v = t.Value, h = hover.Value;
            int d = Theme.S(18);
            var r = new RectangleF(0.5f, (Height - d) / 2f, d, d);
            using (var b = new SolidBrush(Gfx.Lerp(Theme.Panel2, Theme.Hover, h))) g.FillEllipse(b, r);
            using (var p = new Pen(Gfx.Lerp(Gfx.Lerp(Theme.Border, Theme.TextDim, h * 0.5f), Theme.Accent, v), 1.5f * Theme.Dpi)) g.DrawEllipse(p, r);
            if (v > 0.01f)
            {
                float dd = d * 0.5f * v;
                using (var b = new SolidBrush(Theme.Accent)) g.FillEllipse(b, r.X + (d - dd) / 2, r.Y + (d - dd) / 2, dd, dd);
            }
            if (Focused && ShowFocusCues) using (var p = new Pen(Gfx.Alpha(Theme.Accent, 160), 1)) g.DrawEllipse(p, r.X - 2, r.Y - 2, d + 4, d + 4);
            TextRenderer.DrawText(g, Text, Font, new Rectangle(d + Theme.S(8), 0, Width - d - Theme.S(8), Height), Enabled ? Theme.Text : Theme.TextDim, Gfx.Left);
        }
    }

    // ======================= Auswahlliste =======================

    /// <summary>Dropdown (ersetzt ComboBox) mit dunklem Menü und animiertem Pfeil.</summary>
    public class FxCombo : Control, IFxControl
    {
        public readonly List<object> Items = new List<object>();
        int sel = -1;
        readonly Tween hover, open, focus;
        public event EventHandler SelectedIndexChanged;
        public ComboBoxStyle DropDownStyle { get; set; }

        public FxCombo()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true;
            hover = new Tween(this, 0, 16);
            open = new Tween(this, 0, 14);
            focus = new Tween(this, 0, 14);
            Height = Theme.S(32);
            Width = Theme.S(200);
            Cursor = Cursors.Hand;
            Font = Theme.F(9f);
            Margin = new Padding(Theme.S(3));
        }

        public int SelectedIndex
        {
            get { return sel; }
            set
            {
                int v = value < -1 ? -1 : value;
                if (v >= Items.Count) v = Items.Count - 1;
                if (v == sel) return;
                sel = v;
                Invalidate();
                var h = SelectedIndexChanged;
                if (h != null) h(this, EventArgs.Empty);
            }
        }

        public object SelectedItem
        {
            get { return sel >= 0 && sel < Items.Count ? Items[sel] : null; }
            set { SelectedIndex = Items.IndexOf(value); }
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover.To(1); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover.To(0); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); focus.To(1); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); focus.To(0); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || !Enabled) return;
            Focus();
            ShowDrop();
        }

        void ShowDrop()
        {
            if (Items.Count == 0) return;
            var dd = new ContextMenuStrip();
            Theme.Menu(dd);
            dd.ShowImageMargin = true;
            dd.ShowCheckMargin = false;
            dd.MinimumSize = new Size(Width, 0);
            for (int i = 0; i < Items.Count; i++)
            {
                int idx = i;
                var it = new ToolStripMenuItem(Convert.ToString(Items[i], CultureInfo.CurrentCulture)) { Checked = i == sel, Font = Font };
                it.Click += (s, e) => SelectedIndex = idx;
                dd.Items.Add(it);
            }
            open.To(1);
            dd.Closed += (s, e) =>
            {
                open.To(0);
                try { BeginInvoke(new Action(() => dd.Dispose())); } catch { }
            };
            dd.Show(this, new Point(0, Height + Theme.S(3)));
        }

        protected override bool IsInputKey(Keys keyData)
        {
            var k = keyData & Keys.KeyCode;
            return k == Keys.Up || k == Keys.Down || base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Down) { if (sel < Items.Count - 1) SelectedIndex = sel + 1; e.Handled = true; }
            else if (e.KeyCode == Keys.Up) { if (sel > 0) SelectedIndex = sel - 1; e.Handled = true; }
            else if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space || e.KeyCode == Keys.F4) { ShowDrop(); e.Handled = true; }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (!Focused) return;
            int n = sel - Math.Sign(e.Delta);
            if (n >= 0 && n < Items.Count) SelectedIndex = n;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.EffectiveBack(Parent));
            float h = hover.Value, o = open.Value, f = Math.Max(focus.Value * 0.7f, o);
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            float rad = Theme.S(7);
            Gfx.FillRound(g, Gfx.Lerp(Theme.Panel2, Theme.Hover, h * 0.7f), r, rad);
            Gfx.DrawRound(g, Gfx.Lerp(Theme.Border, Theme.Accent, f), r, rad, 1f);
            string txt = SelectedItem != null ? Convert.ToString(SelectedItem, CultureInfo.CurrentCulture) : "";
            TextRenderer.DrawText(g, txt, Font, new Rectangle(Theme.S(11), 0, Width - Theme.S(36), Height), Enabled ? Theme.Text : Theme.TextDim, Gfx.Left);
            Gfx.Chevron(g, new PointF(Width - Theme.S(16), Height / 2f + Theme.S(1) - o * Theme.S(1)), Theme.S(4), Gfx.Lerp(Theme.TextDim, Theme.Accent, f), 180 * o, 1.6f * Theme.Dpi);
        }
    }

    // ======================= Zahlenfeld =======================

    /// <summary>Zahlenfeld mit −/+ (gedrückt halten = wiederholen, Mausrad).</summary>
    public class FxNumber : Control, IFxControl
    {
        readonly TextBox box;
        decimal val, min = 0, max = 100, inc = 1;
        int hot = -1, down = -1;
        readonly Tween focus;
        readonly Timer repeat;
        public event EventHandler ValueChanged;
        public BorderStyle BorderStyle { get; set; }

        public FxNumber()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            box = new TextBox { BorderStyle = BorderStyle.None, TextAlign = HorizontalAlignment.Center, BackColor = Theme.Panel2, ForeColor = Theme.Text, Font = Theme.Medium(9.5f), Tag = "keep" };
            Controls.Add(box);
            focus = new Tween(this, 0, 14);
            Height = Theme.S(32);
            Width = Theme.S(120);
            Margin = new Padding(Theme.S(3));
            box.Text = "0";
            box.GotFocus += (s, e) => focus.To(1);
            box.LostFocus += (s, e) => { focus.To(0); Commit(); };
            box.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Up) { Step(1); e.SuppressKeyPress = true; }
                else if (e.KeyCode == Keys.Down) { Step(-1); e.SuppressKeyPress = true; }
                else if (e.KeyCode == Keys.Enter) { Commit(); e.SuppressKeyPress = true; }
            };
            repeat = new Timer { Interval = 380 };
            repeat.Tick += (s, e) => { repeat.Interval = 55; if (down >= 0) Step(down == 0 ? -1 : 1); };
        }

        public decimal Minimum { get { return min; } set { min = value; if (val < min) Value = min; } }
        public decimal Maximum { get { return max; } set { max = value; if (val > max) Value = max; } }
        public decimal Increment { get { return inc; } set { inc = value; } }

        public decimal Value
        {
            get { return val; }
            set
            {
                decimal v = Math.Max(min, Math.Min(max, value));
                box.Text = v.ToString("0.##", CultureInfo.CurrentCulture);
                if (v == val) return;
                val = v;
                Invalidate();
                var h = ValueChanged;
                if (h != null) h(this, EventArgs.Empty);
            }
        }

        void Step(int dir) { Value = val + dir * inc; }

        void Commit()
        {
            decimal d;
            if (decimal.TryParse(box.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out d)) Value = d;
            else box.Text = val.ToString("0.##", CultureInfo.CurrentCulture);
        }

        int BtnW { get { return Height; } }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int bh = box.PreferredHeight;
            box.SetBounds(BtnW, (Height - bh) / 2, Math.Max(10, Width - 2 * BtnW), bh);
        }

        int Part(Point p) { return p.X < BtnW ? 0 : (p.X > Width - BtnW ? 1 : -1); }

        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); int n = Part(e.Location); if (n != hot) { hot = n; Cursor = n >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); } }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hot = -1; Invalidate(); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            int n = Part(e.Location);
            if (n < 0 || e.Button != MouseButtons.Left) { box.Focus(); return; }
            down = n;
            Step(n == 0 ? -1 : 1);
            repeat.Interval = 380;
            repeat.Start();
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); down = -1; repeat.Stop(); Invalidate(); }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            Step(Math.Sign(e.Delta));
            if (e is HandledMouseEventArgs) ((HandledMouseEventArgs)e).Handled = true;
        }

        protected override void Dispose(bool disposing) { if (disposing) repeat.Dispose(); base.Dispose(disposing); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.EffectiveBack(Parent));
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            float rad = Theme.S(7);
            Gfx.FillRound(g, Theme.Panel2, r, rad);
            for (int i = 0; i < 2; i++)
            {
                var br = i == 0 ? new RectangleF(r.X, r.Y, BtnW, r.Height) : new RectangleF(Width - BtnW - 1, r.Y, BtnW, r.Height);
                if (hot == i || down == i)
                {
                    using (var path = Gfx.Round(r, rad))
                    {
                        var st = g.Save();
                        g.SetClip(path);
                        using (var b = new SolidBrush(down == i ? Gfx.Lerp(Theme.Hover, Theme.Accent, 0.3f) : Theme.Hover)) g.FillRectangle(b, br);
                        g.Restore(st);
                    }
                }
                float cx = br.X + br.Width / 2, cy = br.Y + br.Height / 2, k = Theme.S(5);
                using (var p = new Pen(hot == i ? Theme.Text : Theme.TextDim, 1.6f * Theme.Dpi))
                {
                    p.StartCap = p.EndCap = LineCap.Round;
                    g.DrawLine(p, cx - k, cy, cx + k, cy);
                    if (i == 1) g.DrawLine(p, cx, cy - k, cx, cy + k);
                }
            }
            Gfx.DrawRound(g, Gfx.Lerp(Theme.Border, Theme.Accent, focus.Value), r, rad, 1f);
        }
    }

    // ======================= Schieberegler =======================

    public class FxSlider : Control, IFxControl
    {
        int min, max = 100, val;
        public int SmallChange = 1, LargeChange = 10, TickFrequency;
        public TickStyle TickStyle;
        public int DefaultValue = int.MinValue;
        public Color? FillColor;
        readonly Tween hover, drag;
        bool dragging;
        public event EventHandler ValueChanged;

        public FxSlider()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            hover = new Tween(this, 0, 16);
            drag = new Tween(this, 0, 18);
            Height = Theme.S(26);
            Width = Theme.S(200);
            Cursor = Cursors.Hand;
            TabStop = true;
        }

        public int Minimum { get { return min; } set { min = value; if (val < min) Value = min; Invalidate(); } }
        public int Maximum { get { return max; } set { max = value; if (val > max) Value = max; Invalidate(); } }

        public int Value
        {
            get { return val; }
            set
            {
                int v = Math.Max(min, Math.Min(max, value));
                if (v == val) return;
                val = v;
                Invalidate();
                var h = ValueChanged;
                if (h != null) h(this, EventArgs.Empty);
            }
        }

        float KnobR { get { return Theme.S(7) + hover.Value * Theme.S(1.5f) + drag.Value * Theme.S(1); } }
        float X0 { get { return Theme.S(10); } }
        float X1 { get { return Width - Theme.S(10); } }

        void SetFromX(int x)
        {
            float t = (x - X0) / Math.Max(1, X1 - X0);
            Value = (int)Math.Round(min + Math.Max(0, Math.Min(1, t)) * (max - min));
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover.To(1); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (!dragging) hover.To(0); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            Focus();
            dragging = true;
            Capture = true;
            drag.To(1);
            SetFromX(e.X);
        }

        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (dragging) SetFromX(e.X); }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            dragging = false;
            Capture = false;
            drag.To(0);
            if (!ClientRectangle.Contains(e.Location)) hover.To(0);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (DefaultValue != int.MinValue) Value = DefaultValue;
        }

        protected override bool IsInputKey(Keys k)
        {
            var c = k & Keys.KeyCode;
            return c == Keys.Left || c == Keys.Right || c == Keys.Up || c == Keys.Down || base.IsInputKey(k);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            switch (e.KeyCode)
            {
                case Keys.Left: case Keys.Down: Value -= SmallChange; break;
                case Keys.Right: case Keys.Up: Value += SmallChange; break;
                case Keys.PageDown: Value -= LargeChange; break;
                case Keys.PageUp: Value += LargeChange; break;
                case Keys.Home: Value = min; break;
                case Keys.End: Value = max; break;
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (!Focused) return;
            Value += Math.Sign(e.Delta) * SmallChange;
            if (e is HandledMouseEventArgs) ((HandledMouseEventArgs)e).Handled = true;
        }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.EffectiveBack(Parent));
            float cy = Height / 2f, th = Theme.S(4);
            float t = max > min ? (val - min) / (float)(max - min) : 0;
            float kx = X0 + (X1 - X0) * t;
            var track = new RectangleF(X0, cy - th / 2, X1 - X0, th);
            Gfx.FillRound(g, Theme.Border, track, th / 2);
            var fill = new RectangleF(X0, cy - th / 2, Math.Max(th, kx - X0), th);
            var fc = FillColor ?? Theme.Accent;
            if (Enabled)
                using (var b = Gfx.Gradient(new RectangleF(X0, 0, X1 - X0, 1), fc, Gfx.Lerp(fc, Theme.Secondary, FillColor.HasValue ? 0.2f : 0.5f), 0))
                    Gfx.FillRound(g, b, fill, th / 2);
            else Gfx.FillRound(g, Theme.TextDim, fill, th / 2);
            float kr = KnobR;
            if (drag.Value > 0.01f || Focused)
                using (var b = new SolidBrush(Gfx.Alpha(fc, (int)(60 * Math.Max(drag.Value, Focused ? 0.6f : 0)))))
                    g.FillEllipse(b, kx - kr - Theme.S(4), cy - kr - Theme.S(4), (kr + Theme.S(4)) * 2, (kr + Theme.S(4)) * 2);
            using (var sh = new SolidBrush(Color.FromArgb(80, 0, 0, 0))) g.FillEllipse(sh, kx - kr, cy - kr + 1.5f, kr * 2, kr * 2);
            using (var b = new SolidBrush(Enabled ? Color.White : Theme.TextDim)) g.FillEllipse(b, kx - kr, cy - kr, kr * 2, kr * 2);
            using (var p = new Pen(fc, 2f * Theme.Dpi)) g.DrawEllipse(p, kx - kr + 1, cy - kr + 1, kr * 2 - 2, kr * 2 - 2);
        }
    }

    // ======================= Scrollbalken =======================

    /// <summary>Schmaler, moderner Scrollbalken (wird beim Überfahren breiter).</summary>
    public class FxScrollBar : Control, IFxControl
    {
        public bool Horizontal;
        int min, max = 100, large = 10, small = 1, val;
        readonly Tween hover;
        bool dragging;
        int dragOffset;
        public event EventHandler ValueChanged;
        public event EventHandler Scroll;

        public FxScrollBar()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            hover = new Tween(this, 0, 14);
            Width = Theme.S(12);
            Height = Theme.S(12);
        }

        public int Minimum { get { return min; } set { min = value; Clamp(); Invalidate(); } }
        public int Maximum { get { return max; } set { max = value; Clamp(); Invalidate(); } }
        public int LargeChange { get { return large; } set { large = Math.Max(1, value); Clamp(); Invalidate(); } }
        public int SmallChange { get { return small; } set { small = Math.Max(1, value); } }
        int MaxVal { get { return Math.Max(min, max - large + 1); } }

        public int Value
        {
            get { return val; }
            set
            {
                int v = Math.Max(min, Math.Min(MaxVal, value));
                if (v == val) return;
                val = v;
                Invalidate();
                var h = ValueChanged;
                if (h != null) h(this, EventArgs.Empty);
            }
        }

        void Clamp() { if (val > MaxVal) Value = MaxVal; if (val < min) Value = min; }

        int Len { get { return Horizontal ? Width : Height; } }

        void Thumb(out float start, out float size)
        {
            float range = Math.Max(1, max - min + 1);
            size = Math.Max(Theme.S(28), Len * Math.Min(1f, large / range));
            float travel = Len - size;
            float t = MaxVal > min ? (val - min) / (float)(MaxVal - min) : 0;
            start = travel * t;
        }

        void Fire() { var h = Scroll; if (h != null) h(this, EventArgs.Empty); }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover.To(1); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (!dragging) hover.To(0); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            float s, z;
            Thumb(out s, out z);
            int pos = Horizontal ? e.X : e.Y;
            if (pos >= s && pos <= s + z) { dragging = true; dragOffset = (int)(pos - s); Capture = true; }
            else { Value += pos < s ? -large : large; Fire(); }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!dragging) return;
            float s, z;
            Thumb(out s, out z);
            float travel = Math.Max(1, Len - z);
            float t = ((Horizontal ? e.X : e.Y) - dragOffset) / travel;
            Value = (int)Math.Round(min + Math.Max(0, Math.Min(1, t)) * (MaxVal - min));
            Fire();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            dragging = false;
            Capture = false;
            if (!ClientRectangle.Contains(e.Location)) hover.To(0);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.EffectiveBack(Parent));
            if (MaxVal <= min) return;
            float h = Math.Max(hover.Value, dragging ? 1 : 0);
            float s, z;
            Thumb(out s, out z);
            float thick = Theme.S(4) + Theme.S(4) * h;
            var col = Gfx.Lerp(Gfx.Alpha(Theme.TextDim, 90), Gfx.Alpha(Theme.Text, 170), h);
            if (dragging) col = Gfx.Alpha(Theme.Accent, 220);
            RectangleF r = Horizontal
                ? new RectangleF(s + 2, Height - thick - Theme.S(2), z - 4, thick)
                : new RectangleF(Width - thick - Theme.S(2), s + 2, thick, z - 4);
            if (h > 0.01f)
            {
                using (var b = new SolidBrush(Gfx.Alpha(Theme.Panel2, (int)(140 * h))))
                    if (Horizontal) g.FillRectangle(b, 0, Height - thick - Theme.S(4), Width, thick + Theme.S(4));
                    else g.FillRectangle(b, Width - thick - Theme.S(4), 0, thick + Theme.S(4), Height);
            }
            Gfx.FillRound(g, col, r, thick / 2);
        }
    }

    // ======================= Karte (ersetzt GroupBox) =======================

    public class Card : Panel, IFxControl
    {
        public string Subtitle;
        public bool Flat;     // ohne eigene Fläche (nur Titel)
        public Color? AccentColor;

        public Card()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Panel;
            ForeColor = Theme.Text;
            Padding = new Padding(Theme.S(16), Theme.S(14), Theme.S(16), Theme.S(14));
        }

        public override string Text
        {
            get { return base.Text; }
            set
            {
                base.Text = value;
                var p = Padding;
                p.Top = string.IsNullOrEmpty(value) ? Theme.S(14) : Theme.S(46);
                Padding = p;
                Invalidate();
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.EffectiveBack(Parent));
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            float rad = Theme.S(10);
            if (!Flat)
            {
                Gfx.FillRound(g, BackColor, r, rad);
                Gfx.DrawRound(g, Gfx.Alpha(Theme.Border, 170), r, rad, 1f);
            }
            if (!string.IsNullOrEmpty(Text))
            {
                var ac = AccentColor ?? Theme.Accent;
                var bar = new RectangleF(Theme.S(16), Theme.S(17), Theme.S(3), Theme.S(15));
                using (var b = Gfx.Gradient(bar, ac, Gfx.Lerp(ac, Theme.Secondary, 0.6f), 90)) Gfx.FillRound(g, b, bar, Theme.S(1.5f));
                var tf = Theme.Semi(10.5f);
                var ts = Gfx.Measure(Text, tf);
                TextRenderer.DrawText(g, Text, tf, new Rectangle(Theme.S(27), Theme.S(14), Width - Theme.S(40), ts.Height + 2), Theme.Text, Gfx.Left);
                if (!string.IsNullOrEmpty(Subtitle))
                    TextRenderer.DrawText(g, Subtitle, Theme.F(8.5f), new Rectangle(Theme.S(27) + ts.Width + Theme.S(10), Theme.S(14), Width - Theme.S(50) - ts.Width, ts.Height + 2), Theme.TextDim, Gfx.Left);
            }
        }

        protected override void OnPaint(PaintEventArgs e) { }
    }

    // ======================= Fortschritt =======================

    public class FxProgress : Control, IFxControl
    {
        int value;
        readonly Tween v;
        float shimmer;
        bool loop;

        public FxProgress()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            v = new Tween(this, 0, 10);
            Height = Theme.S(8);
            Width = Theme.S(200);
        }

        public int Value { get { return value; } set { this.value = Math.Max(0, Math.Min(100, value)); v.To(this.value); if (this.value == 0) v.Set(0); } }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible && !loop && Anim.Enabled)
            {
                loop = true;
                Anim.Run(() =>
                {
                    if (IsDisposed || !Visible) { loop = false; return false; }
                    shimmer = (shimmer + Anim.Dt * 0.7f) % 1.4f;
                    Invalidate();
                    return true;
                });
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.EffectiveBack(Parent));
            var r = new RectangleF(0, (Height - Theme.S(6)) / 2f, Width - 1, Theme.S(6));
            Gfx.FillRound(g, Theme.Panel2, r, r.Height / 2);
            var f = r;
            f.Width = Math.Max(r.Height, r.Width * v.Value / 100f);
            using (var b = Gfx.Gradient(r, Theme.Accent, Theme.Secondary, 0)) Gfx.FillRound(g, b, f, r.Height / 2);
            float sx = f.X + (shimmer - 0.2f) * r.Width;
            using (var path = Gfx.Round(f, r.Height / 2))
            {
                g.SetClip(path);
                var sr = new RectangleF(sx, f.Y, Theme.S(60), f.Height);
                using (var b = new LinearGradientBrush(new RectangleF(sr.X - 1, sr.Y, sr.Width + 2, sr.Height), Color.FromArgb(0, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 0f))
                {
                    var cb = new ColorBlend
                    {
                        Colors = new[] { Color.FromArgb(0, 255, 255, 255), Color.FromArgb(90, 255, 255, 255), Color.FromArgb(0, 255, 255, 255) },
                        Positions = new[] { 0f, 0.5f, 1f }
                    };
                    b.InterpolationColors = cb;
                    g.FillRectangle(b, sr);
                }
                g.ResetClip();
            }
        }
    }

    // ======================= Textfeld =======================

    /// <summary>Rahmen für ein TextBox mit runden Ecken, Icon und Fokus-Leuchten.</summary>
    public class FxField : Panel, IFxControl
    {
        public readonly TextBox Box;
        public string IconName;
        readonly Tween focus, hover;

        public FxField(TextBox tb, int width, string cue, string icon)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Box = tb;
            IconName = icon;
            tb.BorderStyle = BorderStyle.None;
            tb.BackColor = Theme.Panel2;
            tb.ForeColor = Theme.Text;
            tb.Font = Theme.F(9.5f);
            tb.Tag = "keep";
            Controls.Add(tb);
            Width = width;
            Height = Theme.S(32);
            Margin = new Padding(Theme.S(3));
            BackColor = Theme.Panel2;
            Cursor = Cursors.IBeam;
            focus = new Tween(this, 0, 14);
            hover = new Tween(this, 0, 16);
            tb.GotFocus += (s, e) => focus.To(1);
            tb.LostFocus += (s, e) => focus.To(0);
            tb.MouseEnter += (s, e) => hover.To(1);
            tb.MouseLeave += (s, e) => hover.To(0);
            tb.EnabledChanged += (s, e) => Invalidate();
            if (cue != null) Theme.Cue(tb, cue);
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover.To(1); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover.To(0); }
        protected override void OnClick(EventArgs e) { base.OnClick(e); Box.Focus(); }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int left = Theme.S(10) + (IconName != null ? Theme.S(22) : 0);
            int bh = Box.PreferredHeight;
            Box.SetBounds(left, (Height - bh) / 2 + 1, Math.Max(10, Width - left - Theme.S(10)), bh);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.EffectiveBack(Parent));
            float f = focus.Value;
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            float rad = Theme.S(7);
            Gfx.FillRound(g, Theme.Panel2, r, rad);
            Gfx.DrawRound(g, Gfx.Lerp(Gfx.Lerp(Theme.Border, Theme.TextDim, hover.Value * 0.3f), Theme.Accent, f), r, rad, 1f + f * 0.5f);
            if (IconName != null)
                Gfx.Icon(g, IconName, 10f, new Rectangle(Theme.S(8), 0, Theme.S(20), Height), Gfx.Lerp(Theme.TextDim, Theme.Accent, f));
        }

        protected override void OnPaint(PaintEventArgs e) { }
    }

    // ======================= Navigation =======================

    public sealed class NavEntry
    {
        public bool Header;
        public string Text, Value, Icon;
        public int Kind;
        public int Count = -1;
        public Color? Dot;
    }

    /// <summary>Seitenleiste mit gleitender Auswahl-Markierung und Hover-Effekt.</summary>
    public sealed class NavList : Control, IFxControl
    {
        public List<NavEntry> Items = new List<NavEntry>();
        int sel = -1, hot = -1;
        readonly Tween selY, hotFade;
        float scroll;
        public event EventHandler SelectedIndexChanged;
        public float ItemHeight = 36, HeaderHeight = 34;

        public NavList()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = Theme.Panel;
            Font = Theme.Medium(9.5f);
            selY = new Tween(this, 0, 18);
            hotFade = new Tween(this, 0, 18);
            TabStop = true;
        }

        float H(NavEntry e) { return Theme.S(e.Header ? HeaderHeight : ItemHeight); }

        float Y(int i)
        {
            float y = Theme.S(8);
            for (int k = 0; k < i && k < Items.Count; k++) y += H(Items[k]);
            return y;
        }

        float Total { get { return Y(Items.Count) + Theme.S(8); } }

        public NavEntry SelectedItem { get { return sel >= 0 && sel < Items.Count ? Items[sel] : null; } }

        public int SelectedIndex
        {
            get { return sel; }
            set
            {
                if (value < 0 || value >= Items.Count || Items[value].Header || value == sel) return;
                bool first = sel < 0;
                sel = value;
                if (first) selY.Set(Y(sel)); else selY.To(Y(sel));
                EnsureVisible(sel);
                Invalidate();
                var h = SelectedIndexChanged;
                if (h != null) h(this, EventArgs.Empty);
            }
        }

        public void SetItems(List<NavEntry> items, int select)
        {
            Items = items;
            sel = (select >= 0 && select < items.Count && !items[select].Header) ? select : -1;
            hot = -1;
            if (sel >= 0) selY.Set(Y(sel));
            ClampScroll();
            Invalidate();
        }

        void EnsureVisible(int i)
        {
            float y = Y(i) - scroll;
            if (y < 0) scroll += y - Theme.S(8);
            else if (y + H(Items[i]) > Height) scroll += y + H(Items[i]) - Height + Theme.S(8);
            ClampScroll();
        }

        void ClampScroll() { scroll = Math.Max(0, Math.Min(Math.Max(0, Total - Height), scroll)); }

        int At(Point p)
        {
            float y = p.Y + scroll;
            for (int i = 0; i < Items.Count; i++)
            {
                float a = Y(i);
                if (y >= a && y < a + H(Items[i])) return Items[i].Header ? -1 : i;
            }
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int n = At(e.Location);
            if (n != hot) { hot = n; hotFade.Set(0); hotFade.To(1); Cursor = n >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hot = -1; Invalidate(); }

        /// <summary>Rechtsklick auf einen Eintrag (Index, Position).</summary>
        public event Action<int, Point> ItemRightClicked;

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            int n = At(e.Location);
            if (n < 0) return;
            if (e.Button == MouseButtons.Right) { var h = ItemRightClicked; if (h != null) h(n, e.Location); return; }
            SelectedIndex = n;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            scroll -= Math.Sign(e.Delta) * Theme.S(60);
            ClampScroll();
            Invalidate();
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); ClampScroll(); }

        protected override bool IsInputKey(Keys k) { var c = k & Keys.KeyCode; return c == Keys.Up || c == Keys.Down || base.IsInputKey(k); }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            int d = e.KeyCode == Keys.Down ? 1 : (e.KeyCode == Keys.Up ? -1 : 0);
            if (d == 0) return;
            for (int i = sel + d; i >= 0 && i < Items.Count; i += d)
                if (!Items[i].Header) { SelectedIndex = i; break; }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            float pad = Theme.S(8);
            if (sel >= 0)
            {
                var sr = new RectangleF(pad, selY.Value - scroll + Theme.S(2), Width - pad * 2, Theme.S(ItemHeight) - Theme.S(4));
                Gfx.FillRound(g, Gfx.Lerp(BackColor, Theme.Accent, 0.16f), sr, Theme.S(7));
                var bar = new RectangleF(pad + Theme.S(1), sr.Y + Theme.S(8), Theme.S(3), sr.Height - Theme.S(16));
                using (var b = Gfx.Gradient(bar, Theme.Accent, Theme.Secondary, 90)) Gfx.FillRound(g, b, bar, Theme.S(1.5f));
            }
            var small = Theme.Semi(7.5f);
            for (int i = 0; i < Items.Count; i++)
            {
                var it = Items[i];
                float y = Y(i) - scroll, h = H(it);
                if (y > Height || y + h < 0) continue;
                if (it.Header)
                {
                    TextRenderer.DrawText(g, (it.Text ?? "").ToUpperInvariant(), small, new Rectangle(Theme.S(20), (int)y + Theme.S(10), Width - Theme.S(28), (int)h - Theme.S(10)), Theme.TextDim, Gfx.Left);
                    continue;
                }
                bool s = i == sel;
                if (i == hot && !s)
                    Gfx.FillRound(g, Gfx.Alpha(Theme.Hover, (int)(255 * hotFade.Value)), new RectangleF(pad, y + Theme.S(2), Width - pad * 2, h - Theme.S(4)), Theme.S(7));
                int x = Theme.S(20);
                if (it.Icon != null)
                {
                    Gfx.Icon(g, it.Icon, 10.5f, new Rectangle(x, (int)y, Theme.S(20), (int)h), s ? Theme.Accent : (it.Kind == 2 ? Theme.Star : Theme.TextDim));
                    x += Theme.S(28);
                }
                else if (it.Dot.HasValue)
                {
                    float dd = Theme.S(8);
                    using (var b = new SolidBrush(it.Dot.Value)) g.FillEllipse(b, x + Theme.S(4), y + h / 2 - dd / 2, dd, dd);
                    x += Theme.S(24);
                }
                int countW = 0;
                if (it.Count >= 0)
                {
                    string c = it.Count.ToString(CultureInfo.CurrentCulture);
                    var cf = Theme.Medium(8f);
                    var cs = Gfx.Measure(c, cf);
                    countW = cs.Width + Theme.S(12);
                    var cr = new RectangleF(Width - pad - Theme.S(8) - countW, y + h / 2 - cs.Height / 2f - Theme.S(2), countW, cs.Height + Theme.S(4));
                    Gfx.FillRound(g, s ? Gfx.Lerp(Theme.Panel2, Theme.Accent, 0.25f) : Theme.Panel2, cr, cr.Height / 2);
                    TextRenderer.DrawText(g, c, cf, Rectangle.Round(cr), s ? Theme.Text : Theme.TextDim, Gfx.Center);
                }
                Color fc = s ? Color.White : (it.Count == 0 && it.Kind >= 4 ? Theme.TextDim : Theme.Text);
                TextRenderer.DrawText(g, it.Text, Font, new Rectangle(x, (int)y, Width - x - countW - Theme.S(20), (int)h), fc, Gfx.Left);
            }
            if (Total > Height)
            {
                float vis = Height / Total;
                float th = Math.Max(Theme.S(24), Height * vis);
                float ty = (Height - th) * (scroll / Math.Max(1, Total - Height));
                Gfx.FillRound(g, Gfx.Alpha(Theme.TextDim, 70), new RectangleF(Width - Theme.S(5), ty, Theme.S(3), th), Theme.S(1.5f));
            }
        }
    }

    // ======================= Dialog =======================

    /// <summary>Dunkler Dialog (ersetzt MessageBox) mit Einblend-Animation.</summary>
    public sealed class FxDialog : Form
    {
        int result = -1;
        readonly string icon;
        readonly string title, text;
        Point dragStart;

        FxDialog(string title, string text, string icon, string[] buttons, int primary, int cancel, bool danger)
        {
            this.title = title; this.text = text; this.icon = icon;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            KeyPreview = true;
            BackColor = Theme.Panel;
            Font = Theme.F(9.5f);
            AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;
            Opacity = Anim.Enabled ? 0 : 1;
            Theme.RoundCorners(this);
            int w = Theme.S(470);
            var ts = TextRenderer.MeasureText(text ?? "", Theme.F(9.5f), new Size(w - Theme.S(100), int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            int h = Theme.S(58) + ts.Height + Theme.S(80);
            ClientSize = new Size(w, Math.Max(Theme.S(160), h));
            int x = ClientSize.Width - Theme.S(18);
            for (int i = 0; i < buttons.Length; i++)
            {
                int idx = i;
                var b = new FxButton { Text = buttons[i] };
                var ps = b.GetPreferredSize(Size.Empty);
                b.Size = new Size(Math.Max(Theme.S(100), ps.Width), Theme.S(36));
                x -= b.Width;
                b.Location = new Point(x, ClientSize.Height - b.Height - Theme.S(16));
                x -= Theme.S(8);
                if (i == primary) b.Kind = danger ? FxKind.Danger : FxKind.Primary;
                b.Click += (s, e) => { result = idx; Close(); };
                Controls.Add(b);
                if (i == primary) AcceptButton = b;
                if (i == cancel) CancelButton = b;
            }
            MouseDown += (s, e) => { if (e.Button == MouseButtons.Left) dragStart = e.Location; };
            MouseMove += (s, e) => { if (e.Button == MouseButtons.Left) Location = new Point(Location.X + e.X - dragStart.X, Location.Y + e.Y - dragStart.Y); };
        }

        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ClassStyle |= 0x20000; return cp; } // CS_DROPSHADOW
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (Opacity < 1) Anim.Over(this, 0.18f, t => { try { Opacity = t; } catch { } });
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var p = new Pen(Theme.Border)) g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
            using (var b = Gfx.Gradient(new RectangleF(0, 0, Width, Theme.S(3)), Theme.Accent, Theme.Secondary, 0)) g.FillRectangle(b, 0, 0, Width, Theme.S(3));
            Color ic = icon == "warning" ? Theme.Warning : icon == "error" ? Theme.Danger : icon == "question" ? Theme.Secondary : Theme.Accent;
            var cr = new RectangleF(Theme.S(24), Theme.S(26), Theme.S(36), Theme.S(36));
            using (var b = new SolidBrush(Gfx.Alpha(ic, 40))) g.FillEllipse(b, cr);
            string glyph = icon == "warning" ? "warning" : icon == "error" ? "error" : icon == "question" ? "info" : "check";
            Gfx.Icon(g, glyph, 13f, Rectangle.Round(cr), ic);
            TextRenderer.DrawText(g, title, Theme.Semi(12f), new Rectangle(Theme.S(76), Theme.S(24), Width - Theme.S(96), Theme.S(28)), Theme.Text, Gfx.Left);
            TextRenderer.DrawText(g, text, Theme.F(9.5f), new Rectangle(Theme.S(76), Theme.S(56), Width - Theme.S(100), Height - Theme.S(120)), Theme.TextDim,
                TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        }

        public static int Show(IWin32Window owner, string title, string text, string icon, string[] buttons, int primary, int cancel, bool danger)
        {
            using (var d = new FxDialog(title, text, icon, buttons, primary, cancel, danger))
            {
                if (owner == null) d.StartPosition = FormStartPosition.CenterScreen;
                d.ShowDialog(owner);
                return d.result;
            }
        }

        public static bool Ask(IWin32Window owner, string title, string text, string yes, string no, bool danger = false)
        {
            return Show(owner, title, text, danger ? "warning" : "question", new[] { yes, no }, 0, 1, danger) == 0;
        }

        public static void Info(IWin32Window owner, string title, string text, string icon = "info")
        {
            Show(owner, title, text, icon, new[] { "OK" }, 0, 0, false);
        }
    }

    // ======================= Benachrichtigungen =======================

    /// <summary>Karte unten rechts im Fenster, gleitet herein und verschwindet nach ein paar Sekunden.</summary>
    public sealed class Toast : Control, IFxControl
    {
        static readonly List<Toast> active = new List<Toast>();
        readonly Form host;
        readonly string title, text, icon;
        readonly Color color;
        readonly Image thumb;
        readonly Action click;
        readonly Tween slide, ypos;
        double shownAt;
        float progress;
        bool closing;

        Toast(Form host, string title, string text, string icon, Color color, Image thumb, Action click)
        {
            this.host = host; this.title = title; this.text = text; this.icon = icon; this.color = color; this.thumb = thumb; this.click = click;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            Size = new Size(Theme.S(360), Theme.S(thumb != null ? 84 : ((text ?? "").Contains("\n") ? 84 : 70)));
            BackColor = Theme.Panel2;
            Cursor = click != null ? Cursors.Hand : Cursors.Default;
            slide = new Tween(this, 0, 12) { Changed = Place };
            ypos = new Tween(this, 0, 14) { Changed = Place };
            using (var p = Gfx.Round(new RectangleF(0, 0, Width, Height), Theme.S(10))) Region = new Region(p);
        }

        public static void Show(Form host, string title, string text, string icon, Color color, Image thumb = null, Action click = null)
        {
            if (host == null || host.IsDisposed || !host.Visible) return;
            var t = new Toast(host, title, text, icon, color, thumb, click);
            host.Controls.Add(t);
            t.BringToFront();
            active.Insert(0, t);
            Restack(true);
            t.shownAt = Anim.Now;
            t.slide.To(1);
            Anim.Run(t.Tick);
        }

        static void Restack(bool initial)
        {
            float y = 0;
            for (int i = 0; i < active.Count; i++)
            {
                var t = active[i];
                if (t.ypos.Value == 0 && t.ypos.Target == 0 && i == 0 && initial) t.ypos.Set(y);
                else t.ypos.To(y);
                y += t.Height + Theme.S(10);
            }
        }

        void Place()
        {
            if (IsDisposed || host.IsDisposed) return;
            int margin = Theme.S(18);
            int x = host.ClientSize.Width - (int)((Width + margin) * Anim.EaseOut(Math.Max(0, Math.Min(1, slide.Value))));
            int y = host.ClientSize.Height - margin - Theme.S(26) - Height - (int)ypos.Value;
            Location = new Point(x, y);
        }

        bool Tick()
        {
            if (IsDisposed) return false;
            double age = Anim.Now - shownAt;
            const double life = 4.2;
            progress = (float)Math.Min(1, age / life);
            Invalidate(new Rectangle(0, Height - Theme.S(3), Width, Theme.S(3)));
            if (!closing && (age > life || !host.Visible)) Close();
            if (closing && slide.Value < 0.02f)
            {
                active.Remove(this);
                Restack(false);
                host.Controls.Remove(this);
                Dispose();
                return false;
            }
            Place();
            return true;
        }

        void Close() { closing = true; slide.To(0); }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); shownAt = Math.Max(shownAt, Anim.Now - 1.0); }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            if (click != null) click();
            Close();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.Panel2);
            using (var path = Gfx.Round(new RectangleF(0, 0, Width - 1, Height - 1), Theme.S(10)))
            using (var p = new Pen(Theme.Border)) g.DrawPath(p, path);
            using (var b = Gfx.Gradient(new RectangleF(0, 0, Theme.S(4), Height), color, Gfx.Lerp(color, Theme.Secondary, 0.5f), 90)) g.FillRectangle(b, 0, 0, Theme.S(4), Height);
            int x = Theme.S(16);
            if (thumb != null)
            {
                var tr = new Rectangle(x, Theme.S(12), Theme.S(107), Height - Theme.S(24));
                Gfx.RoundedImage(g, thumb, tr, Theme.S(6), Theme.Panel2);
                x = tr.Right + Theme.S(12);
            }
            else
            {
                var cr = new RectangleF(x, Height / 2f - Theme.S(16), Theme.S(32), Theme.S(32));
                using (var b = new SolidBrush(Gfx.Alpha(color, 45))) g.FillEllipse(b, cr);
                Gfx.Icon(g, icon, 11f, Rectangle.Round(cr), color);
                x += Theme.S(44);
            }
            TextRenderer.DrawText(g, title, Theme.Semi(10f), new Rectangle(x, Theme.S(14), Width - x - Theme.S(12), Theme.S(22)), Theme.Text, Gfx.Left);
            TextRenderer.DrawText(g, text, Theme.F(8.5f), new Rectangle(x, Theme.S(36), Width - x - Theme.S(12), Height - Theme.S(44)), Theme.TextDim,
                TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            using (var b = new SolidBrush(Gfx.Alpha(color, 160))) g.FillRectangle(b, 0, Height - Theme.S(3), Width * (1 - progress), Theme.S(3));
        }
    }

    /// <summary>Einblendung über allen Fenstern (auch über Spielen im randlosen Fenstermodus), klickt durch, stiehlt keinen Fokus.</summary>
    public sealed class OverlayToast : Form
    {
        static OverlayToast instance;
        string title, text;
        Color color;
        double shownAt;
        bool running;
        float fade;

        OverlayToast()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Theme.Panel;
            AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;
            Size = new Size(Theme.S(330), Theme.S(70));
            Opacity = 0;
            Theme.RoundCorners(this);
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x00000080 | 0x08000000 | 0x00000008 | 0x00000020; // TOOLWINDOW | NOACTIVATE | TOPMOST | TRANSPARENT
                return cp;
            }
        }

        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);

        public static void ShowMessage(string title, string text, Color color, Rectangle screen, int corner)
        {
            if (instance == null || instance.IsDisposed) instance = new OverlayToast();
            var o = instance;
            o.title = title; o.text = text; o.color = color;
            int m = Theme.S(24);
            int x = (corner == 1 || corner == 3) ? screen.Left + m : screen.Right - o.Width - m;
            int y = (corner >= 2) ? screen.Bottom - o.Height - m - Theme.S(40) : screen.Top + m;
            o.Location = new Point(x, y);
            o.shownAt = Anim.Now;
            if (!o.Visible)
            {
                var h = o.Handle;
                ShowWindow(h, 4 /*SW_SHOWNOACTIVATE*/);
                o.Visible = true;
            }
            SetWindowPos(o.Handle, new IntPtr(-1), x, y, o.Width, o.Height, 0x0010 /*NOACTIVATE*/ | 0x0040 /*SHOWWINDOW*/);
            o.Invalidate();
            if (!o.running)
            {
                o.running = true;
                Anim.Run(o.Tick);
            }
        }

        bool Tick()
        {
            if (IsDisposed) { running = false; return false; }
            double age = Anim.Now - shownAt;
            const double hold = 2.6;
            float target = age < hold ? 1f : 0f;
            float speed = Anim.Enabled ? Anim.Dt * Anim.Factor / 0.22f : 1f;
            fade = target > fade ? Math.Min(target, fade + speed) : Math.Max(target, fade - speed);
            try { Opacity = 0.96 * Anim.EaseOut(fade); } catch { }
            Invalidate(new Rectangle(0, Height - Theme.S(3), Width, Theme.S(3)));
            if (age > hold && fade <= 0)
            {
                Hide();
                running = false;
                return false;
            }
            return true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.Panel);
            using (var p = new Pen(Gfx.Alpha(color, 120))) g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
            using (var b = Gfx.Gradient(new RectangleF(0, 0, Theme.S(5), Height), color, Theme.Secondary, 90)) g.FillRectangle(b, 0, 0, Theme.S(5), Height);
            var cr = new RectangleF(Theme.S(18), Height / 2f - Theme.S(17), Theme.S(34), Theme.S(34));
            using (var b = new SolidBrush(Gfx.Alpha(color, 50))) g.FillEllipse(b, cr);
            Gfx.Check(g, new RectangleF(cr.X + Theme.S(8), cr.Y + Theme.S(8), cr.Width - Theme.S(16), cr.Height - Theme.S(16)), color, 2.4f * Theme.Dpi);
            TextRenderer.DrawText(g, title, Theme.Semi(11f), new Rectangle(Theme.S(64), Theme.S(12), Width - Theme.S(76), Theme.S(24)), Theme.Text, Gfx.Left);
            TextRenderer.DrawText(g, text, Theme.F(9f), new Rectangle(Theme.S(64), Theme.S(36), Width - Theme.S(76), Theme.S(22)), Theme.TextDim, Gfx.Left);
            float prog = (float)Math.Min(1, (Anim.Now - shownAt) / 2.6);
            using (var b = new SolidBrush(Gfx.Alpha(color, 180))) g.FillRectangle(b, Theme.S(5), Height - Theme.S(3), (Width - Theme.S(5)) * (1 - prog), Theme.S(3));
        }
    }

    // ======================= Farbwahl =======================

    /// <summary>Farbkreise zum Auswählen + "Eigene Farbe".</summary>
    public sealed class SwatchPicker : Control, IFxControl
    {
        Color value;
        public Color Value { get { return value; } set { this.value = value; Invalidate(); } }
        int hot = -1;
        readonly Tween pop;
        public event EventHandler ValueChanged;

        public SwatchPicker()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            pop = new Tween(this, 1, 10);
            Height = Theme.S(40);
            Width = (Theme.Presets.Length + 1) * Theme.S(38) + Theme.S(4);
            Cursor = Cursors.Hand;
        }

        Rectangle Cell(int i) { return new Rectangle(Theme.S(2) + i * Theme.S(38), Theme.S(4), Theme.S(32), Theme.S(32)); }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int n = -1;
            for (int i = 0; i <= Theme.Presets.Length; i++) if (Cell(i).Contains(e.Location)) n = i;
            if (n != hot) { hot = n; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hot = -1; Invalidate(); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (hot < 0) return;
            if (hot < Theme.Presets.Length) Set(Theme.Presets[hot].Value);
            else
            {
                using (var d = new ColorDialog { Color = Value, FullOpen = true, AnyColor = true })
                    if (d.ShowDialog(FindForm()) == DialogResult.OK) Set(d.Color);
            }
        }

        void Set(Color c)
        {
            Value = Color.FromArgb(255, c);
            pop.Set(0.6f);
            pop.To(1);
            var h = ValueChanged;
            if (h != null) h(this, EventArgs.Empty);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.EffectiveBack(Parent));
            bool any = false;
            for (int i = 0; i <= Theme.Presets.Length; i++)
            {
                var r = (RectangleF)Cell(i);
                bool custom = i == Theme.Presets.Length;
                bool sel = !custom && Theme.Presets[i].Value.ToArgb() == Value.ToArgb();
                if (custom) sel = !any;
                if (sel) any = true;
                float grow = (i == hot ? Theme.S(2) : 0) - (sel ? (1 - pop.Value) * Theme.S(6) : 0);
                r.Inflate(grow - Theme.S(3), grow - Theme.S(3));
                if (custom)
                {
                    if (sel) using (var b = new SolidBrush(Value)) g.FillEllipse(b, r);
                    else
                    {
                        using (var b = new SolidBrush(Theme.Panel2)) g.FillEllipse(b, r);
                        using (var p = new Pen(Theme.TextDim, 1.4f * Theme.Dpi))
                        {
                            float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2, k = r.Width * 0.22f;
                            g.DrawLine(p, cx - k, cy, cx + k, cy);
                            g.DrawLine(p, cx, cy - k, cx, cy + k);
                        }
                    }
                }
                else using (var b = new SolidBrush(Theme.Presets[i].Value)) g.FillEllipse(b, r);
                if (sel)
                {
                    var rr = r; rr.Inflate(Theme.S(3), Theme.S(3));
                    using (var p = new Pen(Color.White, 2f * Theme.Dpi)) g.DrawEllipse(p, rr);
                    if (!custom || sel) Gfx.Check(g, new RectangleF(r.X + r.Width * 0.22f, r.Y + r.Height * 0.22f, r.Width * 0.56f, r.Height * 0.56f),
                        Gfx.Lum(Value) > 0.62f ? Color.FromArgb(20, 20, 20) : Color.White, 2f * Theme.Dpi);
                }
            }
        }
    }
}

namespace ReplayClip
{
    /// <summary>Umschalter für die drei Modi (Rewind / Aufnahme / Screenshot) in der Kopfleiste – mit gleitender Markierung.</summary>
    public sealed class ModeSwitch : Control, IFxControl
    {
        public string[] Items = { "Rewind", "Aufnahme", "Screenshot" };
        public string[] Icons = { "record", "video", "camera" };
        int sel, hot = -1;
        readonly Tween pos;
        public event Action<int> Changed;

        public ModeSwitch()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            pos = new Tween(this, 0, 14);
            pos.Changed = Invalidate;
            Size = new Size(Theme.S(360), Theme.S(40));
            Cursor = Cursors.Hand;
        }

        public int Selected
        {
            get { return sel; }
            set { sel = Math.Max(0, Math.Min(Items.Length - 1, value)); pos.To(sel); Invalidate(); }
        }

        RectangleF Seg(float i)
        {
            float w = (Width - Theme.S(8)) / (float)Items.Length;
            return new RectangleF(Theme.S(4) + i * w, Theme.S(4), w, Height - Theme.S(8));
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int n = -1;
            for (int i = 0; i < Items.Length; i++) if (Seg(i).Contains(e.Location)) n = i;
            if (n != hot) { hot = n; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hot = -1; Invalidate(); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (hot < 0 || hot == sel) return;
            var h = Changed;
            if (h != null) h(hot);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            ButtonRenderer.DrawParentBackground(g, ClientRectangle, this);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            Gfx.FillRound(g, Gfx.Alpha(Theme.Panel2, 230), r, r.Height / 2);
            Gfx.DrawRound(g, Theme.Border, r, r.Height / 2, 1f);
            var pill = Seg(pos.Value);
            using (var b = Gfx.Gradient(pill, Theme.Accent, Theme.Secondary, 0)) Gfx.FillRound(g, b, pill, pill.Height / 2);
            for (int i = 0; i < Items.Length; i++)
            {
                var s = Seg(i);
                float on = Math.Max(0, 1 - Math.Abs(pos.Value - i));
                if (i == hot && i != sel) Gfx.FillRound(g, Gfx.Alpha(Theme.Hover, 160), s, s.Height / 2);
                var c = Gfx.Lerp(i == hot ? Theme.Text : Theme.TextDim, Color.White, on);
                var f = on > 0.5f ? Theme.Semi(9.5f) : Theme.Medium(9.5f);
                string icon = Theme.Icon(Icons[i]);
                var iconFont = Theme.IconFont(10f);
                var isz = Gfx.Measure(icon, iconFont);
                var tsz = Gfx.Measure(Items[i], f);
                int total = isz.Width + Theme.S(6) + tsz.Width;
                int x = (int)(s.X + (s.Width - total) / 2);
                var rr = Rectangle.Round(s);
                TextRenderer.DrawText(g, icon, iconFont, new Rectangle(x, rr.Y, isz.Width + 2, rr.Height), c, Gfx.Left);
                TextRenderer.DrawText(g, Items[i], f, new Rectangle(x + isz.Width + Theme.S(6), rr.Y, tsz.Width + 4, rr.Height), c, Gfx.Left);
            }
        }
    }
}
