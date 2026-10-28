using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

namespace ReplayClip
{
    /// <summary>Textfeld, das eine Tastenkombination aufnimmt (Entf/Rücktaste = keine).</summary>
    public sealed class HotkeyBox : TextBox
    {
        public uint Mods;
        public int Key;
        public event EventHandler HotkeyChanged;

        public HotkeyBox()
        {
            ReadOnly = true;
            ShortcutsEnabled = false;
            Cursor = Cursors.Hand;
            TextAlign = HorizontalAlignment.Center;
        }

        public void SetHotkey(uint mods, int key)
        {
            Mods = mods;
            Key = key;
            Text = Format(mods, key);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            Keys k = keyData & Keys.KeyCode;
            if (k == Keys.ShiftKey || k == Keys.ControlKey || k == Keys.Menu || k == Keys.LWin || k == Keys.RWin || k == Keys.None)
                return true;
            if (k == Keys.Tab && (keyData & Keys.Modifiers) == 0) return base.ProcessCmdKey(ref msg, keyData);
            if (k == Keys.Back || k == Keys.Delete || k == Keys.Escape)
            {
                Mods = 0; Key = 0;
            }
            else
            {
                uint m = 0;
                if ((keyData & Keys.Alt) != 0) m |= Native.MOD_ALT;
                if ((keyData & Keys.Control) != 0) m |= Native.MOD_CONTROL;
                if ((keyData & Keys.Shift) != 0) m |= Native.MOD_SHIFT;
                Mods = m;
                Key = (int)k;
            }
            Text = Format(Mods, Key);
            var h = HotkeyChanged;
            if (h != null) h(this, EventArgs.Empty);
            return true;
        }

        /// <summary>Tastenkürzel entfernen (wie Esc/Entf im Feld).</summary>
        public void ClearHotkey()
        {
            Mods = 0; Key = 0;
            Text = Format(0, 0);
            var h = HotkeyChanged;
            if (h != null) h(this, EventArgs.Empty);
        }

        public static string Format(uint mods, int key)
        {
            if (key == 0) return "(keins)";
            var parts = new List<string>();
            if ((mods & Native.MOD_CONTROL) != 0) parts.Add("Strg");
            if ((mods & Native.MOD_ALT) != 0) parts.Add("Alt");
            if ((mods & Native.MOD_SHIFT) != 0) parts.Add("Umschalt");
            string name = ((Keys)key).ToString();
            if (name.Length == 2 && name[0] == 'D' && char.IsDigit(name[1])) name = name.Substring(1);
            if (name.StartsWith("NumPad")) name = "Num " + name.Substring(6);
            parts.Add(name);
            return string.Join(" + ", parts.ToArray());
        }
    }

    /// <summary>Auswahl eines laufenden Programms für eine App-Spur.</summary>
    public sealed class AppPickerForm : Form
    {
        readonly ListBox list = new ListBox();
        readonly TextBox manual = new TextBox();
        public string Selected;

        sealed class Entry
        {
            public string Key, Title, Desc;
            public bool Audio, Window;
            public override string ToString() { return Display(this); }
        }

        public AppPickerForm()
        {
            Text = "App auswählen";
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.None;
            Size = new Size(Theme.S(600), Theme.S(560));
            MinimumSize = new Size(Theme.S(460), Theme.S(380));
            Font = Theme.F(9f);
            ShowInTaskbar = false;
            MinimizeBox = false;
            MaximizeBox = false;
            BackColor = Theme.Bg;
            Theme.DarkTitleBar(this);

            var info = new Label
            {
                Dock = DockStyle.Top,
                Height = Theme.S(52),
                Padding = new Padding(Theme.S(14), Theme.S(12), Theme.S(14), 0),
                ForeColor = Theme.TextDim,
                Text = "Programme, die gerade Ton ausgeben, stehen oben. Tipp: App (z.B. Discord) vorher starten – oder unten den EXE-Namen eintippen."
            };

            list.Dock = DockStyle.Fill;
            list.IntegralHeight = false;
            Ui.DarkList(list, 46);
            list.DoubleClick += (s, e) => Accept();
            var listHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(Theme.S(12), 0, Theme.S(12), 0) };
            listHost.Controls.Add(list);

            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(Theme.S(10)) };
            var ok = new FxButton { Text = "Übernehmen", AutoSize = true, Kind = FxKind.Primary };
            var cancel = new FxButton { Text = "Abbrechen", AutoSize = true, DialogResult = DialogResult.Cancel };
            ok.Click += (s, e) => Accept();
            bottom.Controls.Add(ok);
            bottom.Controls.Add(cancel);
            bottom.Controls.Add(new FxField(manual, Theme.S(200), "oder EXE-Name, z.B. discord", "edit"));
            AcceptButton = ok;
            CancelButton = cancel;

