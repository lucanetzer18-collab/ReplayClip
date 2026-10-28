using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Windows.Forms;

namespace ReplayClip
{
    /// <summary>Vorschaubilder im Speicher (begrenzt), werden bei Bedarf über FFmpeg erzeugt.</summary>
    public sealed class ThumbCache
    {
        readonly Control owner;
        readonly Dictionary<string, Bitmap> cache = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
        readonly LinkedList<string> lru = new LinkedList<string>();
        readonly HashSet<string> pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        const int Max = 500;
        public event Action<string> Loaded;

        public ThumbCache(Control uiOwner) { owner = uiOwner; }

        public Bitmap Get(string path)
        {
            Bitmap b;
            if (cache.TryGetValue(path, out b))
            {
                lru.Remove(path);
                lru.AddFirst(path);
                return b;
            }
            if (pending.Add(path))
                Thumbnailer.Request(path, (video, thumb) => LoadAsync(video, thumb));
            return null;
        }

        public void Invalidate(string path)
        {
            Bitmap b;
            if (cache.TryGetValue(path, out b)) { cache.Remove(path); lru.Remove(path); b.Dispose(); }
            pending.Remove(path);
        }

        void LoadAsync(string video, string thumbPath)
        {
            Bitmap bmp = null;
            try
            {
                using (var ms = new MemoryStream(File.ReadAllBytes(thumbPath)))
                using (var src = Image.FromStream(ms))
                {
                    bmp = new Bitmap(320, 180, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.Clear(Color.Black);
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        float sc = Math.Min(320f / src.Width, 180f / src.Height);
                        int w = (int)(src.Width * sc), h = (int)(src.Height * sc);
                        g.DrawImage(src, (320 - w) / 2, (180 - h) / 2, w, h);
                    }
                }
            }
            catch { }
            if (bmp == null) return;
            Action a = () =>
            {
                pending.Remove(video);
                Bitmap old;
                if (cache.TryGetValue(video, out old)) { old.Dispose(); lru.Remove(video); }
                cache[video] = bmp;
                lru.AddFirst(video);
                while (lru.Count > Max)
                {
                    string last = lru.Last.Value;
                    lru.RemoveLast();
                    Bitmap ob;
                    if (cache.TryGetValue(last, out ob)) { cache.Remove(last); ob.Dispose(); }
                }
                var h = Loaded;
                if (h != null) h(video);
            };
            try
            {
                if (owner.IsHandleCreated && owner.InvokeRequired) owner.BeginInvoke(a);
                else if (owner.IsHandleCreated) a();
            }
            catch { }
        }
    }

    /// <summary>Clip-Ansicht: Kacheln oder Liste, animiert (Hover, Einblenden, weiches Scrollen), Drag &amp; Drop nach außen.</summary>
    public sealed class ClipBrowser : Control, IFxControl
    {
        public List<ClipItem> Items = new List<ClipItem>();
        public bool ListMode;
        public int TileSize = 1;
        public ThumbCache Thumbs;
        public string EmptyText = "Keine Clips.";
        readonly FxScrollBar vbar = new FxScrollBar();
        readonly HashSet<ClipItem> sel = new HashSet<ClipItem>();
        readonly Dictionary<ClipItem, float> hoverVal = new Dictionary<ClipItem, float>();
        readonly Dictionary<string, double> thumbShown = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        ClipItem anchor, focus, hoverItem;
        Point downPt;
        bool downOnItem;
        float scrollPos, scrollTarget;
        bool animRunning;
        double enterStart = -10;
        public int SortColumn = -1;
        // Vorberechnete Grafik: Vorschaubilder fertig skaliert mit runden Ecken, Schatten als Bitmap.
        sealed class Scaled { public Bitmap Src, Bmp; public int W, H; }
        readonly Dictionary<string, Scaled> scaled = new Dictionary<string, Scaled>(StringComparer.OrdinalIgnoreCase);
        readonly LinkedList<string> scaledOrder = new LinkedList<string>();
        readonly Dictionary<long, Bitmap> shadows = new Dictionary<long, Bitmap>();
        Dictionary<string, int> indexByPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        const int ShadowLevels = 12;
        readonly Dictionary<string, int> renderPct = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        bool renderAnim;

        /// <summary>Fortschritt eines Schnitts, der im Hintergrund gerendert wird (-1 = fertig).</summary>
        public void SetRendering(string path, int pct)
        {
            if (pct < 0) renderPct.Remove(path); else renderPct[path] = pct;
            InvalidateItem(path);
            if (renderPct.Count > 0 && !renderAnim && IsHandleCreated && Anim.Enabled)
            {
                renderAnim = true;
                double last = 0;
                Anim.Run(() =>
                {
                    if (IsDisposed || renderPct.Count == 0) { renderAnim = false; return false; }
                    if (Anim.Now - last > 1 / 30.0) { last = Anim.Now; foreach (var k in renderPct.Keys) InvalidateItem(k); }
                    return true;
                });
            }
        }

        string ThumbOf(ClipItem it) { return it.ThumbFrom ?? it.Path; }

