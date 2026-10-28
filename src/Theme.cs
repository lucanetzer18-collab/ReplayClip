using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ReplayClip
{
    /// <summary>Farben, Schriften, Icons und Hilfen für das dunkle Design.</summary>
    public static class Theme
    {
        public static float Dpi = 1f;
        public static int S(float v) { return (int)Math.Round(v * Dpi); }

        // ---------------- Farben ----------------
        public static Color Bg = Color.FromArgb(17, 18, 23);
        public static Color Panel = Color.FromArgb(25, 27, 33);
        public static Color Panel2 = Color.FromArgb(34, 37, 45);
        public static Color Hover = Color.FromArgb(44, 48, 58);
        public static Color Border = Color.FromArgb(52, 57, 68);
        public static Color Text = Color.FromArgb(234, 236, 241);
        public static Color TextDim = Color.FromArgb(150, 157, 171);
        public static Color Danger = Color.FromArgb(240, 88, 76);
        public static Color Warning = Color.FromArgb(245, 190, 60);
        public static Color Star = Color.FromArgb(255, 196, 61);
        public static Color Rec = Color.FromArgb(255, 64, 72);

        public static Color Accent = Color.FromArgb(46, 204, 113);
        public static Color Secondary = Color.FromArgb(77, 163, 255);
        public static Color AccentDark { get { return Gfx.Lerp(Accent, Color.Black, 0.28f); } }
        public static Color Selection { get { return Gfx.Lerp(Panel, Accent, 0.22f); } }
        public static Color Cut { get { return Secondary; } }
        /// <summary>Textfarbe auf Akzent-Flächen (hell oder dunkel je nach Helligkeit).</summary>
        public static Color OnAccent { get { return Gfx.Lum(Accent) > 0.62f ? Color.FromArgb(18, 20, 24) : Color.White; } }

        public static int Animations;          // 0 voll, 1 reduziert, 2 aus

        public static readonly KeyValuePair<string, Color>[] Presets =
        {
            new KeyValuePair<string, Color>("Neon-Grün", Color.FromArgb(46, 204, 113)),
            new KeyValuePair<string, Color>("FL-Orange", Color.FromArgb(255, 152, 32)),
            new KeyValuePair<string, Color>("Himmelblau", Color.FromArgb(77, 163, 255)),
            new KeyValuePair<string, Color>("Violett", Color.FromArgb(150, 110, 255)),
            new KeyValuePair<string, Color>("Pink", Color.FromArgb(255, 77, 141)),
            new KeyValuePair<string, Color>("Rot", Color.FromArgb(255, 82, 82)),
            new KeyValuePair<string, Color>("Gold", Color.FromArgb(245, 197, 66)),
            new KeyValuePair<string, Color>("Cyan", Color.FromArgb(34, 211, 238)),
            new KeyValuePair<string, Color>("Mint", Color.FromArgb(94, 234, 212)),
        };

        /// <summary>Farbe für Spuren/Spiele (FL-Studio-artige Farbcodes).</summary>
        public static Color Palette(int i)
        {
            Color[] p =
            {
                Accent, Secondary, Color.FromArgb(255, 152, 32), Color.FromArgb(150, 110, 255),
                Color.FromArgb(255, 77, 141), Color.FromArgb(34, 211, 238), Color.FromArgb(245, 197, 66), Color.FromArgb(94, 234, 212)
            };
            return p[((i % p.Length) + p.Length) % p.Length];
        }

        public static Color HashColor(string s)
        {
            int h = 17;
            foreach (char c in (s ?? "").ToLowerInvariant()) h = h * 31 + c;
            return Palette(Math.Abs(h % 8) + 2);
        }

        static Dictionary<string, int> tagColors = new Dictionary<string, int>(StringComparer.CurrentCultureIgnoreCase);

        /// <summary>Farbe eines Tags: selbst gewählt oder automatisch aus dem Namen.</summary>
        public static Color TagColor(string tag)
        {
            int argb;
            if (tag != null && tagColors.TryGetValue(tag, out argb)) return Color.FromArgb(255, Color.FromArgb(argb));
            return HashColor(tag);
        }

        /// <summary>Farbe eines Spiels/Ordners: selbst gewählt oder automatisch.</summary>
        public static Color GameColor(string game)
        {
            int argb;
            if (game != null && tagColors.TryGetValue("game:" + game, out argb)) return Color.FromArgb(255, Color.FromArgb(argb));
            return HashColor(game);
        }

        public static void Configure(Settings s)
        {
            tagColors = new Dictionary<string, int>(s.TagColors, StringComparer.CurrentCultureIgnoreCase);
            Accent = Color.FromArgb(255, Color.FromArgb(s.AccentArgb));
            Secondary = Color.FromArgb(255, Color.FromArgb(s.SecondaryArgb));
            Animations = s.Animations;
        }

        /// <summary>Alle offenen Fenster neu zeichnen (z.B. nach Farbwechsel).</summary>
        public static void RefreshAll()
        {
            foreach (Form f in Application.OpenForms) InvalidateTree(f);
        }

        static void InvalidateTree(Control c)
        {
            c.Invalidate();
            foreach (Control k in c.Controls) InvalidateTree(k);
        }

        // ---------------- Schriften ----------------

        public static readonly string[] FontNames = { "Inter (eingebaut)", "Segoe UI Variable", "Bahnschrift", "Segoe UI" };
        static PrivateFontCollection pfc;
        static string famRegular = "Segoe UI", famMedium = "Segoe UI", famSemi = "Segoe UI Semibold";
        static FontFamily privRegular, privMedium, privSemi;
        static readonly Dictionary<string, Font> cache = new Dictionary<string, Font>();
        static string iconFamily;

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] static extern int AddFontResourceEx(string name, uint fl, IntPtr res);

        public static void InitFonts(int choice)
        {
            bool ok = false;
            if (choice == 0) ok = LoadInter();
            if (!ok && choice == 1 && Exists("Segoe UI Variable Text"))
            {
                famRegular = "Segoe UI Variable Text";
                famMedium = Exists("Segoe UI Variable Text Semibold") ? "Segoe UI Variable Text" : famRegular;
                famSemi = Exists("Segoe UI Variable Text Semibold") ? "Segoe UI Variable Text Semibold" : famRegular;
                ok = true;
            }
            if (!ok && choice == 2 && Exists("Bahnschrift"))
            {
                famRegular = famMedium = "Bahnschrift";
                famSemi = Exists("Bahnschrift SemiBold") ? "Bahnschrift SemiBold" : "Bahnschrift";
                ok = true;
            }
            if (!ok && choice != 0 && LoadInter()) ok = true;
            if (!ok)
            {
                famRegular = famMedium = Exists("Segoe UI") ? "Segoe UI" : SystemFonts.MessageBoxFont.FontFamily.Name;
                famSemi = Exists("Segoe UI Semibold") ? "Segoe UI Semibold" : famRegular;
            }
            if (Exists("Segoe Fluent Icons")) iconFamily = "Segoe Fluent Icons";
            else if (Exists("Segoe MDL2 Assets")) iconFamily = "Segoe MDL2 Assets";
            else iconFamily = null;
        }

        static bool Exists(string name)
        {
            try
            {
                using (var f = new FontFamily(name)) return string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        static bool LoadInter()
        {
            try
            {
                pfc = new PrivateFontCollection();
                string dir = Path.Combine(Settings.AppDataDir, "fonts");
                Directory.CreateDirectory(dir);
                var asm = Assembly.GetExecutingAssembly();
                foreach (var w in new[] { "Regular", "Medium", "SemiBold", "Bold" })
                {
                    string res = "Inter-" + w + ".ttf";
                    using (var st = asm.GetManifestResourceStream(res))
                    {
                        if (st == null) return false;
                        string path = Path.Combine(dir, res);
                        if (!File.Exists(path) || new FileInfo(path).Length != st.Length)
                        {
                            using (var fs = File.Create(path)) st.CopyTo(fs);
                        }
                        pfc.AddFontFile(path);
                        try { AddFontResourceEx(path, 0x10 /*FR_PRIVATE*/, IntPtr.Zero); } catch { }
                    }
                }
                foreach (var f in pfc.Families)
                {
                    if (f.Name == "Inter") privRegular = f;
                    else if (f.Name == "Inter Medium") privMedium = f;
                    else if (f.Name == "Inter SemiBold") privSemi = f;
                }
                if (privRegular == null) return false;
                if (privMedium == null) privMedium = privRegular;
                try { using (var f = new Font(privRegular, 9f)) { int h = f.Height; } }
                catch
                {
                    // manche Umgebungen können private Schriften nicht vermessen -> installierte Schrift gleichen Namens nutzen
                    privRegular = privMedium = privSemi = null;
                    if (!Exists("Inter")) return false;
                    famRegular = "Inter";
                    famMedium = Exists("Inter Medium") ? "Inter Medium" : "Inter";
                    famSemi = Exists("Inter SemiBold") ? "Inter SemiBold" : "Inter";
                }
                return true;
            }
            catch { return false; }
        }

        static Font Make(string key, Func<Font> f)
        {
            Font r;
            if (!cache.TryGetValue(key, out r)) { r = f(); cache[key] = r; }
            return r;
        }

        /// <summary>Normale Schrift (in Punkt).</summary>
        public static Font F(float size, FontStyle style = FontStyle.Regular)
        {
            return Make("r" + size + style, () => privRegular != null ? new Font(privRegular, size, style) : new Font(famRegular, size, style));
        }

        public static Font Medium(float size)
        {
            return Make("m" + size, () => privMedium != null ? new Font(privMedium, size) : new Font(famMedium, size));
        }

        public static Font Semi(float size)
        {
            return Make("s" + size, () =>
            {
                if (privSemi != null) return new Font(privSemi, size);
                if (privRegular != null) return new Font(privRegular, size, FontStyle.Bold);
                return famSemi == famRegular ? new Font(famRegular, size, FontStyle.Bold) : new Font(famSemi, size);
            });
        }

        public static Font Mono(float size)
        {
            return Make("mono" + size, () =>
            {
                foreach (var n in new[] { "Cascadia Mono", "Consolas", "DejaVu Sans Mono" })
                    if (Exists(n)) return new Font(n, size);
                return new Font(FontFamily.GenericMonospace, size);
            });
        }

        // ---------------- Icons (Segoe Fluent Icons / MDL2, sonst Unicode) ----------------

        static readonly Dictionary<string, string[]> icons = new Dictionary<string, string[]>
        {
            { "camera", new[] { "\uE722", "◎" } }, { "crop", new[] { "\uE7A8", "⌗" } }, { "rotate", new[] { "\uE7AD", "↻" } },
            { "record", new[] { "", "●" } }, { "settings", new[] { "", "⚙" } }, { "save", new[] { "", "⤓" } },
            { "play", new[] { "", "▶" } }, { "pause", new[] { "", "❚❚" } }, { "stop", new[] { "", "■" } },
            { "cut", new[] { "", "✂" } }, { "star", new[] { "", "☆" } }, { "starfill", new[] { "", "★" } },
            { "tag", new[] { "", "#" } }, { "folder", new[] { "", "▤" } }, { "search", new[] { "", "⌕" } },
            { "refresh", new[] { "", "⟳" } }, { "delete", new[] { "", "✕" } }, { "copy", new[] { "", "⧉" } },
            { "rename", new[] { "", "✎" } }, { "grid", new[] { "", "▦" } }, { "list", new[] { "", "☰" } },
            { "keyboard", new[] { "", "⌨" } }, { "volume", new[] { "", "♪" } }, { "mic", new[] { "", "◉" } },
            { "video", new[] { "", "▣" } }, { "library", new[] { "", "▥" } }, { "color", new[] { "", "◐" } },
            { "info", new[] { "", "ⓘ" } }, { "storage", new[] { "", "⛁" } }, { "bell", new[] { "", "♫" } },
            { "check", new[] { "", "✓" } }, { "warning", new[] { "", "!" } }, { "error", new[] { "", "✕" } },
            { "prev", new[] { "", "⏮" } }, { "next", new[] { "", "⏭" } }, { "back", new[] { "", "◀" } },
            { "forward", new[] { "", "▶" } }, { "add", new[] { "", "+" } }, { "remove", new[] { "", "−" } },
            { "edit", new[] { "", "✎" } }, { "game", new[] { "", "◆" } }, { "clock", new[] { "", "◷" } },
            { "details", new[] { "", "▧" } }, { "home", new[] { "", "⌂" } }, { "loop", new[] { "", "↻" } },
            { "mute", new[] { "", "M" } }, { "general", new[] { "", "≡" } }, { "zoom", new[] { "", "⊕" } },
            { "open", new[] { "", "↗" } }, { "close", new[] { "", "✕" } }, { "up", new[] { "", "▲" } }, { "down", new[] { "", "▼" } },
            { "tracks", new[] { "", "♪" } }, { "export", new[] { "", "⤴" } }, { "sparkle", new[] { "", "✦" } },
        };

        public static bool HasIcons { get { return iconFamily != null; } }

        public static string Icon(string name)
        {
            string[] v;
            if (name == null || !icons.TryGetValue(name, out v)) return name ?? "";
            return iconFamily != null ? v[0] : v[1];
        }

        public static Font IconFont(float size)
        {
            return Make("i" + size, () => iconFamily != null ? new Font(iconFamily, size) : F(size));
        }

        // ---------------- Fenster / Controls ----------------

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr hwnd, string app, string idList);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, string l);

        /// <summary>Dunkle Titelleiste + runde Ecken (Windows 11).</summary>
        public static void DarkTitleBar(Form f)
        {
            Action a = () =>
            {
                try
                {
                    int on = 1;
                    if (DwmSetWindowAttribute(f.Handle, 20, ref on, 4) != 0) DwmSetWindowAttribute(f.Handle, 19, ref on, 4);
                    int round = 2;
                    DwmSetWindowAttribute(f.Handle, 33, ref round, 4);
                    int cap = ColorTranslator.ToWin32(Panel);
                    DwmSetWindowAttribute(f.Handle, 35, ref cap, 4);   // Titelleistenfarbe (Win11)
                    int txt = ColorTranslator.ToWin32(Text);
                    DwmSetWindowAttribute(f.Handle, 36, ref txt, 4);
                }
                catch { }
            };
            if (f.IsHandleCreated) a(); else f.HandleCreated += (s, e) => a();
        }

        public static void RoundCorners(Form f)
        {
            Action a = () => { try { int round = 2; DwmSetWindowAttribute(f.Handle, 33, ref round, 4); } catch { } };
            if (f.IsHandleCreated) a(); else f.HandleCreated += (s, e) => a();
        }

        public static void DarkScroll(Control c)
        {
            Action a = () => { try { SetWindowTheme(c.Handle, "DarkMode_Explorer", null); } catch { } };
            if (c.IsHandleCreated) a(); else c.HandleCreated += (s, e) => a();
        }

        public static void Cue(TextBox t, string text)
        {
            Action a = () => { try { SendMessage(t.Handle, 0x1501 /*EM_SETCUEBANNER*/, new IntPtr(1), text); } catch { } };
            if (t.IsHandleCreated) a(); else t.HandleCreated += (s, e) => a();
        }

        /// <summary>Färbt Standard-Controls (Labels, Textfelder, Listen, Container) dunkel ein. Eigene Fx-Controls zeichnen sich selbst.</summary>
        public static void Apply(Control root)
        {
            ApplyOne(root);
            foreach (Control c in root.Controls) Apply(c);
            root.ControlAdded += (s, e) => Apply(e.Control);
        }

        static void ApplyOne(Control c)
        {
            if ("keep".Equals(c.Tag) || c is IFxControl) return;
            var fg = c.ForeColor;
            bool dim = fg == Color.DimGray || fg == SystemColors.GrayText;
            bool custom = !(fg == SystemColors.ControlText || fg == SystemColors.WindowText || fg == Color.Black || dim);

            if (c is TextBox)
            {
                c.BackColor = Panel2;
                c.ForeColor = Text;
                var t = (TextBox)c;
                if (t.BorderStyle == BorderStyle.Fixed3D) t.BorderStyle = BorderStyle.FixedSingle;
                if (t.Multiline) DarkScroll(c);
                return;
            }
            if (c is ListBox)
            {
                c.BackColor = Panel2;
                c.ForeColor = Text;
                ((ListBox)c).BorderStyle = BorderStyle.None;
                DarkScroll(c);
                return;
            }
            if (c is LinkLabel)
            {
                var l = (LinkLabel)c;
                l.LinkColor = Secondary; l.ActiveLinkColor = Color.White; l.VisitedLinkColor = Secondary;
                return;
            }
            if (c is Label)
            {
                if (!custom) c.ForeColor = dim ? TextDim : Text;
                if (c.Font == null || c.Font.Name == Control.DefaultFont.Name) c.Font = F(c.Font != null ? c.Font.SizeInPoints : 9f);
                return;
            }
            if (c is ScrollableControl && ((ScrollableControl)c).AutoScroll) DarkScroll(c);
            if (c is Panel || c is TableLayoutPanel || c is FlowLayoutPanel || c is Form || c is SplitContainer || c is UserControl)
            {
                if (c.BackColor == SystemColors.Control || c.BackColor == Color.FromArgb(240, 240, 240))
                    c.BackColor = c is Form ? Bg : (c.Parent != null ? c.Parent.BackColor : Bg);
                if (!custom) c.ForeColor = Text;
            }
        }

        /// <summary>Dunkles Menü mit runden Hover-Flächen und Akzent-Häkchen.</summary>
        public static void Menu(ToolStrip m)
        {
            m.Renderer = new DarkRenderer();
            m.BackColor = Panel;
            m.ForeColor = Text;
            m.Font = F(9.5f);
            m.Padding = new Padding(S(4));
            foreach (ToolStripItem it in m.Items) Style(it);
            m.ItemAdded += (s, e) => Style(e.Item);
        }

        static void Style(ToolStripItem it)
        {
            if (it.ForeColor == SystemColors.ControlText || it.ForeColor == Color.Black) it.ForeColor = Text;
            it.Padding = new Padding(S(4), S(3), S(4), S(3));
            var mi = it as ToolStripMenuItem;
            if (mi != null && mi.DropDown != null && !(mi.DropDown.Renderer is DarkRenderer)) Menu(mi.DropDown);
        }

        public static Color EffectiveBack(Control c)
        {
            while (c != null)
            {
                if (c.BackColor.A == 255) return c.BackColor;
                c = c.Parent;
            }
            return Bg;
        }
    }

    sealed class DarkRenderer : ToolStripProfessionalRenderer
    {
        public DarkRenderer() : base(new DarkColors()) { RoundedEdges = false; }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var r = new RectangleF(Theme.S(2), 0, e.Item.Width - Theme.S(4), e.Item.Height);
            Gfx.FillRound(g, Gfx.Lerp(Theme.Panel, Theme.Accent, 0.2f), r, Theme.S(5));
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? (e.Item.ForeColor == Color.Empty ? Theme.Text : e.Item.ForeColor) : Theme.TextDim;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var r = e.ImageRectangle;
            using (var p = new Pen(Theme.Accent, Math.Max(1.6f, 2f * Theme.Dpi)))
            {
                p.StartCap = p.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f, k = r.Width * 0.28f;
                g.DrawLines(p, new[] { new PointF(cx - k, cy), new PointF(cx - k * 0.25f, cy + k * 0.75f), new PointF(cx + k, cy - k * 0.7f) });
            }
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Theme.TextDim;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using (var p = new Pen(Theme.Border)) e.Graphics.DrawLine(p, Theme.S(8), y, e.Item.Width - Theme.S(8), y);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using (var p = new Pen(Theme.Border)) e.Graphics.DrawRectangle(p, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        }

        sealed class DarkColors : ProfessionalColorTable
        {
            public override Color MenuItemSelected { get { return Theme.Hover; } }
            public override Color MenuItemBorder { get { return Color.Transparent; } }
            public override Color MenuBorder { get { return Theme.Border; } }
            public override Color ToolStripDropDownBackground { get { return Theme.Panel; } }
            public override Color ImageMarginGradientBegin { get { return Theme.Panel; } }
            public override Color ImageMarginGradientMiddle { get { return Theme.Panel; } }
            public override Color ImageMarginGradientEnd { get { return Theme.Panel; } }
            public override Color SeparatorDark { get { return Theme.Border; } }
            public override Color SeparatorLight { get { return Theme.Panel; } }
            public override Color CheckBackground { get { return Color.Transparent; } }
            public override Color CheckSelectedBackground { get { return Color.Transparent; } }
            public override Color CheckPressedBackground { get { return Color.Transparent; } }
            public override Color MenuItemSelectedGradientBegin { get { return Theme.Hover; } }
            public override Color MenuItemSelectedGradientEnd { get { return Theme.Hover; } }
            public override Color MenuItemPressedGradientBegin { get { return Theme.Border; } }
            public override Color MenuItemPressedGradientEnd { get { return Theme.Border; } }
            public override Color StatusStripGradientBegin { get { return Theme.Panel; } }
            public override Color StatusStripGradientEnd { get { return Theme.Panel; } }
        }
    }
}