            Controls.Add(listHost);
            Controls.Add(bottom);
            Controls.Add(info);
            Theme.Apply(this);
            Load += (s, e) => Fill();
        }

        void Fill()
        {
            Cursor = Cursors.WaitCursor;
            var audioPids = Native.RunMta(() => CoreAudio.ListSessionPids(), new List<int>());
            var procs = Native.SnapshotProcesses();
            var audioNames = new HashSet<string>();
            foreach (int pid in audioPids)
            {
                ProcInfo p;
                if (procs.TryGetValue(pid, out p)) audioNames.Add(p.Name);
            }

            var entries = new Dictionary<string, Entry>();
            int own = Process.GetCurrentProcess().Id;
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.Id == own || p.Id <= 4) continue;
                    string key = Native.ExeKey(p.ProcessName);
                    bool win = false;
                    string title = null;
                    try { win = p.MainWindowHandle != IntPtr.Zero; if (win) title = p.MainWindowTitle; } catch { }
                    bool audio = audioNames.Contains(key);
                    if (!win && !audio) continue;
                    Entry e;
                    if (!entries.TryGetValue(key, out e))
                    {
                        e = new Entry { Key = key };
                        entries[key] = e;
                        string path = Native.GetProcessPath(p.Id);
                        if (path != null)
                        {
                            try { e.Desc = FileVersionInfo.GetVersionInfo(path).FileDescription; } catch { }
                        }
                    }
                    e.Audio |= audio;
                    e.Window |= win;
                    if (string.IsNullOrEmpty(e.Title) && !string.IsNullOrEmpty(title)) e.Title = title;
                }
                catch { }
                finally { p.Dispose(); }
            }

            var sorted = new List<Entry>(entries.Values);
            sorted.Sort((a, b) =>
            {
                if (a.Audio != b.Audio) return a.Audio ? -1 : 1;
                return string.Compare(Display(a), Display(b), StringComparison.CurrentCultureIgnoreCase);
            });
            list.BeginUpdate();
            foreach (var e in sorted) list.Items.Add(e);
            list.EndUpdate();
            Cursor = Cursors.Default;
        }

        static string Display(Entry e)
        {
            if (!string.IsNullOrEmpty(e.Desc) && e.Desc.Trim().Length > 1) return e.Desc.Trim();
            if (!string.IsNullOrEmpty(e.Title)) return e.Title;
            return e.Key;
        }

        /// <summary>Zweite Zeile im Listeneintrag.</summary>
        public static string SubText(object o)
        {
            var e = o as Entry;
            if (e == null) return null;
            return e.Key + ".exe  ·  " + (e.Audio ? "gibt gerade Ton aus" : "Fenster offen");
        }

        public static bool IsHighlighted(object o)
        {
            var e = o as Entry;
            return e != null && e.Audio;
        }

        void Accept()
        {
            if (manual.Text.Trim().Length > 0) Selected = Native.ExeKey(Path.GetFileName(manual.Text.Trim()));
            else if (list.SelectedItem is Entry) Selected = ((Entry)list.SelectedItem).Key;
            if (!string.IsNullOrEmpty(Selected))
            {
                DialogResult = DialogResult.OK;
                Close();
            }
        }
    }

    public static class Ui
    {
        public static TableLayoutPanel Grid()
        {
            var t = new TableLayoutPanel
            {
                ColumnCount = 2,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Top,
                Padding = new Padding(0)
            };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            return t;
        }

        public static void Row(TableLayoutPanel t, string label, Control c)
        {
            int r = t.RowCount;
            t.RowCount = r + 1;
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            if (label != null)
            {
                // Einzelne Controls in ein AutoSize-Panel packen – sonst bekommen sie bei Windows-Skalierung u.U. Breite 0
                if (!(c is FlowLayoutPanel)) c = Flow(c);
                var l = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, Theme.S(6), Theme.S(18), Theme.S(4)), Font = Theme.F(9f), ForeColor = Theme.TextDim, Tag = "keep" };
                t.Controls.Add(l, 0, r);
                t.Controls.Add(c, 1, r);
            }
            else
            {
                t.Controls.Add(c, 0, r);
                t.SetColumnSpan(c, 2);
            }
            c.Margin = new Padding(0, Theme.S(4), 0, Theme.S(4));
        }

        public static Card Group(string title, Control content)
        {
            var g = new Card
            {
                Text = title,
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink
            };
            content.Dock = DockStyle.Top;
            g.Controls.Add(content);
            return g;
        }

        public static FlowLayoutPanel Flow(params Control[] cs)
        {
            var f = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0) };
            foreach (var c in cs) f.Controls.Add(c);
            return f;
        }

        public static FxCombo Combo(int width)
        {
            return new FxCombo { Width = width };
        }

        public static FxButton Button(string text, EventHandler click)
        {
            var b = new FxButton { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            if (click != null) b.Click += click;
            return b;
        }

        public static FxButton Button(string icon, string text, EventHandler click)
        {
            var b = Button(text, click);
            b.IconName = icon;
            return b;
        }

        public static FxButton IconButton(string icon, string tip, EventHandler click)
        {
            var b = new FxButton { IconName = icon, Kind = FxKind.Ghost, Size = new Size(Theme.S(34), Theme.S(32)), IconSize = 11f };
            if (click != null) b.Click += click;
            if (tip != null) Tips.Set(b, tip);
            return b;
        }

        public static FxField Field(TextBox tb, int width, string cue = null, string icon = null)
        {
            return new FxField(tb, width, cue, icon);
        }

        public static Label Hint(string text, int maxWidth)
        {
            return new Label { Text = text, AutoSize = true, ForeColor = Theme.TextDim, MaximumSize = new Size(maxWidth, 0), Font = Theme.F(8.5f), Tag = "keep", Margin = new Padding(0, Theme.S(2), 0, Theme.S(4)) };
        }

        /// <summary>ListBox dunkel und selbst gezeichnet (Auswahl mit Akzent-Balken).</summary>
        public static void DarkList(ListBox lb, int itemHeight)
        {
            lb.DrawMode = DrawMode.OwnerDrawFixed;
            lb.ItemHeight = Theme.S(itemHeight);
            lb.BorderStyle = BorderStyle.None;
            lb.BackColor = Theme.Panel2;
            lb.ForeColor = Theme.Text;
            lb.Font = Theme.F(9.5f);
            lb.Tag = "keep";
            Theme.DarkScroll(lb);
            lb.DrawItem += (s, e) =>
            {
                if (e.Index < 0) { e.Graphics.Clear(lb.BackColor); return; }
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                bool sel = (e.State & DrawItemState.Selected) != 0;
                using (var b = new SolidBrush(lb.BackColor)) g.FillRectangle(b, e.Bounds);
                var r = new RectangleF(e.Bounds.X + Theme.S(4), e.Bounds.Y + Theme.S(2), e.Bounds.Width - Theme.S(8), e.Bounds.Height - Theme.S(4));
                if (sel)
                {
                    Gfx.FillRound(g, Gfx.Lerp(lb.BackColor, Theme.Accent, 0.22f), r, Theme.S(6));
                    using (var b = new SolidBrush(Theme.Accent)) g.FillRectangle(b, r.X, r.Y + Theme.S(6), Theme.S(3), r.Height - Theme.S(12));
                }
                object item = lb.Items[e.Index];
                string sub = AppPickerForm.SubText(item);
                var tr = new Rectangle((int)r.X + Theme.S(12), (int)r.Y, (int)r.Width - Theme.S(16), (int)r.Height);
                if (sub != null)
                {
                    var f1 = AppPickerForm.IsHighlighted(item) ? Theme.Semi(9.5f) : Theme.F(9.5f);
                    TextRenderer.DrawText(g, item.ToString(), f1, new Rectangle(tr.X, tr.Y + Theme.S(4), tr.Width, tr.Height / 2), Theme.Text, Gfx.Left);
                    TextRenderer.DrawText(g, sub, Theme.F(8.5f), new Rectangle(tr.X, tr.Y + tr.Height / 2, tr.Width, tr.Height / 2 - Theme.S(4)),
                        AppPickerForm.IsHighlighted(item) ? Theme.Accent : Theme.TextDim, Gfx.Left);
                }
                else TextRenderer.DrawText(g, item.ToString(), lb.Font, tr, Theme.Text, Gfx.Left);
            };
        }

        /// <summary>Tray-/Fenster-Icon zur Laufzeit zeichnen (aktiv = roter Punkt).</summary>
        public static Icon MakeIcon(bool active)
        {
            using (var bmp = new Bitmap(32, 32))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    // Neues Design: dunkler Kreis, Ring mit Farbverlauf (Akzent -> Sekundär), Punkt in der Mitte
                    using (var b = new SolidBrush(Color.FromArgb(22, 20, 32))) g.FillEllipse(b, 1, 1, 30, 30);
                    var r = new Rectangle(5, 5, 22, 22);
                    using (var gb = new LinearGradientBrush(r, active ? Theme.Accent : Color.FromArgb(130, 132, 145), active ? Theme.Secondary : Color.FromArgb(95, 98, 110), 45f))
                    using (var p = new Pen(gb, 3f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                        g.DrawArc(p, r, -60, 300);
                    using (var b = new SolidBrush(active ? Color.FromArgb(255, 70, 90) : Color.FromArgb(150, 152, 165))) g.FillEllipse(b, 11, 11, 10, 10);
                }
                IntPtr h = bmp.GetHicon();
                var ic = (Icon)Icon.FromHandle(h).Clone();
                try { Native.DestroyIcon(h); } catch { }
                return ic;
            }
        }
    }

    /// <summary>Gemeinsame, dunkle Tooltips.</summary>
    public static class Tips
    {
        static ToolTip tip;

        public static void Set(Control c, string text)
        {
            if (tip == null)
            {
                tip = new ToolTip { OwnerDraw = true, InitialDelay = 450, ReshowDelay = 100, AutoPopDelay = 8000 };
                tip.Popup += (s, e) =>
                {
                    var sz = TextRenderer.MeasureText(tip.GetToolTip(e.AssociatedControl), Theme.F(8.5f));
                    e.ToolTipSize = new Size(sz.Width + Theme.S(16), sz.Height + Theme.S(10));
                };
                tip.Draw += (s, e) =>
                {
                    e.Graphics.Clear(Theme.Panel2);
                    using (var p = new Pen(Theme.Border)) e.Graphics.DrawRectangle(p, 0, 0, e.Bounds.Width - 1, e.Bounds.Height - 1);
                    TextRenderer.DrawText(e.Graphics, e.ToolTipText, Theme.F(8.5f), e.Bounds, Theme.Text, Gfx.Center);
                };
            }
            tip.SetToolTip(c, text);
        }
    }

    public sealed class MeterPanel : Panel, IFxControl
    {
        public List<TrackStatus> Data;
        public string Hint = "";

        public MeterPanel()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Panel2;
            Font = Theme.F(9f);
        }

        static int S(float v) { return Theme.S(v); }

        int BarW { get { return Math.Max(S(120), Math.Min(S(300), Width - S(520))); } }

        bool SummaryOwnLine(TrackStatus t)
        {
            int sx = S(72) + S(170) + BarW;
            int avail = Width - sx - S(6);
            return TextRenderer.MeasureText(t.Summary ?? "", Font).Width > avail;
        }

        public int NeededHeight()
        {
            if (Data == null) return S(60);
            int h = S(10);
            foreach (var t in Data) h += S(28) + (SummaryOwnLine(t) ? S(19) : 0) + t.Sources.Count * S(20) + S(8);
            return h + S(4);
        }

        static float ToBar(float level)
        {
            if (level <= 0.000001f) return 0f;
            double db = 20 * Math.Log10(level);
            return (float)Math.Max(0, Math.Min(1, (db + 60) / 60));
        }

        static void DrawBar(Graphics g, RectangleF r, float level)
        {
            Gfx.FillRound(g, Theme.Bg, r, r.Height / 2);
            float f = ToBar(level);
            if (f <= 0) return;
            var fill = new RectangleF(r.X, r.Y, Math.Max(r.Height, r.Width * f), r.Height);
            using (var b = new LinearGradientBrush(new RectangleF(r.X - 1, r.Y, r.Width + 2, r.Height), Theme.Accent, Theme.Danger, 0f))
            {
                b.InterpolationColors = new ColorBlend
                {
                    Colors = new[] { Theme.Accent, Theme.Accent, Theme.Warning, Theme.Danger },
                    Positions = new[] { 0f, 0.7f, 0.88f, 1f }
                };
                Gfx.FillRound(g, b, fill, r.Height / 2);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            var flags = Gfx.Left;
            if (Data == null)
            {
                TextRenderer.DrawText(g, Hint, Font, new Rectangle(S(14), S(10), Width - S(28), S(48)), Theme.TextDim, TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                return;
            }
            var bold = Theme.Semi(9.5f);
            var small = Theme.F(8.5f);
            int y = S(10);
            int nameW = S(170), barW = BarW;
            foreach (var t in Data)
            {
                var dot = Theme.Palette(t.Index);
                using (var b = new SolidBrush(dot)) g.FillEllipse(b, S(14), y + S(8), S(8), S(8));
                TextRenderer.DrawText(g, "Spur " + (t.Index + 1) + ": " + t.Name, bold, new Rectangle(S(28), y, nameW - S(14), S(24)), Theme.Text, flags);
                DrawBar(g, new RectangleF(S(14) + nameW, y + S(7), barW, S(10)), t.Level);
                double db = t.Level > 0.000001f ? 20 * Math.Log10(t.Level) : -99;
                string dbs = db > -60 ? db.ToString("0") + " dB" : "Stille";
                TextRenderer.DrawText(g, dbs, small, new Rectangle(S(22) + nameW + barW, y, S(52), S(24)), Theme.Text, flags);
                int sx = S(78) + nameW + barW;
                if (SummaryOwnLine(t))
                {
                    y += S(28);
                    TextRenderer.DrawText(g, t.Summary ?? "", small, new Rectangle(S(28), y, Width - S(34), S(19)), t.Problem ? Theme.Danger : Theme.TextDim, flags);
                    y += S(19);
                }
                else
                {
                    TextRenderer.DrawText(g, t.Summary ?? "", small, new Rectangle(sx, y, Width - sx - S(6), S(24)), t.Problem ? Theme.Danger : Theme.TextDim, flags);
                    y += S(28);
                }
                foreach (var s in t.Sources)
                {
                    DrawBar(g, new RectangleF(S(28), y + S(6), S(60), S(7)), s.Level);
                    string line = s.Description + "  —  " + s.State;
                    TextRenderer.DrawText(g, line, small, new Rectangle(S(96), y, Width - S(102), S(20)), s.Problem ? Theme.Danger : Theme.TextDim, flags);
                    y += S(20);
                }
                y += S(8);
                using (var p = new Pen(Theme.Border)) g.DrawLine(p, S(14), y - S(4), Width - S(14), y - S(4));
            }
        }
    }
}
