using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace ReplayClip
{
    public sealed class EditorState
    {
        public double Duration = 1, In, Out = 1, Pos, ViewStart, ViewLen = 1;
        public double HoverT = -1;
        public readonly List<string> Names = new List<string>();
        public readonly List<bool> Enabled = new List<bool>();
        public readonly List<int> Volume = new List<int>();
        public float[][] Waves;
        public List<KeyValuePair<double, Bitmap>> Film;
        public bool Playing, Loop;

        public void ClampView()
        {
            ViewLen = Math.Max(0.5, Math.Min(Duration, ViewLen));
            ViewStart = Math.Max(0, Math.Min(Duration - ViewLen, ViewStart));
        }

        public static string Fmt(double t)
        {
            if (t < 0) t = 0;
            int m = (int)(t / 60);
            double s = t - m * 60;
            return m + ":" + s.ToString("00.00", CultureInfo.InvariantCulture);
        }

        public Bitmap FilmAt(double t)
        {
            var f = Film;
            if (f == null || f.Count == 0) return null;
            Bitmap best = f[0].Value;
            foreach (var kv in f) { if (kv.Key <= t + 0.05) best = kv.Value; else break; }
            return best;
        }
    }

    /// <summary>Vorschaufläche: zeigt Bilder per GDI (schnell); während der Wiedergabe zeichnet der Render-Thread direkt hinein.</summary>
    public sealed class VideoView : Control, IFxControl
    {
        public byte[] Frame;
        public int FrameW, FrameH;
        public Bitmap LowRes;
        public float Aspect = 16f / 9f;
        public Func<byte[]> LiveFrame;
        public bool ShowPlayHint = true;
        bool hot;

        public VideoView()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.Opaque | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Black;
            Cursor = Cursors.Hand;
        }

        public Rectangle VideoRect()
        {
            var c = ClientRectangle;
            if (c.Width < 4 || c.Height < 4) return new Rectangle(0, 0, 2, 2);
            int w = c.Width, h = (int)(w / Aspect);
            if (h > c.Height) { h = c.Height; w = (int)(h * Aspect); }
            w &= ~1; h &= ~1;
            return new Rectangle((c.Width - w) / 2, (c.Height - h) / 2, Math.Max(2, w), Math.Max(2, h));
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hot = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hot = false; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var vr = VideoRect();
            using (var b = new SolidBrush(Color.Black))
            {
                g.FillRectangle(b, 0, 0, Width, vr.Top);
                g.FillRectangle(b, 0, vr.Bottom, Width, Height - vr.Bottom);
                g.FillRectangle(b, 0, vr.Top, vr.Left, vr.Height);
                g.FillRectangle(b, vr.Right, vr.Top, Width - vr.Right, vr.Height);
            }
            byte[] f = LiveFrame != null ? LiveFrame() : null;
            int fw = FrameW, fh = FrameH;
            if (f == null) f = Frame;
            if (f != null && fw > 0 && f.Length >= fw * fh * 4)
            {
                bool ok = false;
                if (Blitter.Available)
                {
                    try
                    {
                        IntPtr hdc = g.GetHdc();
                        try { Blitter.Draw(hdc, vr, f, fw, fh); ok = true; }
                        finally { g.ReleaseHdc(hdc); }
                    }
                    catch { Blitter.Available = false; }
                }
                if (!ok) Blitter.DrawGdiPlus(g, vr, f, fw, fh);
            }
            else if (LowRes != null)
            {
                g.InterpolationMode = InterpolationMode.Bilinear;
                g.DrawImage(LowRes, vr);
            }
            else using (var b = new SolidBrush(Color.FromArgb(10, 11, 14))) g.FillRectangle(b, vr);

            if (ShowPlayHint && LiveFrame == null)
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                float pr = Theme.S(hot ? 34 : 28);
                var pc = new RectangleF(vr.X + vr.Width / 2f - pr, vr.Y + vr.Height / 2f - pr, pr * 2, pr * 2);
                using (var b = new SolidBrush(Gfx.Alpha(Color.Black, hot ? 150 : 90))) g.FillEllipse(b, pc);
                using (var p = new Pen(Gfx.Alpha(Color.White, hot ? 230 : 140), 2f * Theme.Dpi)) g.DrawEllipse(p, pc);
                using (var b = new SolidBrush(Gfx.Alpha(Color.White, hot ? 255 : 170)))
                {
                    float k = pr * 0.42f;
                    g.FillPolygon(b, new[] { new PointF(pc.X + pr - k * 0.65f, pc.Y + pr - k), new PointF(pc.X + pr - k * 0.65f, pc.Y + pr + k), new PointF(pc.X + pr + k * 1.05f, pc.Y + pr) });
                }
            }
        }
    }

    /// <summary>Zeitleiste: Lineal, Filmstreifen, Wellenform je Spur, Auswahl (In/Out), Abspielposition. Mausrad = Zoom.</summary>
    public sealed class TimelineView : Control, IFxControl
    {
        readonly EditorState st;
        public int RulerH, FilmH, RowH;
        enum DragMode { None, In, Out, Scrub }
        DragMode drag;
        Bitmap layer;
        string layerKey;
        public event Action<double> Scrub;
        public event Action<double> ScrubEnd;
        public event Action RangeChanged;
        public event Action<int, double> Zoom;      // Mausrad-Delta, Zeit unter der Maus
        public event Action<double> Pan;
        public event Action<double> MarkerMoved;

        public TimelineView(EditorState s)
        {
            st = s;
            RulerH = Theme.S(30);
            FilmH = Theme.S(58);
            RowH = Theme.S(60);
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = Theme.Bg;
            Cursor = Cursors.IBeam;
        }

        public int NeededHeight(int tracks) { return RulerH + FilmH + tracks * RowH + Theme.S(2); }

        float X(double t) { return (float)((t - st.ViewStart) / st.ViewLen * Width); }
        double T(int x) { return Math.Max(0, Math.Min(st.Duration, st.ViewStart + x / (double)Math.Max(1, Width) * st.ViewLen)); }

        public void InvalidateLayer() { layerKey = null; Invalidate(); }

        string Key()
        {
            var sb = new StringBuilder();
            sb.Append(Width).Append('x').Append(Height).Append('|').Append(st.ViewStart.ToString("0.0000")).Append('|').Append(st.ViewLen.ToString("0.0000"));
            sb.Append('|').Append(st.Waves == null ? -1 : st.Waves.Length).Append('|').Append(st.Film == null ? -1 : st.Film.Count).Append('|').Append(Theme.Accent.ToArgb());
            for (int i = 0; i < st.Names.Count; i++) sb.Append('|').Append(st.Enabled[i] ? 1 : 0).Append(st.Volume[i]);
            return sb.ToString();
        }

        void BuildLayer()
        {
            if (Width < 2 || Height < 2) return;
            string key = Key();
            if (layer != null && key == layerKey) return;
            if (layer == null || layer.Width != Width || layer.Height != Height)
            {
                if (layer != null) layer.Dispose();
                layer = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            }
            layerKey = key;
            using (var g = Graphics.FromImage(layer))
            {
                g.SmoothingMode = SmoothingMode.None;
                g.Clear(Theme.Bg);
                int w = Width;

                // Lineal
                using (var rb = new SolidBrush(Theme.Panel)) g.FillRectangle(rb, 0, 0, w, RulerH);
                double[] steps = { 0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600 };
                double step = steps[steps.Length - 1];
                foreach (var s in steps) if (s / st.ViewLen * w >= 90 * Theme.Dpi) { step = s; break; }
                var font = Theme.Medium(7.5f);
                using (var pen = new Pen(Theme.Border))
                using (var minor = new Pen(Gfx.Alpha(Theme.Border, 120)))
                {
                    double t0 = Math.Floor(st.ViewStart / step) * step;
                    for (double t = t0; t <= st.ViewStart + st.ViewLen + step; t += step)
                    {
                        float x = X(t);
                        if (x < -60 || x > w + 60) continue;
                        g.DrawLine(pen, x, RulerH - Theme.S(9), x, RulerH);
                        for (int k = 1; k < 5; k++) { float xm = X(t + step * k / 5); g.DrawLine(minor, xm, RulerH - Theme.S(4), xm, RulerH); }
                        string lbl = step < 1 ? EditorState.Fmt(t) : EditorState.Fmt(t).Replace(".00", "");
                        TextRenderer.DrawText(g, lbl, font, new Point((int)x + 4, Theme.S(6)), Theme.TextDim, TextFormatFlags.NoPadding);
                    }
                }

                // Filmstreifen
                int fy = RulerH;
                using (var b = new SolidBrush(Color.FromArgb(12, 13, 16))) g.FillRectangle(b, 0, fy, w, FilmH);
                if (st.Film != null && st.Film.Count > 0)
                {
                    int th = FilmH - Theme.S(8), tw = th * 16 / 9;
                    g.InterpolationMode = InterpolationMode.Bilinear;
                    for (int x = 0; x < w; x += tw + 1)
                    {
                        var bmp = st.FilmAt(T(x + tw / 2));
                        if (bmp != null) g.DrawImage(bmp, x, fy + Theme.S(4), tw, th);
                    }
                }
                else TextRenderer.DrawText(g, "Filmstreifen wird geladen …", Theme.F(8.5f), new Rectangle(Theme.S(10), fy, w, FilmH), Theme.TextDim, Gfx.Left);
                using (var p = new Pen(Theme.Border)) g.DrawLine(p, 0, fy + FilmH - 1, w, fy + FilmH - 1);

                // Spuren
                g.SmoothingMode = SmoothingMode.AntiAlias;
                for (int i = 0; i < st.Names.Count; i++)
                {
                    int y = RulerH + FilmH + i * RowH;
                    using (var b = new SolidBrush(i % 2 == 0 ? Theme.Bg : Color.FromArgb(21, 22, 28))) g.FillRectangle(b, 0, y, w, RowH);
                    using (var p = new Pen(Gfx.Alpha(Theme.Border, 140))) g.DrawLine(p, 0, y + RowH - 1, w, y + RowH - 1);
                    var wave = st.Waves != null && i < st.Waves.Length ? st.Waves[i] : null;
                    bool on = st.Enabled[i];
                    float vol = st.Volume[i] / 100f;
                    int mid = y + RowH / 2;
                    float half = RowH / 2f - Theme.S(6);
                    if (wave == null)
                    {
                        TextRenderer.DrawText(g, "Wellenform wird geladen …", Theme.F(8.5f), new Point(Theme.S(10), mid - Theme.S(8)), Theme.TextDim);
                        continue;
                    }
                    Color c = on ? Theme.Palette(i) : Color.FromArgb(80, 85, 95);
                    g.SmoothingMode = SmoothingMode.None;
                    using (var pen = new Pen(Gfx.Alpha(c, 200)))
                    using (var core = new Pen(Gfx.Light(c, 0.35f)))
                    {
                        for (int x = 0; x < w; x++)
                        {
                            double a = st.ViewStart + x / (double)w * st.ViewLen;
                            double bb = st.ViewStart + (x + 1) / (double)w * st.ViewLen;
                            int ia = (int)(a * WaveformLoader.PerSecond), ib = Math.Max(ia + 1, (int)(bb * WaveformLoader.PerSecond));
                            float pk = 0;
                            for (int k = ia; k < ib && k < wave.Length; k++) if (wave[k] > pk) pk = wave[k];
                            pk = (float)Math.Min(1, Math.Sqrt(Math.Min(1f, pk * vol)));   // leicht komprimiert -> leise Stellen sichtbar
                            float hh = Math.Max(0.5f, pk * half);
                            g.DrawLine(pen, x, mid - hh, x, mid + hh);
                            if (hh > 3) g.DrawLine(core, x, mid - hh * 0.35f, x, mid + hh * 0.35f);
                        }
                    }
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (var p = new Pen(Gfx.Alpha(c, 70))) g.DrawLine(p, 0, mid, w, mid);
                    if (!on)
                    {
                        var mr = new RectangleF(Theme.S(8), y + Theme.S(6), Theme.S(56), Theme.S(18));
                        Gfx.FillRound(g, Gfx.Alpha(Theme.Danger, 60), mr, Theme.S(4));
                        TextRenderer.DrawText(g, "STUMM", Theme.Semi(7.5f), Rectangle.Round(mr), Theme.Danger, Gfx.Center);
                    }
                }
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            BuildLayer();
            if (layer != null) g.DrawImageUnscaled(layer, 0, 0);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int w = Width;
            float xin = X(st.In), xout = X(st.Out);
            using (var b = new SolidBrush(Gfx.Alpha(Color.Black, 120)))
            {
                if (xin > 0) g.FillRectangle(b, 0, RulerH, xin, Height - RulerH);
                if (xout < w) g.FillRectangle(b, xout, RulerH, w - xout, Height - RulerH);
            }
            using (var b = new SolidBrush(Gfx.Alpha(Theme.Warning, 28))) g.FillRectangle(b, xin, 0, Math.Max(0, xout - xin), RulerH);

            // Hover-Linie
            if (st.HoverT >= 0 && drag == DragMode.None)
            {
                float xh = X(st.HoverT);
                using (var p = new Pen(Gfx.Alpha(Theme.Text, 60))) g.DrawLine(p, xh, RulerH, xh, Height);
            }

            // In/Out
            var wc = Theme.Warning;
            using (var pen = new Pen(wc, 2f * Theme.Dpi))
            {
                g.DrawLine(pen, xin, 0, xin, Height);
                g.DrawLine(pen, xout, 0, xout, Height);
            }
            var flagF = Theme.Semi(7.5f);
            var fi = new RectangleF(xin, 0, Theme.S(16), Theme.S(18));
            var fo = new RectangleF(xout - Theme.S(16), 0, Theme.S(16), Theme.S(18));
            Gfx.FillRound(g, wc, fi, Theme.S(3));
            Gfx.FillRound(g, wc, fo, Theme.S(3));
            TextRenderer.DrawText(g, "I", flagF, Rectangle.Round(fi), Color.Black, Gfx.Center);
            TextRenderer.DrawText(g, "O", flagF, Rectangle.Round(fo), Color.Black, Gfx.Center);

            // Abspielposition
            float xp = X(st.Pos);
            using (var b = new SolidBrush(Gfx.Alpha(Theme.Accent, 50))) g.FillRectangle(b, xp - Theme.S(3), RulerH, Theme.S(6), Height - RulerH);
            using (var pen = new Pen(Color.White, 1.6f * Theme.Dpi)) g.DrawLine(pen, xp, RulerH - Theme.S(4), xp, Height);
            string tl = EditorState.Fmt(st.Pos);
            var tf = Theme.Semi(7.5f);
            var ts = Gfx.Measure(tl, tf);
            var bubble = new RectangleF(xp - ts.Width / 2f - Theme.S(6), Theme.S(3), ts.Width + Theme.S(12), RulerH - Theme.S(10));
            if (bubble.X < 0) bubble.X = 0;
            if (bubble.Right > w) bubble.X = w - bubble.Width;
            Gfx.FillRound(g, Theme.Accent, bubble, Theme.S(4));
            TextRenderer.DrawText(g, tl, tf, Rectangle.Round(bubble), Theme.OnAccent, Gfx.Center);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            if (e.Button != MouseButtons.Left) return;
            float tol = 8 * Theme.Dpi;
            if (Math.Abs(e.X - X(st.In)) <= tol && (e.Y < RulerH || Math.Abs(e.X - X(st.In)) < Math.Abs(e.X - X(st.Out)))) drag = DragMode.In;
            else if (Math.Abs(e.X - X(st.Out)) <= tol) drag = DragMode.Out;
            else { drag = DragMode.Scrub; DoScrub(e.X); }
            Capture = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            float tol = 8 * Theme.Dpi;
            st.HoverT = T(e.X);
            if (drag == DragMode.None)
            {
                Cursor = (Math.Abs(e.X - X(st.In)) <= tol || Math.Abs(e.X - X(st.Out)) <= tol) ? Cursors.SizeWE : Cursors.IBeam;
                Invalidate();
                return;
            }
            double t = T(e.X);
            if (drag == DragMode.In) { st.In = Math.Max(0, Math.Min(t, st.Out - 0.1)); Fire(); Marker(st.In); }
            else if (drag == DragMode.Out) { st.Out = Math.Min(st.Duration, Math.Max(t, st.In + 0.1)); Fire(); Marker(st.Out); }
            else DoScrub(e.X);
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); st.HoverT = -1; Invalidate(); }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (drag == DragMode.Scrub) { var h = ScrubEnd; if (h != null) h(T(e.X)); }
            drag = DragMode.None;
            Capture = false;
        }

        void Fire()
        {
            Invalidate();
            var h = RangeChanged;
            if (h != null) h();
        }

        void Marker(double t) { var h = MarkerMoved; if (h != null) h(t); }

        void DoScrub(int x) { var h = Scrub; if (h != null) h(T(x)); }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if ((ModifierKeys & Keys.Shift) != 0) { var p = Pan; if (p != null) p(-e.Delta / 120.0 * st.ViewLen * 0.15); }
            else { var z = Zoom; if (z != null) z(e.Delta, T(e.X)); }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && layer != null) layer.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>Spurköpfe links neben der Zeitleiste: Farbe, Name, Stumm (M) und Lautstärke.</summary>
    public sealed class TrackHeaders : Panel, IFxControl
    {
        readonly EditorState st;
        readonly TimelineView tl;
        public event Action Changed;
        readonly List<FxButton> mutes = new List<FxButton>();
        readonly List<FxSlider> sliders = new List<FxSlider>();

        public TrackHeaders(EditorState s, TimelineView timeline)
        {
            st = s; tl = timeline;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Panel;
        }

        public void Rebuild()
        {
            SuspendLayout();
            Controls.Clear();
            mutes.Clear();
            sliders.Clear();
            for (int i = 0; i < st.Names.Count; i++)
            {
                int idx = i;
                int y = tl.RulerH + tl.FilmH + i * tl.RowH;
                var m = new FxButton { Text = "M", Size = new Size(Theme.S(28), Theme.S(24)), Location = new Point(Width - Theme.S(40), y + Theme.S(7)), Font = Theme.Semi(8f), Anchor = AnchorStyles.Top | AnchorStyles.Right, Padding = new Padding(0) };
                m.Toggled = !st.Enabled[i];
                m.Click += (s, e) => { st.Enabled[idx] = !st.Enabled[idx]; m.Toggled = !st.Enabled[idx]; Invalidate(); var h = Changed; if (h != null) h(); };
                Tips.Set(m, "Stumm (fällt beim Speichern raus)");
                var sl = new FxSlider { Minimum = 0, Maximum = 500, Value = st.Volume[i], DefaultValue = 100, SmallChange = 5, Location = new Point(Theme.S(18), y + Theme.S(32)), Size = new Size(Width - Theme.S(78), Theme.S(22)), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
                sl.FillColor = Theme.Palette(i);
                sl.ValueChanged += (s, e) => { st.Volume[idx] = sl.Value; Invalidate(); var h = Changed; if (h != null) h(); };
                Tips.Set(sl, "Lautstärke (Doppelklick = 100 %)");
                Controls.Add(m);
                Controls.Add(sl);
                mutes.Add(m);
                sliders.Add(sl);
            }
            ResumeLayout();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.Panel);
            TextRenderer.DrawText(g, "SPUREN", Theme.Semi(7.5f), new Rectangle(Theme.S(14), 0, Width, tl.RulerH), Theme.TextDim, Gfx.Left);
            int fy = tl.RulerH;
            using (var b = new SolidBrush(Gfx.Lerp(Theme.Panel, Theme.Bg, 0.3f))) g.FillRectangle(b, 0, fy, Width, tl.FilmH);
            Gfx.Icon(g, "video", 11f, new Rectangle(Theme.S(12), fy, Theme.S(22), tl.FilmH), Theme.TextDim);
            TextRenderer.DrawText(g, "Video", Theme.Medium(9f), new Rectangle(Theme.S(40), fy, Width, tl.FilmH), Theme.Text, Gfx.Left);
            for (int i = 0; i < st.Names.Count; i++)
            {
                int y = tl.RulerH + tl.FilmH + i * tl.RowH;
                var c = Theme.Palette(i);
                bool on = st.Enabled[i];
                using (var p = new Pen(Theme.Border)) g.DrawLine(p, 0, y, Width, y);
                using (var b = new SolidBrush(on ? c : Theme.Border)) g.FillRectangle(b, 0, y + Theme.S(4), Theme.S(4), tl.RowH - Theme.S(8));
                TextRenderer.DrawText(g, (i + 1) + "  " + st.Names[i], Theme.Semi(9f), new Rectangle(Theme.S(14), y + Theme.S(6), Width - Theme.S(110), Theme.S(24)), on ? Theme.Text : Theme.TextDim, Gfx.Left);
                TextRenderer.DrawText(g, st.Volume[i] + " %", Theme.Medium(8f), new Rectangle(Width - Theme.S(84), y + Theme.S(8), Theme.S(40), Theme.S(20)), Theme.TextDim, Gfx.Left | TextFormatFlags.Right);
            }
        }
    }

    /// <summary>Schnitt-Editor: Vorschau, Zeitleiste, Spuren stumm/Lautstärke, Export.</summary>
    public sealed class EditorForm : Form
    {
        string file;
        readonly string ffmpeg;
        readonly EditorState st = new EditorState();
        ClipInfo info;
        public event Action<string, bool> Exported;   // Ziel, Original überschrieben?
        /// <summary>Speichern läuft im Hintergrund weiter, das Fenster ist schon zu: Quelle, Ziel, überschreiben?</summary>
        public event Action<string, string, bool> ExportStarted;
        public event Action<string, int> ExportProgress;      // Ziel, Prozent
        public event Action<string, string> ExportFailed;     // Ziel, Fehler
        bool background;
        static int Px(float v) { return Theme.S(v); }

        VideoView video;
        PreviewPlayback playback;
        double playUntil, playFrom;
        FrameGrabber grabber;
        DateTime restartAt = DateTime.MaxValue;
        int loadToken;

        FxButton btnPlay, btnLoop;
        Label lblTime, lblSel;
        TimelineView timeline;
        TrackHeaders headers;
        FxScrollBar hscroll;
        TableLayoutPanel root, tlArea;
        FxRadio rbSeparate, rbMix, rbMixPlus;
        FxToggle chkPrecise;
        FxButton btnSaveNew, btnOverwrite;
        FxProgress progress;
        Label lblExport;
        System.Windows.Forms.Timer timer;
        bool exporting;
        double zoomStart, zoomLen;
        bool zoomRunning;
        Size lastVideoSize;

        public EditorForm(string ffmpegPath, string clip)
        {
            ffmpeg = ffmpegPath;
            file = clip;
            AutoScaleMode = AutoScaleMode.None;
            Font = Theme.F(9f);
            Text = "Clip bearbeiten – " + Path.GetFileName(file) + "  ·  ReplayClip " + Program.Version;
            Size = new Size(Px(1280), Px(920));
            MinimumSize = new Size(Px(960), Px(680));
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Theme.DarkTitleBar(this);
            BuildUi();
            Theme.Apply(this);
            timer = new System.Windows.Forms.Timer { Interval = 15 };
            timer.Tick += (s, e) => Tick();
            Load += (s, e) => LoadClip();
        }

        // ======================= Aufbau =======================

        void BuildUi()
        {
            root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(Px(12), Px(12), Px(12), Px(10)), BackColor = Theme.Bg };
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, Px(300)));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            video = new VideoView { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, Px(8)) };
            video.Click += (s, e) => TogglePlay();
            video.Resize += (s, e) => OnVideoResize();

            // Transportleiste
            var tr = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3, Margin = new Padding(0, 0, 0, Px(8)), BackColor = Theme.Bg };
            tr.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            tr.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            tr.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            var leftFlow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Left, BackColor = Theme.Bg };
            lblTime = new Label { AutoSize = true, Font = Theme.Mono(13f), ForeColor = Theme.Text, Margin = new Padding(Px(4), Px(6), Px(10), 0), Text = "0:00.00", Tag = "keep" };
            var lblDur = new Label { AutoSize = true, Font = Theme.Mono(10f), ForeColor = Theme.TextDim, Margin = new Padding(0, Px(10), 0, 0), Tag = "keep", Name = "dur" };
            leftFlow.Controls.Add(lblTime);
            leftFlow.Controls.Add(lblDur);

            var mid = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.None, BackColor = Theme.Bg };
            mid.Controls.Add(Ui.IconButton("prev", "Zum Anfang der Auswahl (Pos1)", (s, e) => SeekTo(st.In)));
            mid.Controls.Add(Ui.IconButton("back", "1 s zurück (←)", (s, e) => SeekTo(st.Pos - 1)));
            btnPlay = new FxButton { IconName = "play", Kind = FxKind.Primary, Size = new Size(Px(54), Px(42)), IconSize = 14f, Radius = 21, Margin = new Padding(Px(6), 0, Px(6), 0) };
            btnPlay.Click += (s, e) => TogglePlay();
            Tips.Set(btnPlay, "Abspielen / Pause (Leertaste)");
            mid.Controls.Add(btnPlay);
            mid.Controls.Add(Ui.IconButton("forward", "1 s vor (→)", (s, e) => SeekTo(st.Pos + 1)));
            mid.Controls.Add(Ui.IconButton("next", "Zum Ende der Auswahl (Ende)", (s, e) => SeekTo(st.Out)));
            btnLoop = Ui.IconButton("loop", "Auswahl in Schleife (L)", (s, e) => ToggleLoop());
            mid.Controls.Add(btnLoop);

            var rightFlow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Right, BackColor = Theme.Bg };
            var bIn = new FxButton { Text = "Anfang  I", AutoSize = true };
            bIn.Click += (s, e) => SetIn();
            Tips.Set(bIn, "Anfang der Auswahl an der Abspielposition setzen (I)");
            var bOut = new FxButton { Text = "Ende  O", AutoSize = true };
            bOut.Click += (s, e) => SetOut();
            Tips.Set(bOut, "Ende der Auswahl an der Abspielposition setzen (O)");
            var bSel = Ui.Button("play", "Auswahl", (s, e) => PlaySelection());
            Tips.Set(bSel, "Nur die Auswahl abspielen");
            var bReset = Ui.IconButton("refresh", "Auswahl zurücksetzen", (s, e) => { st.In = 0; st.Out = st.Duration; RangeUpdated(); });
            rightFlow.Controls.Add(bIn);
            rightFlow.Controls.Add(bOut);
            rightFlow.Controls.Add(bSel);
            rightFlow.Controls.Add(bReset);
            tr.Controls.Add(leftFlow, 0, 0);
            tr.Controls.Add(mid, 1, 0);
            tr.Controls.Add(rightFlow, 2, 0);

            // Zeitleiste
            var tlCard = new Card { Dock = DockStyle.Fill, Padding = new Padding(Px(1)), Margin = new Padding(0, 0, 0, Px(8)) };
            tlArea = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = new Padding(0), BackColor = Theme.Panel };
            tlArea.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Px(250)));
            tlArea.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            tlArea.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            tlArea.RowStyles.Add(new RowStyle(SizeType.Absolute, Px(34)));
            timeline = new TimelineView(st) { Dock = DockStyle.Fill, Margin = new Padding(0) };
            timeline.Scrub += t => Scrub(t);
            timeline.ScrubEnd += t => SeekTo(t);
            timeline.RangeChanged += RangeUpdated;
            timeline.Zoom += (d, t) => ZoomAt(d > 0 ? 0.7 : 1 / 0.7, t);
            timeline.Pan += dt => AnimateView(zoomTargetStart() + dt, zoomTargetLen());
            timeline.MarkerMoved += t => { if (playback == null) Scrub(t); };
            headers = new TrackHeaders(st, timeline) { Dock = DockStyle.Fill, Margin = new Padding(0) };
            headers.Changed += AudioChanged;
            hscroll = new FxScrollBar { Horizontal = true, Dock = DockStyle.Fill, Margin = new Padding(0, Px(8), 0, Px(8)) };
            hscroll.Scroll += (s, e) => { st.ViewStart = hscroll.Value / 1000.0; zoomStart = st.ViewStart; st.ClampView(); timeline.Invalidate(); };
            var zoom = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Margin = new Padding(Px(6), Px(1), 0, 0), BackColor = Theme.Panel };
            zoom.Controls.Add(Ui.IconButton("zoom", "Alles zeigen", (s, e) => AnimateView(0, st.Duration)));
            zoom.Controls.Add(new FxButton { Text = "Auswahl", AutoSize = true, Kind = FxKind.Ghost, Height = Px(30) });
            zoom.Controls[1].Click += (s, e) => ZoomToSelection();
            zoom.Controls.Add(new Label { Text = "Mausrad = Zoom", AutoSize = true, ForeColor = Theme.TextDim, Font = Theme.F(8f), Margin = new Padding(Px(4), Px(9), 0, 0), Tag = "keep" });
            tlArea.Controls.Add(headers, 0, 0);
            tlArea.Controls.Add(timeline, 1, 0);
            tlArea.Controls.Add(zoom, 0, 1);
            tlArea.Controls.Add(hscroll, 1, 1);
            tlCard.Controls.Add(tlArea);

            // Export
            var ex = new Card { Text = "Speichern", Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0) };
            var exl = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            var r1 = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            r1.Controls.Add(new Label { Text = "Ton:", AutoSize = true, Margin = new Padding(0, Px(5), Px(10), 0), ForeColor = Theme.TextDim, Tag = "keep" });
            rbMixPlus = new FxRadio { Text = "Mix als Spur 1 + einzelne Spuren", Checked = true, Margin = new Padding(0, 0, Px(18), 0) };
            rbMix = new FxRadio { Text = "Alles in eine Tonspur mischen", Margin = new Padding(0, 0, Px(18), 0) };
            rbSeparate = new FxRadio { Text = "Spuren einzeln behalten" };
            r1.Controls.Add(rbMixPlus);
            r1.Controls.Add(rbMix);
            r1.Controls.Add(rbSeparate);
            chkPrecise = new FxToggle { Text = "Bildgenau schneiden (Video wird mit der Grafikkarte neu kodiert – dauert nur Sekunden). Aus = verlustfrei, springt aber auf Keyframes (alle 2 s).", Checked = true, MaximumSize = new Size(Px(900), 0), Margin = new Padding(0, Px(8), 0, Px(4)) };
            var r3 = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, Px(6), 0, 0) };
            btnSaveNew = new FxButton { Text = "Als neuen Clip speichern", IconName = "save", Kind = FxKind.Primary, AutoSize = true, Height = Px(38) };
            btnSaveNew.Click += (s, e) => Export(false);
            btnOverwrite = new FxButton { Text = "Original überschreiben", IconName = "export", AutoSize = true, Height = Px(38) };
            btnOverwrite.Click += (s, e) => Export(true);
            progress = new FxProgress { Width = Px(240), Visible = false, Margin = new Padding(Px(14), Px(15), 0, 0) };
            lblExport = new Label { AutoSize = true, Margin = new Padding(Px(12), Px(11), 0, 0), Tag = "keep", ForeColor = Theme.TextDim };
            lblSel = new Label { AutoSize = true, Margin = new Padding(Px(18), Px(11), 0, 0), Tag = "keep", ForeColor = Theme.Text, Font = Theme.Medium(9f) };
            r3.Controls.Add(btnSaveNew);
            r3.Controls.Add(btnOverwrite);
            r3.Controls.Add(lblSel);
            r3.Controls.Add(progress);
            r3.Controls.Add(lblExport);
            exl.Controls.Add(r1);
            exl.Controls.Add(chkPrecise);
            exl.Controls.Add(r3);
            ex.Controls.Add(exl);

            root.Controls.Add(video, 0, 0);
            root.Controls.Add(tr, 0, 1);
            root.Controls.Add(tlCard, 0, 2);
            root.Controls.Add(ex, 0, 3);
            Controls.Add(root);
        }

        void UpdateTimelineHeight()
        {
            int h = timeline.NeededHeight(st.Names.Count) + Px(34) + Px(4);
            root.RowStyles[2] = new RowStyle(SizeType.Absolute, h + Px(8));
        }

        // ======================= Laden =======================

        public void SwitchTo(string newFile)
        {
            StopPlayback();
            file = newFile;
            Text = "Clip bearbeiten – " + Path.GetFileName(file) + "  ·  ReplayClip " + Program.Version;
            LoadClip();
        }

        void LoadClip()
        {
            Cursor = Cursors.WaitCursor;
            info = ClipInfo.Probe(ffmpeg, file);
            Cursor = Cursors.Default;
            if (info.Duration <= 0)
            {
                FxDialog.Info(this, "Clip nicht lesbar", "Der Clip konnte nicht gelesen werden.", "error");
                Close();
                return;
            }
            int token = ++loadToken;
            st.Duration = info.Duration;
            st.In = 0; st.Out = info.Duration; st.Pos = 0;
            st.ViewStart = 0; st.ViewLen = info.Duration;
            zoomStart = 0; zoomLen = info.Duration;
            st.Names.Clear(); st.Enabled.Clear(); st.Volume.Clear();
            st.Waves = null;
            st.Film = null;
            foreach (var n in info.AudioNames) { st.Names.Add(n); st.Enabled.Add(true); st.Volume.Add(100); }
            if (info.Width > 0 && info.Height > 0) video.Aspect = info.Width / (float)info.Height;
            if (st.Names.Count <= 1) { rbSeparate.Checked = true; rbMix.Enabled = rbMixPlus.Enabled = st.Names.Count > 1; }
            UpdateTimelineHeight();
            headers.Rebuild();
            timeline.InvalidateLayer();
            SyncScroll();
            RangeUpdated();

            if (grabber != null) grabber.Stop();
            grabber = new FrameGrabber(ffmpeg, file);
            grabber.FrameReady += (pos, buf, w, h) =>
            {
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        if (playback != null) return;
                        video.Frame = buf; video.FrameW = w; video.FrameH = h;
                        video.Invalidate();
                    }));
                }
                catch { }
            };
            RequestFrame(0);

            string f = file;
            int nTracks = st.Names.Count;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var film = FilmstripLoader.Load(ffmpeg, f, () => token != loadToken || IsDisposed);
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        if (token != loadToken) return;
                        st.Film = film;
                        timeline.InvalidateLayer();
                        if (video.Frame == null && film.Count > 0) { video.LowRes = film[0].Value; video.Invalidate(); }
                    }));
                }
                catch { }
            });
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var waves = WaveformLoader.LoadAll(ffmpeg, f, nTracks);
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        if (token != loadToken) return;
                        st.Waves = waves;
                        timeline.InvalidateLayer();
                    }));
                }
                catch { }
            });
            timer.Start();
        }

        // ======================= Vorschau =======================

        Size TargetSize()
        {
            var vr = video.VideoRect();
            int w = vr.Width, h = vr.Height;
            int maxW = Math.Min(1920, info != null && info.Width > 0 ? info.Width : 1920);
            if (w > maxW) { h = (int)(h * maxW / (double)w); w = maxW; }
            return new Size(Math.Max(16, w & ~1), Math.Max(16, h & ~1));
        }

        void RequestFrame(double t)
        {
            var sz = TargetSize();
            if (grabber != null) grabber.Request(t, sz.Width, sz.Height);
        }

        void OnVideoResize()
        {
            var sz = TargetSize();
            if (sz == lastVideoSize) return;
            lastVideoSize = sz;
            if (playback != null) restartAt = DateTime.UtcNow.AddMilliseconds(250);
            else if (info != null) RequestFrame(st.Pos);
        }

        string PreviewAudioGraph()
        {
            if (st.Names.Count == 0) return null;
            var en = new List<int>();
            for (int i = 0; i < st.Names.Count; i++) if (st.Enabled[i] && st.Volume[i] > 0) en.Add(i);
            if (en.Count == 0) return "[0:a:0]volume=0[a]";
            var labels = new List<string>();
            var names = new List<string>();
            string g = EditorFilters.Build(en, st.Volume, st.Names, ExportAudioMode.MixOnly, labels, names);
            return g.Replace("[mix]", "[a]");
        }

        void StartPlayback(double from, double until)
        {
            StopPlayback();
            if (from >= st.Duration - 0.05) from = st.In;
            st.Pos = from;
            playFrom = from;
            playUntil = Math.Min(until, st.Duration);
            var sz = TargetSize();
            double fps = Math.Max(15, Math.Min(60, info.Fps));
            if ((long)sz.Width * sz.Height > 1600L * 900 && fps > 30 && Environment.ProcessorCount < 8) fps = 30;
            playback = new PreviewPlayback(ffmpeg, file, from, playUntil - from + 0.1, PreviewAudioGraph(), sz.Width, sz.Height, fps, video.Handle, () => video.VideoRect());
            var pb = playback;
            video.LiveFrame = () => pb.Current;
            video.FrameW = sz.Width; video.FrameH = sz.Height;
            st.Playing = true;
            btnPlay.IconName = "pause";
            video.Invalidate();
        }

        void StopPlayback()
        {
            var p = playback;
            playback = null;
            if (p != null)
            {
                var cur = p.Current;
                if (cur != null) { video.Frame = (byte[])cur.Clone(); video.FrameW = p.W; video.FrameH = p.H; }
                p.Stop();
            }
            video.LiveFrame = null;
            st.Playing = false;
            if (btnPlay != null) btnPlay.IconName = "play";
            restartAt = DateTime.MaxValue;
            video.Invalidate();
        }

        void TogglePlay()
        {
            if (playback != null) StopPlayback();
            else if (st.Loop) StartPlayback(st.Pos >= st.In && st.Pos < st.Out - 0.05 ? st.Pos : st.In, st.Out);
            else StartPlayback(st.Pos, st.Duration);
        }

        void PlaySelection() { StartPlayback(st.In, st.Out); }

        void ToggleLoop()
        {
            st.Loop = !st.Loop;
            btnLoop.Toggled = st.Loop;
            if (st.Loop && playback != null) playUntil = Math.Min(playUntil, st.Out);
        }

        /// <summary>Beim Ziehen: sofort Filmstreifen-Bild zeigen, genaues Bild wird nachgeladen.</summary>
        void Scrub(double t)
        {
            t = Math.Max(0, Math.Min(st.Duration, t));
            if (playback != null) StopPlayback();
            st.Pos = t;
            var low = st.FilmAt(t);
            if (low != null) { video.LowRes = low; video.Frame = null; video.Invalidate(); }
            RequestFrame(t);
            UpdateLabels();
            timeline.Invalidate();
        }

        void SeekTo(double t)
        {
            t = Math.Max(0, Math.Min(st.Duration, t));
            bool wasPlaying = playback != null;
            double until = playUntil;
            if (wasPlaying) StopPlayback();
            st.Pos = t;
            EnsureVisible(t);
            if (wasPlaying) StartPlayback(t, until > t ? until : st.Duration);
            else RequestFrame(t);
            UpdateLabels();
            timeline.Invalidate();
        }

        void AudioChanged()
        {
            timeline.InvalidateLayer();
            if (playback != null) restartAt = DateTime.UtcNow.AddMilliseconds(250);
        }

        void Tick()
        {
            var p = playback;
            if (p != null)
            {
                st.Pos = p.StartPos + p.Clock;
                if (p.Ended || st.Pos >= playUntil - 0.01)
                {
                    st.Pos = Math.Min(st.Pos, playUntil);
                    if (st.Loop) StartPlayback(st.In, st.Out);
                    else
                    {
                        StopPlayback();
                        RequestFrame(st.Pos);
                    }
                }
                else if (DateTime.UtcNow >= restartAt)
                {
                    double pos = st.Pos, until = playUntil;
                    StartPlayback(pos, until);
                }
                if (p.Error != null) lblExport.Text = "Vorschau: " + p.Error;
                EnsureVisible(st.Pos);
                UpdateLabels();
                timeline.Invalidate();
            }
        }

        void EnsureVisible(double t)
        {
            if (t < st.ViewStart || t > st.ViewStart + st.ViewLen)
            {
                st.ViewStart = t - st.ViewLen * 0.1;
                st.ClampView();
                zoomStart = st.ViewStart;
                SyncScroll();
            }
        }

        // ---------------- Zoom (weich animiert) ----------------

        double zoomTargetStart() { return zoomRunning ? zoomStart : st.ViewStart; }
        double zoomTargetLen() { return zoomRunning ? zoomLen : st.ViewLen; }

        void ZoomAt(double factor, double t)
        {
            double len = Math.Max(0.5, Math.Min(st.Duration, zoomTargetLen() * factor));
            double startNow = zoomTargetStart(), lenNow = zoomTargetLen();
            double rel = (t - startNow) / lenNow;
            AnimateView(t - rel * len, len);
        }

        void AnimateView(double start, double len)
        {
            len = Math.Max(0.5, Math.Min(st.Duration, len));
            start = Math.Max(0, Math.Min(st.Duration - len, start));
            zoomStart = start; zoomLen = len;
            if (!Anim.Enabled)
            {
                st.ViewStart = start; st.ViewLen = len;
                SyncScroll(); timeline.Invalidate();
                return;
            }
            if (zoomRunning) return;
            zoomRunning = true;
            Anim.Run(() =>
            {
                if (IsDisposed) { zoomRunning = false; return false; }
                float k = 1f - (float)Math.Exp(-16 * Anim.Factor * Anim.Dt);
                st.ViewStart += (zoomStart - st.ViewStart) * k;
                st.ViewLen += (zoomLen - st.ViewLen) * k;
                bool done = Math.Abs(zoomStart - st.ViewStart) < 0.002 * zoomLen && Math.Abs(zoomLen - st.ViewLen) < 0.002 * zoomLen;
                if (done) { st.ViewStart = zoomStart; st.ViewLen = zoomLen; }
                SyncScroll();
                timeline.Invalidate();
                if (done) zoomRunning = false;
                return !done;
            });
        }

        void SyncScroll()
        {
            int max = (int)(st.Duration * 1000);
            int large = Math.Max(1, (int)(st.ViewLen * 1000));
            hscroll.Minimum = 0;
            hscroll.Maximum = Math.Max(0, max);
            hscroll.LargeChange = Math.Min(large, Math.Max(1, max));
            hscroll.SmallChange = Math.Max(1, large / 10);
            hscroll.Value = (int)(st.ViewStart * 1000);
        }

        void ZoomToSelection()
        {
            double len = st.Out - st.In;
            double vl = Math.Max(1, len * 1.2);
            AnimateView(st.In - (vl - len) / 2, vl);
        }

        void SetIn()
        {
            st.In = Math.Max(0, Math.Min(st.Pos, st.Out - 0.1));
            RangeUpdated();
        }

        void SetOut()
        {
            st.Out = Math.Min(st.Duration, Math.Max(st.Pos, st.In + 0.1));
            RangeUpdated();
        }

        void RangeUpdated()
        {
            UpdateLabels();
            timeline.Invalidate();
        }

        void UpdateLabels()
        {
            lblTime.Text = EditorState.Fmt(st.Pos);
            var d = root.Controls.Find("dur", true);
            if (d.Length > 0) d[0].Text = "/ " + EditorState.Fmt(st.Duration);
            lblSel.Text = string.Format(CultureInfo.CurrentCulture, "Auswahl {0} – {1}  ·  {2:0.0} s", EditorState.Fmt(st.In), EditorState.Fmt(st.Out), st.Out - st.In);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (exporting) return base.ProcessCmdKey(ref msg, keyData);
            bool shift = (keyData & Keys.Shift) != 0;
            switch (keyData & Keys.KeyCode)
            {
                case Keys.Space: TogglePlay(); return true;
                case Keys.I: SetIn(); return true;
                case Keys.O: SetOut(); return true;
                case Keys.L: ToggleLoop(); return true;
                case Keys.Left: SeekTo(st.Pos - (shift ? 5 : 1)); return true;
                case Keys.Right: SeekTo(st.Pos + (shift ? 5 : 1)); return true;
                case Keys.Oemcomma: SeekTo(st.Pos - 1.0 / Math.Max(1, info != null ? info.Fps : 30)); return true;
                case Keys.OemPeriod: SeekTo(st.Pos + 1.0 / Math.Max(1, info != null ? info.Fps : 30)); return true;
                case Keys.Home: SeekTo(st.In); return true;
                case Keys.End: SeekTo(st.Out); return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // ======================= Export =======================

        string NewFileName()
        {
            string dir = Path.GetDirectoryName(file);
            string baseName = Path.GetFileNameWithoutExtension(file);
            baseName = Regex.Replace(baseName, @" \(Schnitt( \d+)?\)$", "");
            string p = Path.Combine(dir, baseName + " (Schnitt).mp4");
            int n = 2;
            while (File.Exists(p)) p = Path.Combine(dir, baseName + " (Schnitt " + n++ + ").mp4");
            return p;
        }

        string BuildExportArgs(string outPath, string videoArgs)
        {
            var ci = CultureInfo.InvariantCulture;
            var mode = rbSeparate.Checked ? ExportAudioMode.Separate : (rbMix.Checked ? ExportAudioMode.MixOnly : ExportAudioMode.MixPlusSeparate);
            var en = new List<int>();
            for (int i = 0; i < st.Names.Count; i++) if (st.Enabled[i]) en.Add(i);
            var labels = new List<string>();
            var names = new List<string>();
            string graph = EditorFilters.Build(en, st.Volume, st.Names, mode, labels, names);
            double dur = st.Out - st.In;

            var sb = new StringBuilder("-hide_banner -loglevel error -nostdin -y ");
            sb.Append("-ss ").Append(EditorFilters.T(st.In)).Append(" -t ").Append(EditorFilters.T(dur)).Append(" -i ").Append(Ffmpeg.Q(file)).Append(' ');
            if (graph.Length > 0) sb.Append("-filter_complex \"").Append(graph).Append("\" ");
            sb.Append("-map 0:v:0 ");
            foreach (var l in labels) sb.Append("-map \"").Append(l).Append("\" ");
            if (chkPrecise.Checked) sb.Append(videoArgs);
            else sb.Append("-c:v copy -avoid_negative_ts make_zero ");
            if (labels.Count > 0) sb.Append("-c:a aac -b:a 192k ");
            else sb.Append("-an ");
            for (int i = 0; i < names.Count; i++)
            {
                string n = (names[i] ?? "").Replace("\"", "'");
                sb.AppendFormat(ci, "-metadata:s:a:{0} title=\"{1}\" -metadata:s:a:{0} handler_name=\"{1}\" ", i, n);
            }
            sb.Append("-movflags +faststart -progress pipe:1 -nostats ").Append(Ffmpeg.Q(outPath));
            return sb.ToString();
        }

        void Export(bool overwrite)
        {
            if (exporting) return;
            if (st.Out - st.In < 0.1) { FxDialog.Info(this, "Auswahl zu kurz", "Die Auswahl muss mindestens 0,1 Sekunden lang sein.", "warning"); return; }
            if (overwrite && !FxDialog.Ask(this, "Original überschreiben?", "Das Original wird durch die bearbeitete Version ersetzt. Das lässt sich nicht rückgängig machen.", "Überschreiben", "Abbrechen", true)) return;
            StopPlayback();
            exporting = true;
            btnSaveNew.Enabled = btnOverwrite.Enabled = false;
            progress.Value = 0;
            progress.Visible = true;
            lblExport.ForeColor = Theme.TextDim;
            lblExport.Text = "Speichere …";
            string target = overwrite ? file : NewFileName();
            // Immer erst in eine versteckte Temp-Datei rendern – die Bibliothek sieht den Clip erst, wenn er fertig ist.
            string outPath = Path.Combine(Path.GetDirectoryName(file), "~replayclip_edit_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".mp4");
            // Encoder (NVIDIA/AMD/Intel/Prozessor) wird im Hintergrund bestimmt – Platzhalter jetzt, Optionen später
            const string VENC = "\u0001VENC\u0001";
            string argsTemplate = BuildExportArgs(outPath, VENC);
            bool srcHevc = info.VideoCodec.StartsWith("hevc") || info.VideoCodec.StartsWith("h265");
            string encPref = Settings.Load().Encoder;
            double dur = st.Out - st.In;
            string src = file;
            var gr = grabber;
            bool precise = chkPrecise.Checked;

            ThreadPool.QueueUserWorkItem(_ =>
            {
                string error = null;
                try
                {
                    for (int i = 0; i < 50 && gr != null && gr.Busy; i++) Thread.Sleep(100);
                    string err;
                    int code;
                    if (precise)
                    {
                        bool hevc = srcHevc;
                        var kind = Encoders.Resolve(ffmpeg, encPref, ref hevc);
                        code = RunExport(argsTemplate.Replace(VENC, Encoders.ExportArgs(kind, hevc)), dur, out err);
                        // Grafikkarten-Encoder streikt -> mit dem Prozessor nochmal
                        if (code != 0 && kind != EncKind.Cpu)
                            code = RunExport(argsTemplate.Replace(VENC, Encoders.ExportArgs(EncKind.Cpu, srcHevc)), dur, out err);
                    }
                    else code = RunExport(argsTemplate.Replace(VENC, ""), dur, out err);
                    if (code != 0 || !File.Exists(outPath) || new FileInfo(outPath).Length < 1024)
                        throw new Exception(LastLine(err));
                    if (!overwrite)
                    {
                        if (File.Exists(target)) throw new Exception("Zieldatei existiert bereits: " + Path.GetFileName(target));
                        File.Move(outPath, target);
                    }
                    else
                    {
                        try { File.Replace(outPath, src, null); }
                        catch
                        {
                            File.Delete(src);
                            File.Move(outPath, src);
                        }
                    }
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    try { if (File.Exists(outPath) && outPath != src) File.Delete(outPath); } catch { }
                }
                try { BeginInvoke(new Action(() => ExportDone(target, overwrite, error))); } catch { }
            });

            // Fenster sofort schließen (ausblenden) – gerendert wird im Hintergrund, die Bibliothek zeigt den Fortschritt.
            exportTarget = target;
            background = true;
            var hs = ExportStarted;
            if (hs != null) hs(src, target, overwrite);
            timer.Stop();
            Hide();
        }

        string exportTarget;

        static string LastLine(string s)
        {
            var lines = (s ?? "").Replace("\r", "").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            return lines.Length == 0 ? "FFmpeg-Fehler" : lines[lines.Length - 1];
        }

        int RunExport(string args, double dur, out string err)
        {
            var e = new StringBuilder();
            var p = Ffmpeg.Launch(ffmpeg, args, false, e, false, true);
            string line;
            var rd = p.StandardOutput;
            while ((line = rd.ReadLine()) != null)
            {
                if (line.StartsWith("out_time_us=") || line.StartsWith("out_time_ms="))
                {
                    long us;
                    if (long.TryParse(line.Substring(line.IndexOf('=') + 1), out us) && dur > 0)
                    {
                        int pct = (int)Math.Max(0, Math.Min(100, us / 1e6 / dur * 100));
                        try
                        {
                            BeginInvoke(new Action(() =>
                            {
                                progress.Value = pct; lblExport.Text = "Speichere … " + pct + " %";
                                var hp = ExportProgress;
                                if (hp != null && exportTarget != null) hp(exportTarget, pct);
                            }));
                        }
                        catch { }
                    }
                }
            }
            p.WaitForExit();
            lock (e) err = e.ToString();
            return p.ExitCode;
        }

        void ExportDone(string target, bool overwrite, string error)
        {
            exporting = false;
            btnSaveNew.Enabled = btnOverwrite.Enabled = true;
            progress.Visible = false;
            if (error != null)
            {
                lblExport.ForeColor = Theme.Danger;
                lblExport.Text = "Fehler: " + error;
                SoundFx.Play("error", null, 60, Settings.Load());
                if (background)
                {
                    // Fehler: Editor wieder zeigen, damit man es nochmal versuchen kann
                    background = false;
                    var hf = ExportFailed;
                    if (hf != null) hf(target, error);
                    timer.Start();
                    Show();
                    Activate();
                }
                return;
            }
            lblExport.ForeColor = Theme.Accent;
            lblExport.Text = "✓  Gespeichert: " + Path.GetFileName(target);
            if (!background) Toast.Show(this, "Schnitt gespeichert", Path.GetFileName(target), "check", Theme.Accent);
            var h = Exported;
            if (h != null) h(target, overwrite);
            if (background) { Close(); return; }
            if (overwrite) LoadClip();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (exporting)
            {
                FxDialog.Info(this, "Bitte warten", "Der Clip wird gerade gespeichert.", "warning");
                e.Cancel = true;
                return;
            }
            timer.Stop();
            loadToken++;
            StopPlayback();
            if (grabber != null) grabber.Stop();
            base.OnFormClosing(e);
        }
    }
}