        /// <summary>Halbdunkle Abdeckung mit drehendem Ring und Prozent über dem Vorschaubild.</summary>
        void DrawRendering(Graphics g, Rectangle tr, int pct, float rad, bool list)
        {
            using (var p = TopRound(tr, rad, list))
            using (var b = new SolidBrush(Color.FromArgb(170, 8, 9, 12))) g.FillPath(b, p);
            float R = list ? Math.Min(tr.Height, tr.Width) * 0.32f : S(20);
            var c = new RectangleF(tr.X + tr.Width / 2f - R, tr.Y + tr.Height / 2f - R - (list ? 0 : S(8)), R * 2, R * 2);
            float w = (list ? 2.5f : 4f) * Theme.Dpi;
            using (var pen = new Pen(Color.FromArgb(60, 255, 255, 255), w)) g.DrawEllipse(pen, c);
            float spin = (float)(Anim.Now * 300 % 360);
            using (var pen = new Pen(Theme.Accent, w) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawArc(pen, c, -90 + (pct <= 0 ? spin : 0), pct <= 0 ? 90 : Math.Max(6, pct * 3.6f));
            if (!list)
                TextRenderer.DrawText(g, pct > 0 ? "Rendert … " + pct + " %" : "Rendert …", Theme.Semi(8.5f),
                    new Rectangle(tr.X, (int)c.Bottom + S(6), tr.Width, S(20)), Color.White, Gfx.Center);
        }

        public event Action SelectionChanged;
        public event Action<ClipItem> Activated;
        public event Action<Point> MenuRequested;
        public event Action<ClipItem> FavoriteClicked;
        public event Action<int> HeaderClicked;

        public ClipBrowser()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            vbar.Dock = DockStyle.Right;
            vbar.Scroll += (s, e) => { scrollPos = scrollTarget = vbar.Value; Invalidate(); };
            Controls.Add(vbar);
        }

        static int S(float v) { return Theme.S(v); }

        public List<ClipItem> Selected
        {
            get
            {
                var l = new List<ClipItem>();
                foreach (var it in Items) if (sel.Contains(it)) l.Add(it);
                return l;
            }
        }

