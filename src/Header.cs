using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ReplayClip
{
    /// <summary>
    /// Kopfzeile: Logo (dreht sich beim Starten, pulsiert bei Aufnahme), Titel + Version, Status mit Puffer-Füllstand,
    /// Live-Pegel der Tonspuren und ein schimmernder Akzent-Streifen.
    /// </summary>
    public sealed class HeaderBar : Panel, IFxControl
    {
        public enum RecState { Off, Starting, Recording, Saving }

        RecState state = RecState.Off;
        public string Title = "ReplayClip";
        public string Version = "";
        public string StatusLine = "Bereit";
        public string SubLine = "";
        readonly Tween fill, recFade;
        readonly List<string> meterNames = new List<string>();
        readonly List<float> meterLevels = new List<float>();
        readonly List<float> meterPeaks = new List<float>();
        bool loop;
        double savePulse = -10;
        public int StatusLeft, MetersLeft, MetersWidth;

        public HeaderBar()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Panel;
            fill = new Tween(this, 0, 6);
            recFade = new Tween(this, 0, 8);
            Height = Theme.S(76);
        }

        public RecState State
        {
            get { return state; }
            set
            {
                if (state == value) return;
                state = value;
                recFade.To(value == RecState.Recording || value == RecState.Saving ? 1 : 0);
                if (value == RecState.Saving) savePulse = Anim.Now;
                StartLoop();
                Invalidate();
            }
        }

        public float BufferFill { set { fill.To(Math.Max(0, Math.Min(1, value))); } }

        public void Flash() { savePulse = Anim.Now; StartLoop(); }

        /// <summary>Neue Pegel (0..1 linear) je Spur.</summary>
        public void SetLevels(List<KeyValuePair<string, float>> levels)
        {
            meterNames.Clear();
            while (meterLevels.Count < levels.Count) { meterLevels.Add(0); meterPeaks.Add(0); }
            while (meterLevels.Count > levels.Count) { meterLevels.RemoveAt(meterLevels.Count - 1); meterPeaks.RemoveAt(meterPeaks.Count - 1); }
            for (int i = 0; i < levels.Count; i++)
            {
                meterNames.Add(levels[i].Key);
                float v = ToBar(levels[i].Value);
                if (v > meterLevels[i]) meterLevels[i] = v;
                if (v > meterPeaks[i]) meterPeaks[i] = v;
            }
            StartLoop();
        }

        static float ToBar(float level)
        {
            if (level <= 0.000001f) return 0f;
            double db = 20 * Math.Log10(level);
            return (float)Math.Max(0, Math.Min(1, (db + 60) / 60));
        }

        void StartLoop()
        {
            if (loop || !IsHandleCreated) return;
            loop = true;
            int frame = 0;
            double lastPaint = 0;
            Anim.Run(() =>
            {
                if (IsDisposed) { loop = false; return false; }
                float dt = Anim.Dt;
                bool any = false;
                for (int i = 0; i < meterLevels.Count; i++)
                {
                    meterLevels[i] = Math.Max(0, meterLevels[i] - dt * 1.6f);
                    meterPeaks[i] = Math.Max(meterLevels[i], meterPeaks[i] - dt * 0.35f);
                    if (meterLevels[i] > 0 || meterPeaks[i] > 0) any = true;
                }
                bool live = state != RecState.Off || any || Anim.Now - savePulse < 1.2 || recFade.Value > 0.01f;
                // Ruhige Zustände (Puffer läuft, Pegel) max. ~60 Bilder/s (auch bei 144-Hz-Monitor) – spart Zeit für flüssige Bibliothek.
                // Nur der kurze Speicher-Effekt läuft mit voller Bildrate.
                double now = Anim.Now;
                bool fast = now - savePulse < 1.2 || state == RecState.Starting || state == RecState.Saving;
                double minGap = !Anim.Enabled ? 0.2 : fast ? 0 : 1 / 60.0;
                if (now - lastPaint >= minGap - 0.002) { lastPaint = now; Invalidate(); }
                frame++;
                if (!live) { loop = false; Invalidate(); }
                return live;
            });
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int W = Width, H = Height;
            using (var b = new LinearGradientBrush(new Rectangle(0, 0, W, H + 1), Gfx.Light(Theme.Panel, 0.03f), Gfx.Lerp(Theme.Panel, Theme.Bg, 0.5f), 90f))
                g.FillRectangle(b, 0, 0, W, H);
            double now = Anim.Now;
            float rec = recFade.Value;

            // Akzent-Glow links oben (bei Aufnahme rötlich)
            using (var path = new GraphicsPath())
            {
                path.AddEllipse(-Theme.S(120), -Theme.S(160), Theme.S(420), Theme.S(300));
                using (var pb = new PathGradientBrush(path))
                {
                    pb.CenterColor = Gfx.Alpha(Gfx.Lerp(Theme.Accent, Theme.Rec, rec * 0.6f), 38);
                    pb.SurroundColors = new[] { Color.FromArgb(0, 0, 0, 0) };
                    g.FillPath(pb, path);
                }
            }

            // Logo
            int lx = Theme.S(20), ly = H / 2 - Theme.S(18), ls = Theme.S(36);
            var lr = new RectangleF(lx, ly, ls, ls);
            float spin = state == RecState.Starting ? (float)((now * 360) % 360) : 0;
            using (var p = new Pen(Theme.Panel2, Theme.S(4))) g.DrawEllipse(p, lr.X + 2, lr.Y + 2, lr.Width - 4, lr.Height - 4);
            using (var gb = Gfx.Gradient(lr, Theme.Accent, Theme.Secondary, 45))
            using (var p = new Pen(gb, Theme.S(3.5f)))
            {
                p.StartCap = p.EndCap = LineCap.Round;
                g.DrawArc(p, lr.X + 2, lr.Y + 2, lr.Width - 4, lr.Height - 4, -60 + spin, state == RecState.Starting ? 120 : 300);
            }
            float pulse = rec > 0.01f ? 0.5f + 0.5f * (float)Math.Sin(now * 4.2) : 0;
            float dot = Theme.S(10) + pulse * Theme.S(2) * rec;
            var dc = Gfx.Lerp(Theme.TextDim, Theme.Rec, rec);
            if (rec > 0.01f)
                using (var b = new SolidBrush(Gfx.Alpha(Theme.Rec, (int)(70 * pulse * rec)))) g.FillEllipse(b, lr.X + ls / 2f - dot, lr.Y + ls / 2f - dot, dot * 2, dot * 2);
            using (var b = new SolidBrush(dc)) g.FillEllipse(b, lr.X + ls / 2f - dot / 2, lr.Y + ls / 2f - dot / 2, dot, dot);

            // Titel + Version
            var tf = Theme.Semi(15f);
            var ts = Gfx.Measure(Title, tf);
            int tx = lx + ls + Theme.S(14);
            TextRenderer.DrawText(g, Title, tf, new Point(tx, H / 2 - ts.Height / 2 - Theme.S(1)), Theme.Text, TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            var vf = Theme.Semi(7.5f);
            var vs = Gfx.Measure(Version, vf);
            var vr = new RectangleF(tx + ts.Width + Theme.S(8), H / 2f - vs.Height / 2f - Theme.S(2) - Theme.S(4), vs.Width + Theme.S(12), vs.Height + Theme.S(4));
            Gfx.FillRound(g, Gfx.Lerp(Theme.Panel, Theme.Accent, 0.22f), vr, vr.Height / 2);
            TextRenderer.DrawText(g, Version, vf, Rectangle.Round(vr), Gfx.Light(Theme.Accent, 0.3f), Gfx.Center);

            // Status
            int sx = Math.Max((int)vr.Right + Theme.S(36), StatusLeft);
            int sw = Theme.S(250);
            var stf = Theme.Semi(8f);
            Color stc = state == RecState.Recording ? Theme.Rec : state == RecState.Saving ? Theme.Warning : state == RecState.Starting ? Theme.Secondary : Theme.TextDim;
            if (state == RecState.Recording)
            {
                float bl = 0.55f + 0.45f * (float)Math.Abs(Math.Sin(now * 2.2));
                stc = Gfx.Alpha(stc, (int)(255 * bl));
            }
            using (var b = new SolidBrush(stc)) g.FillEllipse(b, sx, H / 2f - Theme.S(19), Theme.S(8), Theme.S(8));
            TextRenderer.DrawText(g, StatusLine.ToUpperInvariant(), stf, new Rectangle(sx + Theme.S(14), H / 2 - Theme.S(25), sw, Theme.S(20)),
                state == RecState.Off ? Theme.TextDim : Gfx.Lerp(stc, Theme.Text, 0.15f), Gfx.Left);
            TextRenderer.DrawText(g, SubLine, Theme.F(8.5f), new Rectangle(sx, H / 2 - Theme.S(5), sw + Theme.S(60), Theme.S(18)), Theme.TextDim, Gfx.Left);
            var bar = new RectangleF(sx, H / 2f + Theme.S(16), sw, Theme.S(4));
            Gfx.FillRound(g, Theme.Panel2, bar, bar.Height / 2);
            if (fill.Value > 0.002f)
            {
                var fr = new RectangleF(bar.X, bar.Y, Math.Max(bar.Height, bar.Width * fill.Value), bar.Height);
                using (var b = Gfx.Gradient(bar, Theme.Accent, Theme.Secondary, 0)) Gfx.FillRound(g, b, fr, bar.Height / 2);
            }
            double sp = now - savePulse;
            if (sp < 1.0)
            {
                float t = (float)(sp / 1.0);
                using (var b = new SolidBrush(Gfx.Alpha(Theme.Warning, (int)(200 * (1 - t))))) g.FillRectangle(b, bar.X, bar.Y - Theme.S(1), bar.Width, bar.Height + Theme.S(2));
            }

            // Live-Pegel
            int mx = MetersLeft > 0 ? MetersLeft : sx + sw + Theme.S(70);
            int mw = MetersWidth > 0 ? MetersWidth : Theme.S(170);
            if (meterNames.Count > 0 && mw > Theme.S(60))
            {
                int rows = Math.Min(4, meterNames.Count);
                int rh = Theme.S(12);
                int top = H / 2 - rows * rh / 2;
                var mf = Theme.Medium(7f);
                for (int i = 0; i < rows; i++)
                {
                    int y = top + i * rh;
                    var c = Theme.Palette(i);
                    TextRenderer.DrawText(g, meterNames[i], mf, new Rectangle(mx, y - Theme.S(1), Theme.S(58), rh), Theme.TextDim, Gfx.Left);
                    var tr = new RectangleF(mx + Theme.S(62), y + Theme.S(3), mw - Theme.S(62), Theme.S(5));
                    Gfx.FillRound(g, Theme.Bg, tr, tr.Height / 2);
                    float v = meterLevels[i];
                    if (v > 0.003f)
                    {
                        var f = new RectangleF(tr.X, tr.Y, Math.Max(tr.Height, tr.Width * v), tr.Height);
                        using (var b = new LinearGradientBrush(new RectangleF(tr.X - 1, tr.Y, tr.Width + 2, tr.Height), c, Theme.Danger, 0f))
                        {
                            b.InterpolationColors = new ColorBlend { Colors = new[] { c, c, Theme.Warning, Theme.Danger }, Positions = new[] { 0f, 0.7f, 0.88f, 1f } };
                            Gfx.FillRound(g, b, f, tr.Height / 2);
                        }
                    }
                    float pk = meterPeaks[i];
                    if (pk > 0.01f) using (var b = new SolidBrush(Gfx.Light(c, 0.4f))) g.FillRectangle(b, tr.X + tr.Width * pk - Theme.S(1), tr.Y - Theme.S(1), Theme.S(2), tr.Height + Theme.S(2));
                }
            }

            // Akzent-Linie unten mit Schimmer
            var line = new RectangleF(0, H - Theme.S(2), W, Theme.S(2));
            using (var b = Gfx.Gradient(line, Gfx.Alpha(Theme.Accent, 200), Gfx.Alpha(Theme.Secondary, 200), 0)) g.FillRectangle(b, line);
            if (rec > 0.01f && Anim.Enabled)
            {
                float ph = (float)((now * 0.35) % 1.0);
                float sx2 = -Theme.S(300) + ph * (W + Theme.S(600));
                using (var b = new LinearGradientBrush(new RectangleF(sx2 - 1, 0, Theme.S(300) + 2, 1), Color.FromArgb(0, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 0f))
                {
                    b.InterpolationColors = new ColorBlend
                    {
                        Colors = new[] { Color.FromArgb(0, 255, 255, 255), Color.FromArgb((int)(220 * rec), 255, 255, 255), Color.FromArgb(0, 255, 255, 255) },
                        Positions = new[] { 0f, 0.5f, 1f }
                    };
                    g.FillRectangle(b, sx2, H - Theme.S(2), Theme.S(300), Theme.S(2));
                }
            }
        }
    }
}