        public void SetItems(List<ClipItem> items)
        {
            bool changed = items.Count != Items.Count;
            if (!changed)
                for (int i = 0; i < items.Count; i++)
                    if (!string.Equals(items[i].Path, Items[i].Path, StringComparison.OrdinalIgnoreCase)) { changed = true; break; }
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var it in sel) keep.Add(it.Path);
            string focusPath = focus != null ? focus.Path : null;
            Items = items;
            indexByPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < Items.Count; i++) indexByPath[Items[i].Path] = i;
            sel.Clear();
            focus = anchor = null;
            hoverVal.Clear();
            hoverItem = null;
            foreach (var it in Items)
            {
                if (keep.Contains(it.Path)) sel.Add(it);
                if (focusPath != null && string.Equals(it.Path, focusPath, StringComparison.OrdinalIgnoreCase)) focus = anchor = it;
            }
            if (changed)
            {
                scrollPos = scrollTarget = 0;
                enterStart = Anim.Now;
                Animate();
            }
            UpdateScroll();
            Invalidate();
            Fire();
        }

        /// <summary>Layout geändert (Ansicht/Größe) – mit kurzer Einblend-Animation.</summary>
        public void Relayout()
        {
            scrollPos = scrollTarget = 0;
            enterStart = Anim.Now;
            Animate();
            UpdateScroll();
            Invalidate();
        }

        public void ThumbLoaded(string path)
        {
            DropScaled(path);
            thumbShown[path] = Anim.Now;
            InvalidateItem(path);
            Animate();
        }

        public void SelectPath(string path)
        {
            sel.Clear();
            foreach (var it in Items)
                if (string.Equals(it.Path, path, StringComparison.OrdinalIgnoreCase)) { sel.Add(it); focus = anchor = it; EnsureVisible(Items.IndexOf(it)); }
            Invalidate();
            Fire();
        }

        void Fire()
        {
            var h = SelectionChanged;
            if (h != null) h();
        }

        // ---------------- Animation ----------------

        void Animate()
        {
            if (animRunning || !IsHandleCreated) return;
            if (!Anim.Enabled) { scrollPos = scrollTarget; Invalidate(); return; }
            animRunning = true;
            var keys = new List<ClipItem>();
            var done = new List<string>();
            Anim.Run(() =>
            {
                if (IsDisposed) { animRunning = false; return false; }
                bool busy = false, full = false;
                // weiches Scrollen
                if (Math.Abs(scrollTarget - scrollPos) > 0.5f)
                {
                    scrollPos += (scrollTarget - scrollPos) * (1 - (float)Math.Exp(-18 * Anim.Factor * Anim.Dt));
                    busy = full = true;
                }
                else if (scrollPos != scrollTarget) { scrollPos = scrollTarget; full = true; }
                double now = Anim.Now;
                if (now - enterStart < 1.4 / Anim.Factor) busy = full = true;
                else if (now - enterStart < 1.4 / Anim.Factor + 0.1) full = true; // letztes Bild der Einblendung
                // Hover ein-/ausblenden – nur die betroffenen Kacheln neu zeichnen
                keys.Clear();
                keys.AddRange(hoverVal.Keys);
                foreach (var k in keys)
                {
                    float target = k == hoverItem ? 1 : 0;
                    float v = hoverVal[k];
                    float nv = v + (target - v) * (1 - (float)Math.Exp(-16 * Anim.Factor * Anim.Dt));
                    if (Math.Abs(target - nv) < 0.004f) nv = target;
                    if (nv == 0 && target == 0) hoverVal.Remove(k); else hoverVal[k] = nv;
                    if (nv != target) busy = true;
                    if (nv != v && !full) InvalidateItem(k.Path);
                }
                // Vorschaubilder weich einblenden
                done.Clear();
                foreach (var kv in thumbShown)
                {
                    double age = (now - kv.Value) * Anim.Factor;
                    if (age < 0.3) busy = true;
                    if (!full) InvalidateItem(kv.Key);
                    if (age >= 0.3) done.Add(kv.Key);
                }
                foreach (var d in done) thumbShown.Remove(d);
                if (full) Invalidate();
                if (!busy) animRunning = false;
                return busy;
            });
        }

        /// <summary>Bereich einer Kachel inkl. Anheben und Schatten.</summary>
        Rectangle PaintBounds(Rectangle r)
        {
            if (ListMode) return Rectangle.Inflate(r, 1, 1);
            int g = S(6) + 2;
            return new Rectangle(r.X - g, r.Y - g - S(3), r.Width + 2 * g, r.Height + 2 * g + S(3));
        }

        void InvalidateItem(string path)
        {
            int i;
            if (!indexByPath.TryGetValue(path, out i)) return;
            var r = ItemRect(i);
            if (r.Bottom < -S(20) || r.Top > Height + S(20)) return;
            Invalidate(PaintBounds(r));
        }

        // ---------------- Grafik-Cache ----------------

        void DropScaled(string path)
        {
            foreach (var suffix in new[] { "|t", "|l" })
            {
                Scaled sc;
                if (scaled.TryGetValue(path + suffix, out sc)) { scaled.Remove(path + suffix); scaledOrder.Remove(path + suffix); sc.Bmp.Dispose(); }
            }
        }

        static GraphicsPath TopRound(RectangleF r, float rad, bool bottomToo)
        {
            var p = new GraphicsPath();
            float d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            if (bottomToo)
            {
                p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            }
            else
            {
                p.AddLine(r.Right, r.Y + rad, r.Right, r.Bottom);
                p.AddLine(r.Right, r.Bottom, r.X, r.Bottom);
            }
            p.CloseFigure();
            return p;
        }

        /// <summary>Vorschaubild genau in Zielgröße mit runden Ecken (transparent) – wird danach nur noch 1:1 kopiert.</summary>
        Bitmap ScaledThumb(string path, Bitmap src, int w, int h, float rad, bool list)
        {
            if (src == null || w <= 0 || h <= 0) return null;
            string key = path + (list ? "|l" : "|t");
            Scaled sc;
            if (scaled.TryGetValue(key, out sc))
            {
                if (sc.Src == src && sc.W == w && sc.H == h) return sc.Bmp;
                scaled.Remove(key); scaledOrder.Remove(key); sc.Bmp.Dispose();
            }
            var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            try
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    using (var tmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppPArgb))
                    {
                        using (var tg = Graphics.FromImage(tmp))
                        {
                            tg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            tg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                            using (var ia = new System.Drawing.Imaging.ImageAttributes())
                            {
                                ia.SetWrapMode(WrapMode.TileFlipXY);
                                tg.DrawImage(src, new Rectangle(0, 0, w, h), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, ia);
                            }
                        }
                        using (var tb = new TextureBrush(tmp, WrapMode.Clamp))
                        using (var p = TopRound(new RectangleF(0, 0, w, h), rad, list))
                            g.FillPath(tb, p);
                    }
                }
            }
            catch { bmp.Dispose(); return null; }
            scaled[key] = new Scaled { Src = src, Bmp = bmp, W = w, H = h };
            scaledOrder.AddFirst(key);
            while (scaledOrder.Count > 260)
            {
                string old = scaledOrder.Last.Value;
                scaledOrder.RemoveLast();
                Scaled o;
                if (scaled.TryGetValue(old, out o)) { scaled.Remove(old); o.Bmp.Dispose(); }
            }
            return bmp;
        }

        /// <summary>Weicher Schatten/Glow als fertige Bitmap (je Größe, Farbe und Stärke einmal berechnet).</summary>
        Bitmap Shadow(int w, int h, float rad, Color c, int alpha)
        {
            int level = Math.Max(1, Math.Min(ShadowLevels, (int)Math.Round(alpha / 110f * ShadowLevels)));
            long key = ((long)w << 40) ^ ((long)h << 28) ^ ((long)(c.ToArgb() & 0xFFFFFF) << 4) ^ level;
            Bitmap b;
            if (shadows.TryGetValue(key, out b)) return b;
            if (shadows.Count > 64) { foreach (var x in shadows.Values) x.Dispose(); shadows.Clear(); }
            int size = S(6);
            b = new Bitmap(w + 2 * size + 2, h + 2 * size + 2, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(b))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Gfx.Glow(g, new RectangleF(size + 1, size + 1, w, h), rad, c, size, (int)(level * 110f / ShadowLevels));
            }
            shadows[key] = b;
            return b;
        }

        void ClearGfxCache()
        {
            foreach (var x in scaled.Values) x.Bmp.Dispose();
            scaled.Clear(); scaledOrder.Clear();
            foreach (var x in shadows.Values) x.Dispose();
            shadows.Clear();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) ClearGfxCache();
            base.Dispose(disposing);
        }

        // ---------------- Layout ----------------

        int TileW { get { return S(new[] { 200, 260, 340 }[Math.Max(0, Math.Min(2, TileSize))]); } }
        int ThumbH { get { return TileW * 9 / 16; } }
        int TileH { get { return ThumbH + S(78); } }
        int Gap { get { return S(16); } }
        int Pad { get { return S(16); } }
        int HeaderH { get { return ListMode ? S(34) : 0; } }
        int RowH { get { return S(62); } }
        int ViewW { get { return Width - (vbar.Visible ? vbar.Width : 0); } }
        int Scroll { get { return (int)Math.Round(scrollPos); } }

        int Cols
        {
            get
            {
                if (ListMode) return 1;
                int avail = ViewW - 2 * Pad;
                return Math.Max(1, (avail + Gap) / (TileW + Gap));
            }
        }

        int ContentHeight
        {
            get
            {
                if (ListMode) return Items.Count * RowH + S(8);
                int rows = (Items.Count + Cols - 1) / Cols;
                return 2 * Pad + rows * TileH + Math.Max(0, rows - 1) * Gap;
            }
        }

        Rectangle ItemRect(int i)
        {
            int y0 = HeaderH - Scroll;
            if (ListMode) return new Rectangle(S(8), y0 + S(4) + i * RowH, ViewW - S(16), RowH - S(4));
            int c = i % Cols, r = i / Cols;
            int used = Cols * TileW + (Cols - 1) * Gap;
            int x0 = Pad + Math.Max(0, (ViewW - 2 * Pad - used) / 2);
            return new Rectangle(x0 + c * (TileW + Gap), y0 + Pad + r * (TileH + Gap), TileW, TileH);
        }

        int ItemAt(Point p)
        {
            if (p.Y < HeaderH) return -1;
            for (int i = FirstVisible(); i < Items.Count; i++)
            {
                var r = ItemRect(i);
                if (r.Top > Height) break;
                if (r.Contains(p)) return i;
            }
            return -1;
        }

        int FirstVisible()
        {
            if (ListMode) return Math.Max(0, Scroll / RowH - 1);
            int row = Math.Max(0, (Scroll - Pad) / (TileH + Gap) - 1);
            return row * Cols;
        }

        Rectangle StarRect(Rectangle r)
        {
            if (ListMode) return new Rectangle(r.X + S(112), r.Y + (r.Height - S(20)) / 2, S(20), S(20));
            return new Rectangle(r.X + S(8), r.Y + S(8), S(26), S(26));
        }

        void UpdateScroll()
        {
            int view = Math.Max(1, Height - HeaderH);
            int content = ContentHeight;
            vbar.Visible = content > view;
            vbar.Minimum = 0;
            vbar.Maximum = Math.Max(0, content);
            vbar.LargeChange = view;
            vbar.SmallChange = ListMode ? RowH : S(60);
            int maxVal = Math.Max(0, content - view);
            if (scrollTarget > maxVal) scrollTarget = maxVal;
            if (scrollPos > maxVal) scrollPos = maxVal;
            vbar.Value = (int)scrollTarget;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            UpdateScroll();
        }

        void ScrollTo(float target)
        {
            int view = Math.Max(1, Height - HeaderH);
            scrollTarget = Math.Max(0, Math.Min(Math.Max(0, ContentHeight - view), target));
            vbar.Value = (int)scrollTarget;
            Animate();
            if (!Anim.Enabled) Invalidate();
        }

        void EnsureVisible(int i)
        {
            if (i < 0) return;
            var r = ItemRect(i);
            int y = r.Top + Scroll - HeaderH;
            int view = Height - HeaderH;
            float v = scrollTarget;
            if (y < v) v = y - (ListMode ? 0 : Pad);
            else if (y + r.Height > v + view) v = y + r.Height - view + (ListMode ? S(4) : Pad);
            ScrollTo(v);
        }

        // ---------------- Zeichnen ----------------

        public static string Dur(double d)
        {
            if (d <= 0) return "";
            int s = (int)Math.Round(d);
            return s >= 3600 ? string.Format("{0}:{1:00}:{2:00}", s / 3600, (s / 60) % 60, s % 60) : string.Format("{0}:{1:00}", s / 60, s % 60);
        }

        public static string FmtSize(long b)
        {
            if (b >= 1024L * 1024 * 1024) return (b / 1073741824.0).ToString("0.0", CultureInfo.CurrentCulture) + " GB";
            return (b / 1048576.0).ToString("0", CultureInfo.CurrentCulture) + " MB";
        }

        static void StarPath(GraphicsPath p, RectangleF r)
        {
            var pts = new PointF[10];
            float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2 + r.Height * 0.04f, R = r.Width / 2, rr = R * 0.45f;
            for (int i = 0; i < 10; i++)
            {
                double a = -Math.PI / 2 + i * Math.PI / 5;
                float rad = i % 2 == 0 ? R : rr;
                pts[i] = new PointF(cx + (float)(Math.Cos(a) * rad), cy + (float)(Math.Sin(a) * rad));
            }
            p.AddPolygon(pts);
        }

        public static void DrawStar(Graphics g, RectangleF r, bool filled, Color c)
        {
            using (var p = new GraphicsPath())
            {
                StarPath(p, r);
                if (filled) using (var b = new SolidBrush(c)) g.FillPath(b, p);
                else using (var pen = new Pen(c, 1.5f)) g.DrawPath(pen, p);
            }
        }

        static void Badge(Graphics g, string text, Font f, Rectangle anchor, bool right, bool bottom, Color back, Color fore, int pad)
        {
            var sz = Gfx.Measure(text, f);
            int w = sz.Width + S(10), h = sz.Height + S(4);
            int x = right ? anchor.Right - w - pad : anchor.X + pad;
            int y = bottom ? anchor.Bottom - h - pad : anchor.Y + pad;
            Gfx.FillRound(g, back, new RectangleF(x, y, w, h), S(5));
            TextRenderer.DrawText(g, text, f, new Rectangle(x, y, w, h), fore, Gfx.Center);
        }

        static int DrawTags(Graphics g, List<string> tags, Font f, int x, int y, int maxX)
        {
            foreach (var t in tags)
            {
                var sz = Gfx.Measure(t, f);
                int w = sz.Width + S(14), h = sz.Height + S(4);
                if (x + w > maxX) { TextRenderer.DrawText(g, "…", f, new Point(x, y), Theme.TextDim); break; }
                var c = Theme.TagColor(t);
                var r = new RectangleF(x, y, w, h);
                Gfx.FillRound(g, Gfx.Lerp(Theme.Panel2, c, 0.22f), r, h / 2f);
                TextRenderer.DrawText(g, t, f, Rectangle.Round(r), Gfx.Lerp(Theme.Text, c, 0.35f), Gfx.Center);
                x += w + S(5);
            }
            return x;
        }

        float EnterAnim(int visibleIndex)
        {
            if (!Anim.Enabled) return 1;
            double t = (Anim.Now - enterStart) * Anim.Factor - Math.Min(visibleIndex, 24) * 0.028;
            return Anim.EaseOut((float)Math.Max(0, Math.Min(1, t / 0.32)));
        }

        float ThumbAlpha(string path)
        {
            double t;
            if (!Anim.Enabled || !thumbShown.TryGetValue(path, out t)) return 1;
            return (float)Math.Min(1, (Anim.Now - t) * Anim.Factor / 0.3);
        }

        float Hover(ClipItem it)
        {
            float v;
            return hoverVal.TryGetValue(it, out v) ? v : 0;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var clip = e.ClipRectangle;
            using (var bb = new SolidBrush(BackColor)) g.FillRectangle(bb, clip);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.Bilinear;
            if (Items.Count == 0)
            {
                DrawEmpty(g);
                return;
            }
            var bold = Theme.Semi(9.5f);
            var small = Theme.F(8.5f);
            int first = FirstVisible();
            for (int i = first; i < Items.Count; i++)
            {
                var r = ItemRect(i);
                if (r.Top > Height) break;
                if (r.Bottom < HeaderH) continue;
                float enter = EnterAnim(i - first);
                if (enter <= 0.001f) continue;
                int dy = (int)((1 - enter) * S(18));
                r.Offset(0, dy);
                // Ohne Hover/Auswahl hat die Kachel weder Schatten noch Anheben -> nur ihr eigenes Rechteck zählt.
                var it0 = Items[i];
                var pb = (Hover(it0) > 0.01f || sel.Contains(it0)) ? PaintBounds(r) : Rectangle.Inflate(r, 1, 1);
                if (enter < 1) pb.Inflate(S(6), S(6));
                if (!pb.IntersectsWith(clip)) continue;
                if (ListMode) DrawRow(g, Items[i], r, i, bold, small);
                else DrawTile(g, Items[i], r, bold, small);
                if (enter < 1)
                    using (var b = new SolidBrush(Gfx.Alpha(BackColor, (int)(255 * (1 - enter))))) g.FillRectangle(b, r.X - S(6), r.Y - S(6), r.Width + S(12), r.Height + S(12));
            }
            if (ListMode && clip.Top < HeaderH) DrawHeader(g);
        }

        void DrawEmpty(Graphics g)
        {
            int cx = ViewW / 2, cy = (Height - HeaderH) / 2 + HeaderH - S(30);
            float pulse = 0.5f + 0.5f * (float)Math.Sin(Anim.Now * 2);
            var ring = new RectangleF(cx - S(34), cy - S(34), S(68), S(68));
            using (var p = new Pen(Gfx.Alpha(Theme.Accent, 120), 3f * Theme.Dpi)) g.DrawArc(p, ring, -60, 300);
            using (var b = new SolidBrush(Gfx.Alpha(Theme.Rec, (int)(120 + 80 * pulse)))) g.FillEllipse(b, cx - S(12), cy - S(12), S(24), S(24));
            TextRenderer.DrawText(g, EmptyText, Theme.F(10f), new Rectangle(S(20), cy + S(50), ViewW - S(40), S(80)), Theme.TextDim,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        }

        void DrawTile(Graphics g, ClipItem it, Rectangle r, Font bold, Font small)
        {
            bool s = sel.Contains(it);
            float h = Hover(it);
            r.Offset(0, -(int)(h * S(3)));
            var rf = new RectangleF(r.X, r.Y, r.Width, r.Height);
            float rad = S(10);
            if (h > 0.01f || s)
            {
                var sh = Shadow(r.Width, r.Height, rad, s ? Theme.Accent : Color.Black, (int)(s ? 70 + 40 * h : 90 * h));
                g.DrawImageUnscaled(sh, r.X - S(6) - 1, r.Y - S(6) - 1);
            }
            Gfx.FillRound(g, s ? Gfx.Lerp(Theme.Panel, Theme.Accent, 0.12f) : Gfx.Lerp(Theme.Panel, Theme.Hover, h * 0.6f), rf, rad);

            // Vorschaubild (oben runde Ecken) – fertig skaliert aus dem Cache, nur noch 1:1 kopieren
            var tr = new Rectangle(r.X, r.Y, r.Width, ThumbH);
            var src = Thumbs != null ? Thumbs.Get(ThumbOf(it)) : null;
            var bmp = ScaledThumb(ThumbOf(it), src, tr.Width, tr.Height, rad, false);
            if (bmp != null)
            {
                float a = ThumbAlpha(it.Path);
                if (a < 1)
                    using (var p = TopRound(tr, rad, false))
                    using (var b = new SolidBrush(Color.FromArgb(12, 13, 16))) g.FillPath(b, p);
                if (a >= 1) g.DrawImageUnscaled(bmp, tr.X, tr.Y);
                else
                {
                    using (var ia = new System.Drawing.Imaging.ImageAttributes())
                    {
                        ia.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix { Matrix33 = a });
                        g.DrawImage(bmp, tr, 0, 0, tr.Width, tr.Height, GraphicsUnit.Pixel, ia);
                    }
                }
            }
            else
            {
                // Lade-Schimmer
                float ph = (float)((Anim.Now * 0.8) % 1.0);
                using (var p = TopRound(tr, rad, false))
                using (var b = new LinearGradientBrush(new RectangleF(tr.X - 1 + (ph - 0.5f) * tr.Width, tr.Y, tr.Width + 2, tr.Height), Color.FromArgb(12, 13, 16), Color.FromArgb(28, 30, 36), 0f))
                {
                    b.WrapMode = WrapMode.TileFlipX;
                    g.FillPath(b, p);
                }
            }
            int rp;
            bool rendering = renderPct.TryGetValue(it.Path, out rp);
            if (rendering) { DrawRendering(g, tr, rp, rad, false); h = 0; }
            if (h > 0.01f)
            {
                using (var b = new LinearGradientBrush(new RectangleF(tr.X, tr.Y + tr.Height * 0.35f, tr.Width, tr.Height * 0.65f + 1), Color.FromArgb(0, 0, 0, 0), Color.FromArgb((int)(150 * h), 0, 0, 0), 90f))
                    g.FillRectangle(b, tr.X, tr.Y + tr.Height * 0.35f + 1, tr.Width, tr.Height * 0.65f);
                float pr = S(22) * (0.8f + 0.2f * h);
                var pc = new RectangleF(tr.X + tr.Width / 2f - pr, tr.Y + tr.Height / 2f - pr, pr * 2, pr * 2);
                using (var b = new SolidBrush(Gfx.Alpha(Theme.Accent, (int)(230 * h)))) g.FillEllipse(b, pc);
                using (var b = new SolidBrush(Gfx.Alpha(Theme.OnAccent, (int)(255 * h))))
                {
                    float k = pr * 0.42f;
                    g.FillPolygon(b, new[] { new PointF(pc.X + pr - k * 0.7f, pc.Y + pr - k), new PointF(pc.X + pr - k * 0.7f, pc.Y + pr + k), new PointF(pc.X + pr + k * 1.05f, pc.Y + pr) });
                }
            }

            string d = Dur(it.Meta.Duration);
            if (d.Length > 0) Badge(g, d, Theme.Medium(8f), tr, true, true, Color.FromArgb(200, 10, 10, 12), Color.White, S(7));
            if (it.IsCut) Badge(g, "SCHNITT", Theme.Semi(7.5f), tr, true, false, Theme.Cut, Gfx.Lum(Theme.Cut) > 0.6f ? Color.Black : Color.White, S(7));
            if (it.Meta.Favorite || h > 0.01f)
            {
                var sr = StarRect(r);
                if (it.Meta.Favorite)
                {
                    using (var b = new SolidBrush(Color.FromArgb(120, 0, 0, 0))) g.FillEllipse(b, sr.X - S(3), sr.Y - S(3), sr.Width + S(6), sr.Height + S(6));
                    DrawStar(g, sr, true, Theme.Star);
                }
                else DrawStar(g, sr, false, Color.FromArgb((int)(220 * h), 255, 255, 255));
            }

            int x = r.X + S(12), w = r.Width - S(24);
            int y = tr.Bottom + S(9);
            TextRenderer.DrawText(g, it.Name, bold, new Rectangle(x, y, w, S(20)), Theme.Text, Gfx.Left);
            y += S(22);
            var gc = Theme.HashColor(it.Game);
            using (var b = new SolidBrush(gc)) g.FillEllipse(b, x, y + S(5), S(7), S(7));
            string info = it.Game + "  ·  " + it.Date.ToString("dd.MM.yy HH:mm", CultureInfo.CurrentCulture) + "  ·  " + FmtSize(it.Size);
            TextRenderer.DrawText(g, info, small, new Rectangle(x + S(13), y, w - S(13), S(18)), Theme.TextDim, Gfx.Left);
            y += S(22);
            DrawTags(g, it.Meta.Tags, Theme.Medium(7.5f), x, y, r.Right - S(10));
            if (s) Gfx.DrawRound(g, Theme.Accent, new RectangleF(r.X + 1, r.Y + 1, r.Width - 2, r.Height - 2), rad - 1, 2f * Theme.Dpi);
        }

        static readonly string[] ColNames = { "Name", "Spiel/App", "Datum", "Dauer", "Größe", "Tags" };

        int[] ColX()
        {
            int w = ViewW - S(8);
            int thumb = S(112);
            int game = Math.Min(S(160), w / 7), date = S(122), dur = S(60), size = S(72), tags = Math.Max(S(120), w / 6);
            int name = Math.Max(S(160), w - thumb - game - date - dur - size - tags - S(10));
            var xs = new int[7];
            xs[0] = thumb; xs[1] = xs[0] + name; xs[2] = xs[1] + game; xs[3] = xs[2] + date; xs[4] = xs[3] + dur; xs[5] = xs[4] + size; xs[6] = xs[5] + tags;
            return xs;
        }

        void DrawHeader(Graphics g)
        {
            var hr = new Rectangle(0, 0, ViewW, HeaderH);
            using (var b = new SolidBrush(BackColor)) g.FillRectangle(b, hr);
            var xs = ColX();
            var f = Theme.Semi(8f);
            for (int c = 0; c < 6; c++)
            {
                string t = ColNames[c].ToUpperInvariant();
                var rr = new Rectangle(xs[c] + S(8), 0, xs[c + 1] - xs[c] - S(8), HeaderH);
                TextRenderer.DrawText(g, t, f, rr, SortColumn == c ? Theme.Accent : Theme.TextDim, Gfx.Left);
                if (SortColumn == c)
                {
                    var ts = Gfx.Measure(t, f);
                    Gfx.Chevron(g, new PointF(rr.X + ts.Width + S(10), HeaderH / 2f + S(1)), S(3), Theme.Accent, 0, 1.5f * Theme.Dpi);
                }
            }
            using (var p = new Pen(Theme.Border)) g.DrawLine(p, S(8), HeaderH - 1, ViewW - S(8), HeaderH - 1);
        }

        void DrawRow(Graphics g, ClipItem it, Rectangle r, int i, Font bold, Font small)
        {
            bool s = sel.Contains(it);
            float h = Hover(it);
            var rf = new RectangleF(r.X, r.Y, r.Width, r.Height);
            Color bg = s ? Gfx.Lerp(Theme.Bg, Theme.Accent, 0.16f) : Gfx.Lerp(i % 2 == 0 ? Theme.Bg : Color.FromArgb(21, 22, 28), Theme.Hover, h * 0.7f);
            Gfx.FillRound(g, bg, rf, S(8));
            var xs = ColX();
            var tr = new Rectangle(r.X + S(8), r.Y + S(6), S(88), r.Height - S(12));
            var src = Thumbs != null ? Thumbs.Get(ThumbOf(it)) : null;
            var bmp = ScaledThumb(ThumbOf(it), src, tr.Width, tr.Height, S(5), true);
            float ta = bmp != null ? ThumbAlpha(it.Path) : 0;
            if (ta < 1)
                using (var p = TopRound(tr, S(5), true))
                using (var b = new SolidBrush(Color.FromArgb(12, 13, 16))) g.FillPath(b, p);
            if (bmp != null)
            {
                if (ta >= 1) g.DrawImageUnscaled(bmp, tr.X, tr.Y);
                else
                    using (var ia = new System.Drawing.Imaging.ImageAttributes())
                    {
                        ia.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix { Matrix33 = ta });
                        g.DrawImage(bmp, tr, 0, 0, tr.Width, tr.Height, GraphicsUnit.Pixel, ia);
                    }
            }
            int rp;
            if (renderPct.TryGetValue(it.Path, out rp)) DrawRendering(g, tr, rp, S(5), true);
            var flags = Gfx.Left;
            int nx = xs[0] + S(8);
            if (it.Meta.Favorite || h > 0.01f)
                DrawStar(g, new RectangleF(nx, r.Y + r.Height / 2 - S(9), S(18), S(18)), it.Meta.Favorite, it.Meta.Favorite ? Theme.Star : Gfx.Alpha(Theme.TextDim, (int)(255 * h)));
            nx += S(26);
            if (it.IsCut)
            {
                var bf = Theme.Semi(7f);
                var bsz = Gfx.Measure("SCHNITT", bf);
                var br = new RectangleF(nx, r.Y + (r.Height - bsz.Height - S(4)) / 2f, bsz.Width + S(10), bsz.Height + S(4));
                Gfx.FillRound(g, Theme.Cut, br, S(4));
                TextRenderer.DrawText(g, "SCHNITT", bf, Rectangle.Round(br), Gfx.Lum(Theme.Cut) > 0.6f ? Color.Black : Color.White, Gfx.Center);
                nx += (int)br.Width + S(8);
            }
            TextRenderer.DrawText(g, it.Name, bold, new Rectangle(nx, r.Y, xs[1] - nx - S(4), r.Height), Theme.Text, flags);
            var gc = Theme.HashColor(it.Game);
            using (var b = new SolidBrush(gc)) g.FillEllipse(b, xs[1] + S(8), r.Y + r.Height / 2 - S(3), S(7), S(7));
            TextRenderer.DrawText(g, it.Game, Font, new Rectangle(xs[1] + S(20), r.Y, xs[2] - xs[1] - S(22), r.Height), Theme.TextDim, flags);
            TextRenderer.DrawText(g, it.Date.ToString("dd.MM.yyyy HH:mm", CultureInfo.CurrentCulture), Font, new Rectangle(xs[2] + S(8), r.Y, xs[3] - xs[2] - S(10), r.Height), Theme.TextDim, flags);
            TextRenderer.DrawText(g, Dur(it.Meta.Duration), Font, new Rectangle(xs[3] + S(8), r.Y, xs[4] - xs[3] - S(10), r.Height), Theme.TextDim, flags);
            TextRenderer.DrawText(g, FmtSize(it.Size), Font, new Rectangle(xs[4] + S(8), r.Y, xs[5] - xs[4] - S(10), r.Height), Theme.TextDim, flags);
            DrawTags(g, it.Meta.Tags, Theme.Medium(7.5f), xs[5] + S(8), r.Y + (r.Height - S(18)) / 2, xs[6]);
            if (s) using (var b = new SolidBrush(Theme.Accent)) g.FillRectangle(b, r.X, r.Y + S(10), S(3), r.Height - S(20));
        }

        // ---------------- Maus / Tastatur ----------------

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            if (ListMode && e.Y < HeaderH)
            {
                var xs = ColX();
                for (int c = 0; c < 6; c++)
                    if (e.X >= xs[c] && e.X < xs[c + 1]) { var hh = HeaderClicked; if (hh != null) hh(c); }
                return;
            }
            int i = ItemAt(e.Location);
            downPt = e.Location;
            downOnItem = i >= 0;
            if (i < 0)
            {
                if (e.Button == MouseButtons.Left) { sel.Clear(); Invalidate(); Fire(); }
                if (e.Button == MouseButtons.Right) { var mh = MenuRequested; if (mh != null && sel.Count > 0) mh(e.Location); }
                return;
            }
            var it = Items[i];
            if (it.Placeholder || renderPct.ContainsKey(it.Path)) return; // rendert gerade – noch nicht anfassbar
            if (e.Button == MouseButtons.Left && StarRect(ItemRect(i)).Contains(e.Location))
            {
                var fh = FavoriteClicked;
                if (fh != null) fh(it);
                Invalidate();
                return;
            }
            if (e.Button == MouseButtons.Right)
            {
                if (!sel.Contains(it)) { sel.Clear(); sel.Add(it); focus = anchor = it; Fire(); }
                Invalidate();
                var mh = MenuRequested;
                if (mh != null) mh(e.Location);
                return;
            }
            if (e.Button != MouseButtons.Left) return;
            bool ctrl = (ModifierKeys & Keys.Control) != 0, shift = (ModifierKeys & Keys.Shift) != 0;
            if (shift && anchor != null) SelectRange(Items.IndexOf(anchor), i, ctrl);
            else if (ctrl) { if (!sel.Remove(it)) sel.Add(it); anchor = it; }
            else if (!sel.Contains(it)) { sel.Clear(); sel.Add(it); anchor = it; }
            focus = it;
            if (e.Clicks == 2) { var ah = Activated; if (ah != null) ah(it); }
            Invalidate();
            Fire();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && downOnItem && (ModifierKeys & (Keys.Control | Keys.Shift)) == 0 && sel.Count > 1)
            {
                int i = ItemAt(e.Location);
                if (i >= 0 && Math.Abs(e.X - downPt.X) < 4 && Math.Abs(e.Y - downPt.Y) < 4)
                {
                    sel.Clear(); sel.Add(Items[i]); anchor = focus = Items[i];
                    Invalidate(); Fire();
                }
            }
            downOnItem = false;
        }

        void SelectRange(int a, int b, bool add)
        {
            if (!add) sel.Clear();
            for (int k = Math.Min(a, b); k <= Math.Max(a, b); k++) sel.Add(Items[k]);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int i = ItemAt(e.Location);
            var it = i >= 0 ? Items[i] : null;
            if (it != hoverItem)
            {
                hoverItem = it;
                if (it != null && !hoverVal.ContainsKey(it)) hoverVal[it] = 0;
                if (!Anim.Enabled) { hoverVal.Clear(); if (it != null) hoverVal[it] = 1; Invalidate(); }
                Animate();
            }
            Cursor = (i >= 0 && StarRect(ItemRect(i)).Contains(e.Location)) ? Cursors.Hand : Cursors.Default;
            if (e.Button == MouseButtons.Left && downOnItem && sel.Count > 0 &&
                (Math.Abs(e.X - downPt.X) > S(8) || Math.Abs(e.Y - downPt.Y) > S(8)))
            {
                downOnItem = false;
                var paths = new List<string>();
                foreach (var x in Selected) paths.Add(x.Path);
                var data = new DataObject(DataFormats.FileDrop, paths.ToArray());
                DoDragDrop(data, DragDropEffects.Copy);
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hoverItem = null;
            Animate();
            if (!Anim.Enabled) { hoverVal.Clear(); Invalidate(); }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (!vbar.Visible) return;
            int step = ListMode ? RowH * 3 : (TileH + Gap) / 2 + S(20);
            ScrollTo(scrollTarget - Math.Sign(e.Delta) * step * Math.Max(1, Math.Abs(e.Delta) / 120));
        }

        protected override bool IsInputKey(Keys keyData)
        {
            switch (keyData & Keys.KeyCode)
            {
                case Keys.Up: case Keys.Down: case Keys.Left: case Keys.Right: return true;
            }
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (Items.Count == 0) { base.OnKeyDown(e); return; }
            int i = focus != null ? Items.IndexOf(focus) : -1;
            int n = i;
            switch (e.KeyCode)
            {
                case Keys.Left: n = i - 1; break;
                case Keys.Right: n = i + 1; break;
                case Keys.Up: n = i - Cols; break;
                case Keys.Down: n = i + Cols; break;
                case Keys.Home: n = 0; break;
                case Keys.End: n = Items.Count - 1; break;
                case Keys.PageDown: n = i + Cols * Math.Max(1, (Height - HeaderH) / (ListMode ? RowH : TileH + Gap)); break;
                case Keys.PageUp: n = i - Cols * Math.Max(1, (Height - HeaderH) / (ListMode ? RowH : TileH + Gap)); break;
                case Keys.Enter:
                    if (focus != null && !focus.Placeholder && !renderPct.ContainsKey(focus.Path)) { var ah = Activated; if (ah != null) ah(focus); }
                    e.Handled = true; return;
                case Keys.A:
                    if (e.Control) { foreach (var it in Items) sel.Add(it); Invalidate(); Fire(); e.Handled = true; return; }
                    break;
            }
            if (n != i)
            {
                if (i < 0) n = 0;
                n = Math.Max(0, Math.Min(Items.Count - 1, n));
                var it = Items[n];
                if (e.Shift && anchor != null) SelectRange(Items.IndexOf(anchor), n, false);
                else { sel.Clear(); sel.Add(it); anchor = it; }
                focus = it;
                EnsureVisible(n);
                Invalidate();
                Fire();
                e.Handled = true;
                return;
            }
            base.OnKeyDown(e);
        }
    }
}
