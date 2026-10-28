using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace ReplayClip
{
    /// <summary>Hauptfenster = Clip-Bibliothek. Status/Buffer-Steuerung oben, Einstellungen als eigenes Fenster.</summary>
    public sealed class MainForm : Form, ISettingsHost
    {
        const int HK_SAVE = 1, HK_TOGGLE = 2;
        public static readonly int WM_SHOWME = SafeRegisterMessage("ReplayClip_ShowWindow_v1");

        static int SafeRegisterMessage(string name)
        {
            try { return Native.RegisterWindowMessage(name); } catch { return 0; }
        }

        readonly Settings settings;
        readonly Recorder recorder;
        readonly bool startHidden;
        bool allowVisible, exiting, balloonShown, starting;
        string lastClip;
        string statusMsg = "";
        string hotkeyWarning = "";
        static int Px(float v) { return Theme.S(v); }

        // Kopf
        HeaderBar header;
        FxButton btnToggle, btnSaveNow, btnSettings;
        Panel banner;
        Label bannerText;
        FxButton bannerButton;
        Action bannerAction;
        Label stLeft, stRight;
        NotifyIcon tray;
        ToolStripMenuItem trayToggle;
        Icon iconOn, iconOff;
        PostTimer uiTimer, meterTimer;

        // Bibliothek
        NavList nav;
        ClipBrowser browser;
        ThumbCache thumbs;
        TextBox txtSearch;
        FxCombo cmbSort, cmbTile;
        FxButton btnTiles, btnList, btnDetails;
        Panel details;
        List<ClipItem> all = new List<ClipItem>();
        readonly Dictionary<string, ClipItem> rendering = new Dictionary<string, ClipItem>(StringComparer.OrdinalIgnoreCase);
        int filterKind = 1;           // 1 alle, 2 Favoriten, 3 Schnitte, 4 Spiel, 5 Tag
        string filterValue = "";
        bool scanning, rescanAgain;
        string selectAfterScan;
        Thread probeThread;
        readonly Queue<ClipItem> probeQueue = new Queue<ClipItem>();
        bool probeDirty;

        // Details
        Panel detThumb;
        Label detName, detInfo;
        LinkLabel detLink;
        FxButton detFav;
        FlowLayoutPanel detTags, detQuick;
        TextBox detTagBox;
        FxField detTagField;
        FlowLayoutPanel detActions;
        Label detTagsTitle, detQuickTitle;
        readonly Tween detFade;

        public MainForm(bool minimized)
        {
            settings = Settings.Load();
            startHidden = minimized || settings.StartMinimized;
            recorder = new Recorder(() => settings);
            Thumbnailer.FfmpegPath = () => Ffmpeg.Find(settings);

            SuspendLayout();
            AutoScaleMode = AutoScaleMode.None;
            Text = "ReplayClip " + Program.Version;
            Font = Theme.F(9f);
            Size = new Size(Px(1360), Px(880));
            MinimumSize = new Size(Px(1000), Px(620));
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            KeyPreview = true;
            DoubleBuffered = true;
            Theme.DarkTitleBar(this);
            iconOn = Ui.MakeIcon(true);
            iconOff = Ui.MakeIcon(false);
            Icon = iconOff;
            detFade = new Tween(this, 1, 12);

            thumbs = new ThumbCache(this);
            BuildLayout();
            BuildTray();
            Theme.Apply(this);
            ResumeLayout(false);
            PerformLayout();

            recorder.StateChanged += (on, msg) => OnUi(() => OnRecorderState(on, msg));
            recorder.SaveStarted += () => OnUi(() => { if (settings.HotkeySound) SoundFx.Play("press", null, settings.SoundVolume, settings); statusMsg = "Speichere Clip …"; header.State = HeaderBar.RecState.Saving; UpdateStatus(); });
            recorder.ClipSaved += file => OnUi(() => OnClipSaved(file));
            recorder.SaveFailed += msg => OnUi(() => OnSaveFailed(msg));
            thumbs.Loaded += p => { browser.ThumbLoaded(p); detThumb.Invalidate(); };

            uiTimer = new PostTimer(this) { Interval = 500 };
            uiTimer.Tick += (s, e) =>
            {
                UpdateStatus();
                if (probeDirty) { probeDirty = false; browser.Invalidate(); UpdateDetails(); }
            };
            uiTimer.Start();
            meterTimer = new PostTimer(this) { Interval = 33 };
            meterTimer.Tick += (s, e) => UpdateMeters();
            meterTimer.Start();
            Activated += (s, e) => { if (!scanning && Visible) Rescan(); };
        }

        void OnUi(Action a)
        {
            if (IsDisposed) return;
            try { if (InvokeRequired) BeginInvoke(a); else a(); }
            catch (InvalidOperationException) { }
        }

        // ======================= ISettingsHost =======================

        public Recorder Recorder { get { return recorder; } }
        public string HotkeyWarning { get { return hotkeyWarning; } }

        public void UnregisterHotkeys()
        {
            if (!IsHandleCreated) return;
            try { Native.UnregisterHotKey(Handle, HK_SAVE); Native.UnregisterHotKey(Handle, HK_TOGGLE); } catch { }
        }

        public void RestartNeeded()
        {
            if (recorder.IsActive)
                ShowBanner("Geänderte Aufnahme-Einstellungen gelten nach einem Neustart des Replay-Buffers (der aktuelle Puffer geht dabei verloren).", "Jetzt neu starten", RestartBuffer);
        }

        public void LibraryChanged()
        {
            BuildQuickButtons();
            Rescan();
        }

        public void FfmpegReady()
        {
            HideBanner();
            if (settings.StartBufferOnLaunch && !recorder.IsActive) StartBuffer();
        }

        public void AppearanceChanged()
        {
            iconOn = Ui.MakeIcon(true);
            iconOff = Ui.MakeIcon(false);
            Icon = recorder.IsActive ? iconOn : iconOff;
            tray.Icon = Icon;
            banner.BackColor = Gfx.Lerp(Theme.Bg, Theme.Warning, 0.14f);
            UpdateStatus();
            Theme.RefreshAll();
        }

        public void RestartApp()
        {
            try
            {
                settings.Save();
                Library.SaveNow();
                Process.Start(new ProcessStartInfo(Application.ExecutablePath, "--restart") { UseShellExecute = false });
            }
            catch { return; }
            ExitApp();
        }

        // ======================= Aufbau =======================

        void BuildLayout()
        {
            // ---- Kopfzeile ----
            header = new HeaderBar { Dock = DockStyle.Top, Version = "v" + Program.Version };
            btnSaveNow = new FxButton { Text = "Clip speichern", IconName = "save", AutoSize = true };
            btnSaveNow.Click += (s, e) => recorder.SaveClip();
            btnToggle = new FxButton { Text = "Buffer starten", IconName = "record", Kind = FxKind.Primary, AutoSize = false, Size = new Size(Px(170), Px(40)) };
            btnToggle.Click += (s, e) => ToggleBuffer();
            btnSettings = Ui.IconButton("settings", "Einstellungen", (s, e) => OpenSettings(null));
            btnSettings.Size = new Size(Px(42), Px(40));
            btnSettings.Kind = FxKind.Normal;
            btnSettings.Margin = new Padding(Px(4), 0, 0, 0);
            header.Controls.Add(btnSaveNow);
            header.Controls.Add(btnToggle);
            header.Controls.Add(btnSettings);
            header.Resize += (s, e) => LayoutHeader();
            foreach (var b in new Control[] { btnSaveNow, btnToggle, btnSettings }) b.SizeChanged += (s, e) => LayoutHeader();

            // ---- Hinweisbalken ----
            banner = new Panel { Dock = DockStyle.Top, Height = Px(46), BackColor = Gfx.Lerp(Theme.Bg, Theme.Warning, 0.14f), Visible = false, Padding = new Padding(Px(18), Px(7), Px(12), Px(7)), Tag = "keep" };
            bannerText = new Label { AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Gfx.Light(Theme.Warning, 0.4f), Font = Theme.Medium(9f), Tag = "keep" };
            bannerButton = new FxButton { Dock = DockStyle.Right, AutoSize = true, Kind = FxKind.Normal };
            bannerButton.Click += (s, e) => { var a = bannerAction; HideBanner(); if (a != null) a(); };
            banner.Controls.Add(bannerText);
            banner.Controls.Add(bannerButton);

            // ---- Statuszeile ----
            var status = new Panel { Dock = DockStyle.Bottom, Height = Px(28), BackColor = Theme.Panel, Padding = new Padding(Px(16), 0, Px(16), 0), Tag = "keep" };
            stLeft = new Label { Dock = DockStyle.Left, AutoSize = false, Width = Px(420), TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.TextDim, Font = Theme.F(8.5f), Tag = "keep" };
            stRight = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight, ForeColor = Theme.TextDim, Font = Theme.F(8.5f), Tag = "keep" };
            status.Controls.Add(stRight);
            status.Controls.Add(stLeft);

            // ---- Navigation links ----
            nav = new NavList { Dock = DockStyle.Left, Width = Px(240) };
            nav.ItemRightClicked += (i, p) =>
            {
                var en = nav.Items[i];
                if (en.Kind == 5 && en.Value != "\u0001") ShowTagColorMenu(en.Value, nav, p);
            };
            nav.SelectedIndexChanged += (s, e) =>
            {
                var it = nav.SelectedItem;
                if (it == null) return;
                filterKind = it.Kind;
                filterValue = it.Value;
                ApplyFilter();
            };
            var navLine = new Panel { Dock = DockStyle.Left, Width = 1, BackColor = Theme.Border, Tag = "keep" };

            // ---- Werkzeugleiste ----
            var tools = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(Px(14), Px(12), Px(14), Px(6)), BackColor = Theme.Bg };
            txtSearch = new TextBox();
            var search = Ui.Field(txtSearch, Px(220), "Suchen … (Name, Spiel, Tag)", "search");
            txtSearch.TextChanged += (s, e) => ApplyFilter();
            cmbSort = Ui.Combo(Px(150));
            cmbSort.Items.AddRange(new object[] { "Neueste zuerst", "Älteste zuerst", "Name", "Größe", "Dauer", "Spiel/App" });
            cmbSort.SelectedIndex = Math.Max(0, Math.Min(5, settings.GallerySort));
            cmbSort.SelectedIndexChanged += (s, e) => { settings.GallerySort = cmbSort.SelectedIndex; settings.Save(); ApplyFilter(); };
            btnTiles = Ui.IconButton("grid", "Kacheln", (s, e) => SetView(false));
            btnList = Ui.IconButton("list", "Liste", (s, e) => SetView(true));
            btnTiles.Kind = btnList.Kind = FxKind.Normal;
            cmbTile = Ui.Combo(Px(92));
            cmbTile.Items.AddRange(new object[] { "Klein", "Mittel", "Groß" });
            cmbTile.SelectedIndex = settings.TileSize;
            cmbTile.SelectedIndexChanged += (s, e) => { settings.TileSize = cmbTile.SelectedIndex; settings.Save(); browser.TileSize = settings.TileSize; browser.Relayout(); };
            btnDetails = Ui.IconButton("details", "Detailbereich ein/aus", (s, e) => ToggleDetails());
            btnDetails.Kind = FxKind.Normal;
            var refresh = Ui.IconButton("refresh", "Neu einlesen (F5)", (s, e) => Rescan());
            var folder = Ui.IconButton("folder", "Clips-Ordner öffnen", (s, e) => Shell.OpenFolder(settings.SaveDir));
            tools.Controls.Add(search);
            tools.Controls.Add(Spacer(6));
            tools.Controls.Add(cmbSort);
            tools.Controls.Add(Spacer(6));
            tools.Controls.Add(btnTiles);
            tools.Controls.Add(btnList);
            tools.Controls.Add(cmbTile);
            tools.Controls.Add(Spacer(6));
            tools.Controls.Add(btnDetails);
            tools.Controls.Add(refresh);
            tools.Controls.Add(folder);

            // ---- Clip-Ansicht ----
            browser = new ClipBrowser { Dock = DockStyle.Fill, Thumbs = thumbs, ListMode = settings.GalleryList, TileSize = settings.TileSize, Font = Theme.F(9f) };
            browser.SelectionChanged += () => { UpdateDetails(); FadeDetails(); };
            browser.Activated += it => OpenEditor(it.Path);
            browser.FavoriteClicked += it => { it.Meta.Favorite = !it.Meta.Favorite; Library.SaveSoon(); RebuildNav(); UpdateDetails(); browser.Invalidate(); };
            browser.MenuRequested += p => ShowClipMenu(browser.PointToScreen(p));
            browser.HeaderClicked += c =>
            {
                int[] map = { 2, 5, 0, 4, 3, -1 };
                if (c >= 0 && c < map.Length && map[c] >= 0) cmbSort.SelectedIndex = map[c];
            };

            // ---- Detailbereich rechts ----
            details = BuildDetails();
            details.Visible = settings.ShowDetails;

            var center = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            center.Controls.Add(browser);
            center.Controls.Add(tools);

            Controls.Add(center);
            Controls.Add(details);
            Controls.Add(navLine);
            Controls.Add(nav);
            Controls.Add(status);
            Controls.Add(banner);
            Controls.Add(header);
            UpdateViewButtons();
            BuildQuickButtons();
        }

        void LayoutHeader()
        {
            // Buttons rechtsbündig, Status-Block und Pegel dazwischen
            int x = header.Width - Px(18);
            foreach (var b in new Control[] { btnSettings, btnToggle, btnSaveNow })
            {
                if (b.AutoSize) b.Size = b.GetPreferredSize(Size.Empty);
                if (b == btnToggle && b.Width < Px(170)) b.Width = Px(170);
                x -= b.Width;
                b.Location = new Point(x, (header.Height - b.Height) / 2);
                x -= Px(8);
            }
            int right = x - Px(16);
            header.StatusLeft = Px(330);
            int metersLeft = header.StatusLeft + Px(320);
            header.MetersLeft = metersLeft;
            header.MetersWidth = Math.Min(Px(220), right - metersLeft);
            header.Invalidate();
        }

        static Control Spacer(int w) { return new Panel { Width = Px(w), Height = 1, Margin = new Padding(0) }; }

        void ToggleDetails()
        {
            settings.ShowDetails = !settings.ShowDetails;
            settings.Save();
            details.Visible = settings.ShowDetails;
            if (details.Visible) FadeDetails();
            UpdateViewButtons();
        }

        void SetView(bool list)
        {
            if (settings.GalleryList == list) return;
            settings.GalleryList = list;
            settings.Save();
            browser.ListMode = list;
            browser.Relayout();
            UpdateViewButtons();
        }

        void UpdateViewButtons()
        {
            btnTiles.Toggled = !settings.GalleryList;
            btnList.Toggled = settings.GalleryList;
            btnDetails.Toggled = settings.ShowDetails;
            cmbTile.Visible = !settings.GalleryList;
        }

        // ---------------- Navigation ----------------

        void RebuildNav()
        {
            int favs = 0, cuts = 0;
            var games = new SortedDictionary<string, int>(StringComparer.CurrentCultureIgnoreCase);
            var tags = new SortedDictionary<string, int>(StringComparer.CurrentCultureIgnoreCase);
            foreach (var it in all)
            {
                if (it.Meta.Favorite) favs++;
                if (it.IsCut) cuts++;
                int n;
                games.TryGetValue(it.Game, out n); games[it.Game] = n + 1;
                foreach (var t in it.Meta.Tags) { tags.TryGetValue(t, out n); tags[t] = n + 1; }
            }
            var items = new List<NavEntry>();
            items.Add(new NavEntry { Header = true, Text = "Bibliothek" });
            items.Add(new NavEntry { Kind = 1, Text = "Alle Clips", Icon = "library", Count = all.Count });
            items.Add(new NavEntry { Kind = 2, Text = "Favoriten", Icon = "starfill", Count = favs });
            items.Add(new NavEntry { Kind = 3, Text = "Schnitte", Icon = "cut", Count = cuts });
            if (games.Count > 0)
            {
                items.Add(new NavEntry { Header = true, Text = "Spiele & Apps" });
                foreach (var kv in games) items.Add(new NavEntry { Kind = 4, Text = kv.Key, Value = kv.Key, Count = kv.Value, Dot = Theme.HashColor(kv.Key) });
            }
            items.Add(new NavEntry { Header = true, Text = "Tags" });
            if (tags.Count == 0) items.Add(new NavEntry { Kind = 5, Text = "(noch keine Tags)", Value = "\u0001", Icon = "tag", Count = -1 });
            foreach (var kv in tags) items.Add(new NavEntry { Kind = 5, Text = kv.Key, Value = kv.Key, Count = kv.Value, Dot = Theme.TagColor(kv.Key) });

            int sel = 1;
            for (int i = 0; i < items.Count; i++)
                if (!items[i].Header && items[i].Kind == filterKind && (filterKind <= 3 || string.Equals(items[i].Value, filterValue, StringComparison.CurrentCultureIgnoreCase))) sel = i;
            nav.SetItems(items, sel);
        }

        // ---------------- Bibliothek laden / filtern ----------------

        void Rescan()
        {
            if (scanning) { rescanAgain = true; return; }
            scanning = true;
            string root = settings.SaveDir;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                List<ClipItem> items;
                try { items = Library.Scan(root); } catch { items = new List<ClipItem>(); }
                OnUi(() =>
                {
                    all = items;
                    scanning = false;
                    RebuildNav();
                    ApplyFilter();
                    if (selectAfterScan != null) { browser.SelectPath(selectAfterScan); selectAfterScan = null; }
                    StartProbing();
                    if (rescanAgain) { rescanAgain = false; Rescan(); }
                });
            });
        }

        void ApplyFilter()
        {
            string q = txtSearch.Text.Trim();
            var list = new List<ClipItem>();
            long size = 0;
            foreach (var it in all)
            {
                switch (filterKind)
                {
                    case 2: if (!it.Meta.Favorite) continue; break;
                    case 3: if (!it.IsCut) continue; break;
                    case 4: if (!string.Equals(it.Game, filterValue, StringComparison.CurrentCultureIgnoreCase)) continue; break;
                    case 5: if (!it.Meta.HasTag(filterValue)) continue; break;
                }
                if (q.Length > 0 && !Matches(it, q)) continue;
                list.Add(it);
                size += it.Size;
            }
            foreach (var ph in rendering.Values)
                if (ph != null && (filterKind == 0 || filterKind == 1 || filterKind == 3) && !list.Exists(x => string.Equals(x.Path, ph.Path, StringComparison.OrdinalIgnoreCase)))
                    list.Add(ph);
            Comparison<ClipItem> cmp;
            switch (cmbSort.SelectedIndex)
            {
                case 1: cmp = (a, b) => a.Date.CompareTo(b.Date); break;
                case 2: cmp = (a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase); break;
                case 3: cmp = (a, b) => b.Size.CompareTo(a.Size); break;
                case 4: cmp = (a, b) => b.Meta.Duration.CompareTo(a.Meta.Duration); break;
                case 5: cmp = (a, b) => { int c = string.Compare(a.Game, b.Game, StringComparison.CurrentCultureIgnoreCase); return c != 0 ? c : b.Date.CompareTo(a.Date); }; break;
                default: cmp = (a, b) => b.Date.CompareTo(a.Date); break;
            }
            list.Sort(cmp);
            int[] colForSort = { 2, 2, 0, 4, 3, 1 };
            browser.SortColumn = colForSort[Math.Max(0, cmbSort.SelectedIndex)];
            string hk = HotkeyBox.Format(settings.SaveHotkeyMods, settings.SaveHotkeyKey);
            if (filterKind == 2) browser.EmptyText = "Noch keine Favoriten.\nKlick auf den Stern eines Clips oder drück F.";
            else if (filterKind == 3) browser.EmptyText = "Noch keine Schnitte.\nDoppelklick auf einen Clip öffnet den Editor.";
            else if (q.Length > 0) browser.EmptyText = "Nichts gefunden für „" + q + "“.";
            else browser.EmptyText = "Noch keine Clips hier.\nStarte oben den Replay-Buffer und drück im Spiel " + hk + ".";
            browser.SetItems(list);
            stLeft.Text = list.Count == all.Count
                ? string.Format(CultureInfo.CurrentCulture, "{0} Clips  ·  {1}", list.Count, ClipBrowser.FmtSize(size))
                : string.Format(CultureInfo.CurrentCulture, "{0} von {1} Clips  ·  {2}", list.Count, all.Count, ClipBrowser.FmtSize(size));
        }

        static bool Matches(ClipItem it, string q)
        {
            var ci = CultureInfo.CurrentCulture.CompareInfo;
            if (ci.IndexOf(it.Name, q, CompareOptions.IgnoreCase) >= 0) return true;
            if (ci.IndexOf(it.Game, q, CompareOptions.IgnoreCase) >= 0) return true;
            foreach (var t in it.Meta.Tags) if (ci.IndexOf(t, q, CompareOptions.IgnoreCase) >= 0) return true;
            return false;
        }

        /// <summary>Dauer der Clips im Hintergrund ermitteln (einmalig, wird gespeichert).</summary>
        void StartProbing()
        {
            lock (probeQueue)
            {
                probeQueue.Clear();
                foreach (var it in all) if (it.Meta.Duration <= 0) probeQueue.Enqueue(it);
                if (probeQueue.Count == 0 || (probeThread != null && probeThread.IsAlive)) return;
            }
            probeThread = new Thread(() =>
            {
                while (true)
                {
                    ClipItem it;
                    lock (probeQueue)
                    {
                        if (probeQueue.Count == 0) break;
                        it = probeQueue.Dequeue();
                    }
                    string ff = Ffmpeg.Find(settings);
                    if (ff == null) break;
                    try
                    {
                        var info = ClipInfo.Probe(ff, it.Path);
                        if (info.Duration > 0) { it.Meta.Duration = info.Duration; probeDirty = true; Library.SaveSoon(); }
                    }
                    catch { }
                }
            }) { IsBackground = true, Priority = ThreadPriority.BelowNormal };
            probeThread.Start();
        }

        // ---------------- Detailbereich ----------------

        Panel BuildDetails()
        {
            var p = new Panel { Dock = DockStyle.Right, Width = Px(352), BackColor = Theme.Panel, Padding = new Padding(Px(18), Px(16), Px(14), Px(16)), AutoScroll = true, Tag = "keep" };
            Theme.DarkScroll(p);
            p.Paint += (s, e) => { using (var pen = new Pen(Theme.Border)) e.Graphics.DrawLine(pen, 0, 0, 0, p.Height); };
            var stack = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Theme.Panel };
            int w = Px(290);

            detThumb = new DoubleBufferedPanel { Width = w, Height = w * 9 / 16, BackColor = Theme.Panel, Margin = new Padding(0, 0, 0, Px(12)), Tag = "keep", Cursor = Cursors.Hand };
            detThumb.Paint += PaintDetailThumb;
            detThumb.MouseEnter += (s, e) => detThumb.Invalidate();
            detThumb.MouseLeave += (s, e) => detThumb.Invalidate();
            detThumb.Click += (s, e) => { var sel = browser.Selected; if (sel.Count > 0) OpenEditor(sel[0].Path); };
            detName = new Label { AutoSize = true, MaximumSize = new Size(w, 0), Font = Theme.Semi(12f), ForeColor = Theme.Text, Tag = "keep" };
            detInfo = new Label { AutoSize = true, MaximumSize = new Size(w, 0), ForeColor = Theme.TextDim, Font = Theme.F(9f), Margin = new Padding(0, Px(6), 0, Px(4)), Tag = "keep" };
            detLink = new LinkLabel { AutoSize = true, MaximumSize = new Size(w, 0), Visible = false, LinkColor = Theme.Cut, ActiveLinkColor = Color.White, Font = Theme.Medium(9f), Tag = "keep", Margin = new Padding(0, Px(2), 0, Px(4)) };
            detLink.LinkClicked += (s, e) => { var target = detLink.Tag as string; if (target != null) { filterKind = 1; filterValue = ""; txtSearch.Text = ""; RebuildNav(); ApplyFilter(); browser.SelectPath(target); } };

            detActions = new FlowLayoutPanel { AutoSize = true, Width = w + Px(10), MaximumSize = new Size(w + Px(10), 0), WrapContents = true, Margin = new Padding(0, Px(10), 0, Px(4)) };
            var bEdit = new FxButton { Text = "Bearbeiten / Schneiden", IconName = "cut", Kind = FxKind.Primary, Size = new Size(w, Px(40)), Margin = new Padding(0, 0, 0, Px(8)) };
            bEdit.Click += (s, e) => { var sel = browser.Selected; if (sel.Count > 0) OpenEditor(sel[0].Path); };
            detActions.Controls.Add(bEdit);
            int half = (w - Px(8)) / 2;
            detFav = ActionButton("star", "Favorit", half, (s, e) => ToggleFavorite());
            detActions.Controls.Add(detFav);
            detActions.Controls.Add(ActionButton("play", "Abspielen", half, (s, e) => { var sel = browser.Selected; if (sel.Count > 0) Shell.OpenFile(sel[0].Path); }));
            detActions.Controls.Add(ActionButton("folder", "Im Ordner", half, (s, e) => { var sel = browser.Selected; if (sel.Count > 0) Shell.SelectInExplorer(sel[0].Path); }));
            detActions.Controls.Add(ActionButton("copy", "Kopieren", half, (s, e) => CopySelected()));
            detActions.Controls.Add(ActionButton("rename", "Umbenennen", half, (s, e) => RenameSelected()));
            var del = ActionButton("delete", "Löschen", half, (s, e) => DeleteSelected());
            del.Kind = FxKind.Danger;
            detActions.Controls.Add(del);

            detTagsTitle = new Label { Text = "TAGS", AutoSize = true, Font = Theme.Semi(7.5f), ForeColor = Theme.TextDim, Margin = new Padding(0, Px(14), 0, Px(6)), Tag = "keep" };
            detTags = new FlowLayoutPanel { AutoSize = true, Width = w, MaximumSize = new Size(w + Px(6), 0), WrapContents = true };
            detTagBox = new TextBox { AutoCompleteMode = AutoCompleteMode.SuggestAppend, AutoCompleteSource = AutoCompleteSource.CustomSource };
            detTagField = Ui.Field(detTagBox, w, "Tag eingeben + Enter …", "tag");
            detTagField.Margin = new Padding(0, Px(4), 0, 0);
            detTagBox.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { AddTagToSelection(detTagBox.Text); detTagBox.Text = ""; e.SuppressKeyPress = true; }
                else if (e.KeyCode == Keys.Escape) { browser.Focus(); e.SuppressKeyPress = true; }
            };
            detQuickTitle = new Label { Text = "SCHNELL-TAGS  ·  TASTEN 1–9", AutoSize = true, Font = Theme.Semi(7.5f), ForeColor = Theme.TextDim, Margin = new Padding(0, Px(16), 0, Px(6)), Tag = "keep" };
            detQuick = new FlowLayoutPanel { AutoSize = true, Width = w, MaximumSize = new Size(w + Px(6), 0), WrapContents = true };

            stack.Controls.Add(detThumb);
            stack.Controls.Add(detName);
            stack.Controls.Add(detInfo);
            stack.Controls.Add(detLink);
            stack.Controls.Add(detActions);
            stack.Controls.Add(detTagsTitle);
            stack.Controls.Add(detTags);
            stack.Controls.Add(detTagField);
            stack.Controls.Add(detQuickTitle);
            stack.Controls.Add(detQuick);
            p.Controls.Add(stack);
            return p;
        }

        static FxButton ActionButton(string icon, string text, int width, EventHandler click)
        {
            var b = new FxButton { Text = text, IconName = icon, Size = new Size(width, Px(34)), Margin = new Padding(0, 0, Px(8), Px(8)), LeftAlign = true };
            b.Click += click;
            return b;
        }

        sealed class DoubleBufferedPanel : Panel
        {
            public DoubleBufferedPanel()
            {
                SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            }
        }

        void PaintDetailThumb(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Theme.Panel);
            var r = new Rectangle(0, 0, detThumb.Width, detThumb.Height);
            var sel = browser.Selected;
            if (sel.Count == 0)
            {
                Gfx.FillRound(g, Theme.Panel2, r, Px(10));
                int cx = r.Width / 2, cy = r.Height / 2;
                using (var p = new Pen(Gfx.Alpha(Theme.Accent, 110), 3f * Theme.Dpi)) g.DrawArc(p, cx - Px(26), cy - Px(26), Px(52), Px(52), -60, 300);
                using (var b = new SolidBrush(Gfx.Alpha(Theme.Rec, 170))) g.FillEllipse(b, cx - Px(9), cy - Px(9), Px(18), Px(18));
                return;
            }
            var bmp = thumbs.Get(sel[0].Path);
            using (var b = new SolidBrush(Color.Black)) g.FillRectangle(b, r);
            if (bmp != null) g.DrawImage(bmp, r);
            bool hot = detThumb.ClientRectangle.Contains(detThumb.PointToClient(Cursor.Position));
            if (sel.Count > 1)
            {
                using (var b = new SolidBrush(Color.FromArgb(170, 0, 0, 0))) g.FillRectangle(b, r);
                TextRenderer.DrawText(g, "+" + (sel.Count - 1), Theme.Semi(18f), r, Color.White, Gfx.Center);
            }
            else
            {
                float pr = Px(hot ? 26 : 22);
                var pc = new RectangleF(r.Width / 2f - pr, r.Height / 2f - pr, pr * 2, pr * 2);
                using (var b = new SolidBrush(Gfx.Alpha(Theme.Accent, hot ? 240 : 170))) g.FillEllipse(b, pc);
                Gfx.Icon(g, "cut", hot ? 14f : 12f, Rectangle.Round(pc), Theme.OnAccent);
            }
            Gfx.RoundedImage(g, null, r, Px(10), Theme.Panel);
            float a = detFade.Value;
            if (a < 1) using (var b = new SolidBrush(Gfx.Alpha(Theme.Panel, (int)(255 * (1 - a))))) g.FillRectangle(b, r);
        }

        void FadeDetails()
        {
            if (!details.Visible) return;
            detFade.Changed = () => detThumb.Invalidate();
            detFade.Set(0.25f);
            detFade.To(1);
        }

        void BuildQuickButtons()
        {
            if (detQuick == null) return;
            detQuick.SuspendLayout();
            detQuick.Controls.Clear();
            for (int i = 0; i < settings.QuickTags.Count; i++)
            {
                string tag = settings.QuickTags[i];
                var b = new FxButton { Text = (i < 9 ? (i + 1) + "  " : "") + tag, AutoSize = true, Tag = tag, Margin = new Padding(0, 0, Px(6), Px(6)), Height = Px(30) };
                b.Click += (s, e) => ToggleTag(tag);
                b.MouseUp += (s, e) => { if (e.Button == MouseButtons.Right) ShowTagColorMenu(tag, (Control)s, e.Location); };
                Tips.Set(b, "Rechtsklick: Farbe ändern");
                detQuick.Controls.Add(b);
            }
            detQuick.ResumeLayout();
            UpdateDetails();
        }

        void UpdateDetails()
        {
            if (detName == null) return;
            var sel = browser.Selected;
            bool any = sel.Count > 0;
            foreach (Control c in new Control[] { detActions, detTagsTitle, detTags, detTagField, detQuickTitle, detQuick }) c.Visible = any;
            detThumb.Invalidate();
            detLink.Visible = false;
            string hk = HotkeyBox.Format(settings.SaveHotkeyMods, settings.SaveHotkeyKey);
            if (!any)
            {
                detName.Text = all.Count == 0 ? "Willkommen bei ReplayClip" : "Kein Clip ausgewählt";
                detInfo.Text = all.Count == 0
                    ? "Hier erscheinen deine Clips. Starte oben den Replay-Buffer und drück im Spiel " + hk + "."
                    : "Klick auf einen Clip für Details.\n\nTipps\n•  Doppelklick = schneiden\n•  1–9 = Schnell-Tag, F = Favorit\n•  Clips per Drag && Drop direkt in Discord ziehen\n•  Strg+F = Suche";
                return;
            }
            var tagsAll = new List<string>();
            var first = sel[0];
            foreach (var t in first.Meta.Tags)
            {
                bool inAll = true;
                foreach (var it in sel) if (!it.Meta.HasTag(t)) { inAll = false; break; }
                if (inAll) tagsAll.Add(t);
            }
            if (sel.Count == 1)
            {
                detName.Text = first.Name;
                var sb = new System.Text.StringBuilder();
                sb.AppendLine(first.Game);
                sb.AppendLine(first.Date.ToString("dddd, dd.MM.yyyy  ·  HH:mm", CultureInfo.CurrentCulture));
                string d = ClipBrowser.Dur(first.Meta.Duration);
                sb.Append(d.Length > 0 ? d + "  ·  " : "").Append(ClipBrowser.FmtSize(first.Size));
                detInfo.Text = sb.ToString();
                if (first.IsCut)
                {
                    bool exists = File.Exists(first.Meta.CutFrom);
                    detLink.Text = exists ? "✂  Schnitt aus: " + Path.GetFileNameWithoutExtension(first.Meta.CutFrom) : "✂  Schnitt (Original gelöscht)";
                    detLink.Tag = exists ? first.Meta.CutFrom : null;
                    detLink.Visible = true;
                }
                else
                {
                    int cuts = 0;
                    ClipItem cut = null;
                    foreach (var it in all) if (string.Equals(it.Meta.CutFrom, first.Path, StringComparison.OrdinalIgnoreCase)) { cuts++; cut = it; }
                    if (cuts > 0)
                    {
                        detLink.Text = cuts == 1 ? "✂  1 Schnitt davon: " + cut.Name : "✂  " + cuts + " Schnitte davon";
                        detLink.Tag = cut.Path;
                        detLink.Visible = true;
                    }
                }
            }
            else
            {
                long size = 0;
                double dur = 0;
                foreach (var it in sel) { size += it.Size; dur += it.Meta.Duration; }
                detName.Text = sel.Count + " Clips ausgewählt";
                detInfo.Text = ClipBrowser.FmtSize(size) + (dur > 0 ? "  ·  " + ClipBrowser.Dur(dur) + " gesamt" : "") + "\nTags und Favorit gelten für alle ausgewählten Clips.";
            }
            bool allFav = true;
            foreach (var it in sel) if (!it.Meta.Favorite) { allFav = false; break; }
            detFav.Text = allFav ? "Favorit" : "Favorit";
            detFav.IconName = allFav ? "starfill" : "star";
            detFav.Toggled = allFav;

            detTags.SuspendLayout();
            detTags.Controls.Clear();
            if (tagsAll.Count == 0) detTags.Controls.Add(new Label { Text = "Noch keine Tags – unten eintippen oder 1–9 drücken", AutoSize = true, ForeColor = Theme.TextDim, Font = Theme.F(8.5f), Tag = "keep" });
            foreach (var t in tagsAll)
            {
                string tag = t;
                var c = Theme.TagColor(tag);
                var chip = new FxButton { Text = tag + "   ✕", Kind = FxKind.Chip, Tint = Gfx.Lerp(Theme.Panel2, c, 0.3f), AutoSize = true, Height = Px(28), Margin = new Padding(0, 0, Px(6), Px(6)), Font = Theme.Medium(8.5f) };
                Tips.Set(chip, "Klick: Tag entfernen  ·  Rechtsklick: Farbe ändern");
                chip.Click += (s, e) => RemoveTagFromSelection(tag);
                chip.MouseUp += (s, e) => { if (e.Button == MouseButtons.Right) ShowTagColorMenu(tag, (Control)s, e.Location); };
                detTags.Controls.Add(chip);
            }
            detTags.ResumeLayout();

            foreach (Control c in detQuick.Controls)
            {
                var b = c as FxButton;
                if (b == null) continue;
                string tag = (string)b.Tag;
                bool on = true;
                foreach (var it in sel) if (!it.Meta.HasTag(tag)) { on = false; break; }
                b.Toggled = on;
            }
            var ac = new AutoCompleteStringCollection();
            var seen = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
            foreach (var it in all) foreach (var t in it.Meta.Tags) if (seen.Add(t)) ac.Add(t);
            foreach (var t in settings.QuickTags) if (seen.Add(t)) ac.Add(t);
            detTagBox.AutoCompleteCustomSource = ac;
        }

        // ---------------- Tags / Favoriten / Dateiaktionen ----------------

        static Bitmap Swatch(Color c)
        {
            int s = Theme.S(14);
            var b = new Bitmap(s, s);
            using (var g = Graphics.FromImage(b))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var br = new SolidBrush(c)) g.FillEllipse(br, 1, 1, s - 2, s - 2);
            }
            return b;
        }

        /// <summary>Menü zum Ändern der Tag-Farbe (Vorlagen, eigene Farbe, automatisch).</summary>
        void ShowTagColorMenu(string tag, Control at, Point p)
        {
            var m = new ContextMenuStrip();
            var head = new ToolStripMenuItem("Farbe für „" + tag + "“") { Image = Swatch(Theme.TagColor(tag)), Font = Theme.Semi(9f) };
            m.Items.Add(head);
            m.Items.Add(new ToolStripSeparator());
            int cur = Theme.TagColor(tag).ToArgb();
            var palette = new List<KeyValuePair<string, Color>>(Theme.Presets);
            for (int i = 0; i < 10; i++)
            {
                var pc = Theme.Palette(i);
                bool dup = false;
                foreach (var x in palette) if (x.Value.ToArgb() == pc.ToArgb()) dup = true;
                if (!dup) palette.Add(new KeyValuePair<string, Color>("Farbe " + (i + 1), pc));
            }
            foreach (var kv in palette)
            {
                var col = kv.Value;
                var it = new ToolStripMenuItem(kv.Key, Swatch(col), (s, e) => SetTagColor(tag, col)) { Checked = col.ToArgb() == cur && settings.TagColors.ContainsKey(tag) };
                m.Items.Add(it);
            }
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("Eigene Farbe …", null, (s, e) =>
            {
                using (var d = new ColorDialog { Color = Theme.TagColor(tag), FullOpen = true, AnyColor = true })
                    if (d.ShowDialog(this) == DialogResult.OK) SetTagColor(tag, d.Color);
            });
            var auto = m.Items.Add("Automatisch (Standard)", null, (s, e) => SetTagColor(tag, Color.Empty));
            auto.Enabled = settings.TagColors.ContainsKey(tag);
            Theme.Menu(m);
            m.Closed += (s, e) => BeginInvoke(new Action(m.Dispose));
            m.Show(at, p);
        }

        void SetTagColor(string tag, Color c)
        {
            if (c.IsEmpty) settings.TagColors.Remove(tag);
            else settings.TagColors[tag] = Color.FromArgb(255, c).ToArgb();
            settings.Save();
            Theme.Configure(settings);
            RebuildNav();
            UpdateDetails();
            browser.Invalidate();
        }

        void TagsChanged()
        {
            Library.SaveSoon();
            RebuildNav();
            if (filterKind == 5 || filterKind == 2) ApplyFilter();
            browser.Invalidate();
            UpdateDetails();
        }

        void ToggleTag(string tag)
        {
            var sel = browser.Selected;
            if (sel.Count == 0) return;
            bool allHave = true;
            foreach (var it in sel) if (!it.Meta.HasTag(tag)) { allHave = false; break; }
            foreach (var it in sel)
            {
                if (allHave) it.Meta.Tags.RemoveAll(x => string.Equals(x, tag, StringComparison.CurrentCultureIgnoreCase));
                else if (!it.Meta.HasTag(tag)) it.Meta.Tags.Add(tag);
            }
            stRight.Text = (allHave ? "Tag entfernt: " : "Tag gesetzt: ") + tag;
            TagsChanged();
        }

        void AddTagToSelection(string tag)
        {
            tag = (tag ?? "").Trim().Replace("|", "/").Replace("\t", " ");
            if (tag.Length == 0) return;
            foreach (var it in all) foreach (var t in it.Meta.Tags) if (string.Equals(t, tag, StringComparison.CurrentCultureIgnoreCase)) tag = t;
            foreach (var it in browser.Selected) if (!it.Meta.HasTag(tag)) it.Meta.Tags.Add(tag);
            TagsChanged();
        }

        void RemoveTagFromSelection(string tag)
        {
            foreach (var it in browser.Selected) it.Meta.Tags.RemoveAll(x => string.Equals(x, tag, StringComparison.CurrentCultureIgnoreCase));
            TagsChanged();
        }

        void ToggleFavorite()
        {
            var sel = browser.Selected;
            if (sel.Count == 0) return;
            bool allFav = true;
            foreach (var it in sel) if (!it.Meta.Favorite) { allFav = false; break; }
            foreach (var it in sel) it.Meta.Favorite = !allFav;
            TagsChanged();
        }

        void CopySelected()
        {
            var sel = browser.Selected;
            if (sel.Count == 0) return;
            var sc = new System.Collections.Specialized.StringCollection();
            foreach (var it in sel) sc.Add(it.Path);
            try
            {
                Clipboard.SetFileDropList(sc);
                Toast.Show(this, "Kopiert", sel.Count + " Clip(s) in der Zwischenablage – z.B. in Discord mit Strg+V einfügen.", "copy", Theme.Secondary);
            }
            catch { }
        }

        void DeleteSelected()
        {
            var sel = browser.Selected;
            if (sel.Count == 0) return;
            string q = sel.Count == 1 ? "„" + sel[0].Name + "“ wird in den Papierkorb verschoben." : sel.Count + " Clips werden in den Papierkorb verschoben.";
            if (!FxDialog.Ask(this, "Clip löschen?", q, "In den Papierkorb", "Abbrechen", true)) return;
            foreach (var it in sel)
            {
                if (RecycleFile(it.Path)) { Library.Remove(it.Path); thumbs.Invalidate(it.Path); }
            }
            Rescan();
        }

        static bool RecycleFile(string path)
        {
            try
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                return true;
            }
            catch { return false; }
        }

        void RenameSelected()
        {
            var sel = browser.Selected;
            if (sel.Count != 1) return;
            var it = sel[0];
            string name = InputBox.Show(this, "Umbenennen", "Neuer Name:", it.Name);
            if (name == null) return;
            name = Native.SafeFileName(name.Trim());
            if (name.Length == 0 || name == it.Name) return;
            string target = Path.Combine(Path.GetDirectoryName(it.Path), name + ".mp4");
            if (File.Exists(target)) { FxDialog.Info(this, "Name schon vergeben", "Es gibt schon einen Clip mit diesem Namen.", "warning"); return; }
            try
            {
                File.Move(it.Path, target);
                Library.Rename(it.Path, target);
                thumbs.Invalidate(it.Path);
                selectAfterScan = target;
                Rescan();
            }
            catch (Exception ex) { FxDialog.Info(this, "Umbenennen fehlgeschlagen", ex.Message, "error"); }
        }

        void ShowClipMenu(Point screen)
        {
            var sel = browser.Selected;
            if (sel.Count == 0) return;
            var m = new ContextMenuStrip();
            Theme.Menu(m);
            var edit = m.Items.Add("Bearbeiten / Schneiden", null, (s, e) => OpenEditor(sel[0].Path));
            edit.Font = Theme.Semi(9.5f);
            m.Items.Add("Im Standard-Player abspielen", null, (s, e) => Shell.OpenFile(sel[0].Path));
            m.Items.Add("Im Explorer anzeigen", null, (s, e) => Shell.SelectInExplorer(sel[0].Path));
            m.Items.Add("Kopieren (zum Einfügen in Discord)", null, (s, e) => CopySelected());
            if (sel.Count == 1) m.Items.Add("Umbenennen  (F2)", null, (s, e) => RenameSelected());
            m.Items.Add(new ToolStripSeparator());
            bool allFav = true;
            foreach (var it in sel) if (!it.Meta.Favorite) { allFav = false; break; }
            m.Items.Add(allFav ? "Favorit entfernen  (F)" : "Als Favorit markieren  (F)", null, (s, e) => ToggleFavorite());
            var tagMenu = new ToolStripMenuItem("Tags");
            for (int i = 0; i < settings.QuickTags.Count; i++)
            {
                string tag = settings.QuickTags[i];
                bool on = true;
                foreach (var it in sel) if (!it.Meta.HasTag(tag)) { on = false; break; }
                tagMenu.DropDownItems.Add(new ToolStripMenuItem((i < 9 ? (i + 1) + "   " : "") + tag, null, (s, e) => ToggleTag(tag)) { Checked = on });
            }
            tagMenu.DropDownItems.Add(new ToolStripSeparator());
            tagMenu.DropDownItems.Add(new ToolStripMenuItem("Eigenen Tag hinzufügen …", null, (s, e) =>
            {
                string t = InputBox.Show(this, "Tag hinzufügen", "Tag:", "");
                if (t != null) AddTagToSelection(t);
            }));
            m.Items.Add(tagMenu);
            Theme.Menu(tagMenu.DropDown);
            if (sel.Count == 1 && sel[0].IsCut && File.Exists(sel[0].Meta.CutFrom))
            {
                string orig = sel[0].Meta.CutFrom;
                m.Items.Add("Original anzeigen", null, (s, e) => { filterKind = 1; RebuildNav(); txtSearch.Text = ""; ApplyFilter(); browser.SelectPath(orig); });
            }
            m.Items.Add(new ToolStripSeparator());
            var del = m.Items.Add("In den Papierkorb  (Entf)", null, (s, e) => DeleteSelected());
            del.ForeColor = Theme.Danger;
            m.Show(screen);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            bool inText = ActiveControl is TextBox || (ActiveControl is ContainerControl && ((ContainerControl)ActiveControl).ActiveControl is TextBox) || txtSearch.Focused || detTagBox.Focused;
            if (keyData == (Keys.Control | Keys.F)) { txtSearch.Focus(); txtSearch.SelectAll(); return true; }
            if (!inText)
            {
                if (keyData >= Keys.D1 && keyData <= Keys.D9 || keyData >= Keys.NumPad1 && keyData <= Keys.NumPad9)
                {
                    int i = keyData >= Keys.NumPad1 ? keyData - Keys.NumPad1 : keyData - Keys.D1;
                    if (i < settings.QuickTags.Count) ToggleTag(settings.QuickTags[i]);
                    return true;
                }
                if (keyData == Keys.F) { ToggleFavorite(); return true; }
                if (keyData == Keys.Delete) { DeleteSelected(); return true; }
                if (keyData == Keys.F2) { RenameSelected(); return true; }
                if (keyData == (Keys.Control | Keys.C)) { CopySelected(); return true; }
                if (keyData == Keys.F5) { Rescan(); return true; }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // ---------------- Editor ----------------

        void OpenEditor(string path)
        {
            string ff = Ffmpeg.Find(settings);
            if (ff == null) { FxDialog.Info(this, "FFmpeg fehlt", "FFmpeg wird für den Editor benötigt (Einstellungen › Speicher & FFmpeg).", "warning"); return; }
            if (!File.Exists(path)) { Rescan(); return; }
            var ed = new EditorForm(ff, path) { Icon = Icon };
            ed.Exported += (target, overwrite) => OnExported(ed, path, target, overwrite);
            ed.ExportStarted += (src, target, overwrite) => OnExportStarted(src, target, overwrite);
            ed.ExportProgress += (target, pct) => browser.SetRendering(RenderKey(target), pct);
            ed.ExportFailed += (target, err) => { EndRendering(target); Toast.Show(this, "Schnitt fehlgeschlagen", err, "warning", Theme.Danger); };
            ed.Show(this);
        }

        /// <summary>Editor ist schon zu, der Schnitt rendert noch: Platzhalter-Kachel mit Fortschritt zeigen.</summary>
        void OnExportStarted(string src, string target, bool overwrite)
        {
            if (!overwrite)
            {
                var orig = all.Find(x => string.Equals(x.Path, src, StringComparison.OrdinalIgnoreCase));
                var meta = new ClipMeta();
                if (orig != null) foreach (var t in orig.Meta.Tags) meta.Tags.Add(t);
                meta.CutFrom = src;
                rendering[target] = new ClipItem
                {
                    Path = target, Name = Path.GetFileNameWithoutExtension(target), Date = DateTime.Now, Meta = meta,
                    Game = orig != null ? orig.Game : "", Placeholder = true, ThumbFrom = src
                };
                ApplyFilter();
            }
            else rendering[target] = null;
            browser.SetRendering(target, 0);
            Toast.Show(this, "Schnitt wird gespeichert", Path.GetFileName(target) + " – läuft im Hintergrund.", "save", Theme.Secondary);
        }

        string RenderKey(string target) { return target; }

        void EndRendering(string target)
        {
            bool had = rendering.ContainsKey(target) && rendering[target] != null;
            rendering.Remove(target);
            browser.SetRendering(target, -1);
            if (had) ApplyFilter();
        }

        void OnExported(EditorForm ed, string original, string target, bool overwrite)
        {
            EndRendering(target);
            if (!ed.Visible) Toast.Show(this, "Schnitt gespeichert", Path.GetFileName(target), "check", Theme.Accent);
            thumbs.Invalidate(target);
            if (overwrite)
            {
                var m = Library.Get(target);
                m.Duration = 0;
                Library.SaveSoon();
                selectAfterScan = target;
                Rescan();
                return;
            }
            var src = Library.Get(original);
            var meta = Library.Get(target);
            meta.CutFrom = original;
            meta.App = src.App.Length > 0 ? src.App : Library.GameFromPath(settings.SaveDir, original);
            foreach (var t in src.Tags) if (!meta.HasTag(t)) meta.Tags.Add(t);
            Library.SaveSoon();
            selectAfterScan = target;

            bool recycle = settings.AfterExport == 2;
            if (settings.AfterExport == 0)
            {
                string info = "";
                try { info = " (" + ClipBrowser.FmtSize(new FileInfo(original).Length) + ")"; } catch { }
                recycle = FxDialog.Show(ed.Visible ? (IWin32Window)ed : this, "Schnitt gespeichert", "Soll das Original" + info + " in den Papierkorb verschoben werden?\n\nIn den Einstellungen › Bibliothek kannst du festlegen, dass nicht mehr gefragt wird.",
                    "question", new[] { "Original löschen", "Behalten" }, 1, 1, false) == 0;
            }
            if (recycle)
            {
                if (ed.Visible) ed.SwitchTo(target);
                if (RecycleFile(original)) { Library.Remove(original); thumbs.Invalidate(original); }
            }
            Rescan();
        }

        // ---------------- Einstellungen ----------------

        void OpenSettings(string page)
        {
            using (var f = new SettingsForm(settings, this, page) { Icon = Icon })
                f.ShowDialog(this);
            BuildQuickButtons();
            Rescan();
            UpdateStatus();
        }

        // ======================= Fenster / Tray =======================

        protected override void SetVisibleCore(bool value)
        {
            if (!allowVisible && startHidden)
            {
                value = false;
                if (!IsHandleCreated) CreateHandle();
            }
            base.SetVisibleCore(value);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            RegisterHotkeys();
            BeginInvoke(new Action(AfterStartup));
        }

        void AfterStartup()
        {
            Rescan();
            Updater.CleanupOld();
            if (Program.JustUpdated)
                Toast.Show(this, "Aktualisiert", "ReplayClip " + Program.Version + " läuft jetzt.", "check", Theme.Accent);
            // Update-Suche: kurz nach dem Start und danach alle 6 Stunden
            var upd = new System.Windows.Forms.Timer { Interval = 6000 };
            upd.Tick += (s, e) => { upd.Interval = 6 * 3600 * 1000; AutoUpdateCheck(); };
            upd.Start();
            if (Ffmpeg.Find(settings) == null)
            {
                ShowBanner("FFmpeg wurde nicht gefunden – wird für Aufnahme, Vorschaubilder und Editor benötigt.", "Jetzt einrichten", () => OpenSettings("Speicher & FFmpeg"));
                if (startHidden) ShowFromTray();
            }
            else if (settings.StartBufferOnLaunch)
            {
                StartBuffer();
            }
            UpdateStatus();
        }

        void ShowFromTray()
        {
            allowVisible = true;
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY)
            {
                int id = m.WParam.ToInt32();
                if (id == HK_SAVE) recorder.SaveClip();
                else if (id == HK_TOGGLE) ToggleBuffer();
                return;
            }
            if (m.Msg == WM_SHOWME && WM_SHOWME != 0)
            {
                ShowFromTray();
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!exiting && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                if (!balloonShown)
                {
                    balloonShown = true;
                    updateBalloon = false; tray.ShowBalloonTip(3000, "ReplayClip läuft weiter", "Das Programm ist im Infobereich. Speichern mit " + HotkeyBox.Format(settings.SaveHotkeyMods, settings.SaveHotkeyKey) + ".", ToolTipIcon.Info);
                }
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (!exiting)
            {
                exiting = true;
                try { recorder.Stop(); } catch { }
                try { tray.Visible = false; } catch { }
            }
            Library.SaveNow();
            base.OnFormClosed(e);
        }

        public void ExitApp()
        {
            exiting = true;
            uiTimer.Stop();
            meterTimer.Stop();
            Cursor = Cursors.WaitCursor;
            try { recorder.Stop(); } catch { }
            UnregisterHotkeys();
            tray.Visible = false;
            tray.Dispose();
            settings.Save();
            Library.SaveNow();
            Application.Exit();
        }

        void BuildTray()
        {
            var menu = new ContextMenuStrip();
            Theme.Menu(menu);
            menu.Items.Add("Öffnen", null, (s, e) => ShowFromTray());
            menu.Items.Add("Clip speichern", null, (s, e) => recorder.SaveClip());
            trayToggle = new ToolStripMenuItem("Replay-Buffer starten", null, (s, e) => ToggleBuffer());
            menu.Items.Add(trayToggle);
            menu.Items.Add("Clips-Ordner öffnen", null, (s, e) => Shell.OpenFolder(settings.SaveDir));
            menu.Items.Add("Einstellungen …", null, (s, e) => { ShowFromTray(); OpenSettings(null); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Beenden", null, (s, e) => ExitApp());
            tray = new NotifyIcon { Icon = iconOff, Text = "ReplayClip " + Program.Version, ContextMenuStrip = menu, Visible = true };
            tray.DoubleClick += (s, e) => ShowFromTray();
            tray.BalloonTipClicked += (s, e) =>
            {
                ShowFromTray();
                if (lastClip != null && File.Exists(lastClip)) { filterKind = 1; RebuildNav(); ApplyFilter(); browser.SelectPath(lastClip); }
            };
        }

        public void RegisterHotkeys()
        {
            try { RegisterHotkeysCore(); } catch { }
        }

        void RegisterHotkeysCore()
        {
            if (!IsHandleCreated) return;
            UnregisterHotkeys();
            var warn = new List<string>();
            if (settings.SaveHotkeyKey != 0 && !Native.RegisterHotKey(Handle, HK_SAVE, settings.SaveHotkeyMods | Native.MOD_NOREPEAT, (uint)settings.SaveHotkeyKey))
                warn.Add(HotkeyBox.Format(settings.SaveHotkeyMods, settings.SaveHotkeyKey));
            // Tastenkürzel "Buffer an/aus" entfällt (Start/Stopp über den Knopf oben bzw. das Tray-Menü)
            if (false && settings.ToggleHotkeyKey != 0 && !Native.RegisterHotKey(Handle, HK_TOGGLE, settings.ToggleHotkeyMods | Native.MOD_NOREPEAT, (uint)settings.ToggleHotkeyKey))
                warn.Add(HotkeyBox.Format(settings.ToggleHotkeyMods, settings.ToggleHotkeyKey));
            hotkeyWarning = warn.Count > 0
                ? "Bereits von einem anderen Programm belegt: " + string.Join(", ", warn.ToArray()) + " (z.B. NVIDIA App – dort Instant Replay/Overlay deaktivieren oder hier eine andere Taste wählen)."
                : "";
            if (warn.Count > 0) ShowBanner(hotkeyWarning, "Tastenkürzel ändern", () => OpenSettings("Tastenkürzel"));
        }

        // ======================= Banner =======================

        void ShowBanner(string text, string button, Action action, bool info = false)
        {
            var tone = info ? Theme.Accent : Theme.Warning;
            banner.BackColor = Gfx.Lerp(Theme.Bg, tone, 0.14f);
            bannerText.ForeColor = Gfx.Light(tone, 0.4f);
            bannerText.Text = (info ? "★   " : "⚠   ") + text;
            bannerButton.Text = button ?? "";
            bannerButton.Visible = button != null;
            bannerAction = action;
            if (!banner.Visible)
            {
                banner.Height = 0;
                banner.Visible = true;
                Anim.Over(banner, 0.25f, t => banner.Height = (int)(Px(46) * t));
            }
        }

        void HideBanner()
        {
            if (!banner.Visible) return;
            bannerAction = null;
            int h0 = banner.Height;
            Anim.Over(banner, 0.2f, t => banner.Height = (int)(h0 * (1 - t)), () => { banner.Visible = false; banner.Height = Px(46); });
        }

        // ======================= Updates =======================

        UpdateInfo pendingUpdate;
        bool updating;
        public UpdateInfo PendingUpdate { get { return pendingUpdate; } }

        void AutoUpdateCheck()
        {
            if (!settings.UpdateCheck || updating || !Updater.RepoValid(settings.UpdateRepo)) return;
            Updater.CheckAsync(this, settings.UpdateRepo, (u, err) =>
            {
                if (err == null) { settings.LastUpdateCheck = DateTime.UtcNow.ToString("o"); settings.Save(); }
                if (u == null || u.Tag == settings.SkipUpdate) return;
                OfferUpdate(u, false);
                NotifyUpdate(u);
            });
        }

        public void OfferUpdate(UpdateInfo u, bool fromSettings)
        {
            pendingUpdate = u;
            if (fromSettings) { AskInstallUpdate(); return; }
            ShowBanner("ReplayClip " + u.Display + " ist verfügbar (du hast " + Program.Version + ").", "Ansehen & aktualisieren", AskInstallUpdate, true);
        }

        /// <summary>Mitteilung über eine neue Version: im Fenster (Hinweis) und als Windows-Mitteilung, wenn ReplayClip im Hintergrund ist.</summary>
        void NotifyUpdate(UpdateInfo u)
        {
            bool inFront = Visible && WindowState != FormWindowState.Minimized && ContainsFocus;
            if (Visible) Toast.Show(this, "Neue Version " + u.Display, "Klicken zum Ansehen und Aktualisieren", "refresh", Theme.Accent, null, AskInstallUpdate);
            if (!inFront && settings.NotifiedUpdate != u.Tag)
            {
                settings.NotifiedUpdate = u.Tag;
                settings.Save();
                try
                {
                    tray.BalloonTipClicked -= OnUpdateBalloonClicked;
                    tray.BalloonTipClicked += OnUpdateBalloonClicked;
                    updateBalloon = true;
                    tray.ShowBalloonTip(8000, "ReplayClip " + u.Display + " ist verfügbar", "Klicken zum Ansehen und Aktualisieren (du hast " + Program.Version + ").", ToolTipIcon.Info);
                }
                catch { }
            }
        }

        bool updateBalloon;

        void OnUpdateBalloonClicked(object s, EventArgs e)
        {
            if (!updateBalloon) return;
            updateBalloon = false;
            ShowFromTray();
            AskInstallUpdate();
        }

        void AskInstallUpdate()
        {
            var u = pendingUpdate;
            if (u == null || updating) return;
            string notes = (u.Notes ?? "").Trim();
            if (notes.Length > 900) notes = notes.Substring(0, 900) + " …";
            var owner = Form.ActiveForm ?? this;
            int r = FxDialog.Show(owner, "Update auf " + u.Display,
                "Neu in " + u.Display + ":\n\n" + (notes.Length > 0 ? notes : "(keine Beschreibung)") +
                "\n\nReplayClip lädt das Update, startet neu und läuft danach direkt weiter. Ein laufender Puffer geht dabei verloren.",
                "info", new[] { "Jetzt aktualisieren", "Später", "Diese Version überspringen" }, 0, 1, false);
            if (r == 2) { settings.SkipUpdate = u.Tag; settings.Save(); return; }
            if (r != 0) { ShowBanner("ReplayClip " + u.Display + " ist verfügbar.", "Aktualisieren", AskInstallUpdate, true); return; }
            updating = true;
            // Einstellungen schließen, damit man den Fortschritt im Hauptfenster sieht
            foreach (Form f in new List<Form>(Application.OpenForms.Cast<Form>()))
                if (f is SettingsForm) f.Close();
            ShowFromTray();
            ShowBanner("Lade Update " + u.Display + " …", null, null, true);
            Updater.Install(this, u,
                pct => bannerText.Text = "★   Lade Update " + u.Display + " … " + pct + " %",
                err =>
                {
                    updating = false;
                    if (err != null)
                    {
                        ShowBanner("Update fehlgeschlagen: " + err, "Release-Seite öffnen", () => Shell.OpenFile(u.PageUrl));
                        return;
                    }
                    bannerText.Text = "★   Update installiert – starte neu …";
                    try
                    {
                        settings.Save();
                        Library.SaveNow();
                        Process.Start(new ProcessStartInfo(Application.ExecutablePath, "--updated") { UseShellExecute = false });
                    }
                    catch (Exception ex)
                    {
                        ShowBanner("Neustart fehlgeschlagen: " + ex.Message + " – bitte ReplayClip selbst neu starten.", null, null);
                        return;
                    }
                    ExitApp();
                });
        }

        // ======================= Recorder-Steuerung =======================

        void ToggleBuffer()
        {
            if (recorder.IsActive) StopBuffer(); else StartBuffer();
        }

        void StartBuffer()
        {
            settings.Save();
            HideBanner();
            statusMsg = "Starte …";
            starting = true;
            UpdateStatus();
            btnToggle.Enabled = false;
            var snapshot = settings.Clone();
            ThreadPool.QueueUserWorkItem(_ => recorder.Start(snapshot));
        }

        void StopBuffer()
        {
            btnToggle.Enabled = false;
            ThreadPool.QueueUserWorkItem(_ => recorder.Stop());
        }

        void RestartBuffer()
        {
            btnToggle.Enabled = false;
            starting = true;
            UpdateStatus();
            var snapshot = settings.Clone();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                recorder.Stop();
                recorder.Start(snapshot);
            });
        }

        void OnRecorderState(bool on, string msg)
        {
            btnToggle.Enabled = true;
            starting = msg != null && msg.Contains("starte neu");
            statusMsg = on ? "" : (msg ?? "");
            if (!on && !string.IsNullOrEmpty(msg) && msg != "Replay-Buffer gestoppt" && !msg.Contains("starte neu"))
            {
                ShowBanner(msg.Replace("\n", "  ·  "), "Erneut versuchen", StartBuffer);
                if (settings.NotifyBalloon) { updateBalloon = false; tray.ShowBalloonTip(4000, "ReplayClip", msg, ToolTipIcon.Warning); }
                SoundFx.PlayError(settings);
            }
            if (on) Toast.Show(this, "Replay-Buffer läuft", "Speichern mit " + HotkeyBox.Format(settings.SaveHotkeyMods, settings.SaveHotkeyKey) +
                (settings.ResetBufferAfterSave ? " – danach beginnt der Puffer neu." : "."), "record", Theme.Rec);
            UpdateStatus();
        }

        Rectangle CaptureScreen()
        {
            try
            {
                foreach (var m in Native.GetMonitors())
                    if (m.Index == settings.MonitorIndex)
                        foreach (var s in Screen.AllScreens)
                            if (string.Equals(s.DeviceName, m.DeviceName, StringComparison.OrdinalIgnoreCase)) return s.Bounds;
            }
            catch { }
            return Screen.PrimaryScreen.Bounds;
        }

        void OnClipSaved(string file)
        {
            lastClip = file;
            statusMsg = "";
            string game = Path.GetFileName(Path.GetDirectoryName(file));
            var meta = Library.Find(file);
            if (meta != null && meta.App.Length > 0) game = meta.App;
            string dur = ClipBrowser.Dur(recorder.LastClipSeconds);
            stRight.Text = "Gespeichert: " + Path.GetFileName(file);
            SoundFx.PlaySaved(settings);
            header.Flash();
            if (settings.OverlayToast && !(ContainsFocus && Visible))
                OverlayToast.ShowMessage("Clip gespeichert", game + (dur.Length > 0 ? "  ·  " + dur : ""), Theme.Accent, CaptureScreen(), settings.OverlayPos);
            if (settings.NotifyBalloon) { updateBalloon = false; tray.ShowBalloonTip(2500, "Clip gespeichert", game + " › " + Path.GetFileName(file), ToolTipIcon.Info); }

            // In-App-Hinweis mit Vorschaubild (sobald es da ist)
            bool shown = false;
            Action<Image> show = img =>
            {
                if (shown) return;
                shown = true;
                Toast.Show(this, "Clip gespeichert", game + (dur.Length > 0 ? "  ·  " + dur : "") + "\nKlicken zum Anzeigen", "check", Theme.Accent, img,
                    () => { ShowFromTray(); filterKind = 1; RebuildNav(); txtSearch.Text = ""; ApplyFilter(); browser.SelectPath(file); });
            };
            Thumbnailer.Request(file, (v, t) =>
            {
                Image img = null;
                try { using (var ms = new MemoryStream(File.ReadAllBytes(t))) img = new Bitmap(Image.FromStream(ms)); } catch { }
                OnUi(() => show(img));
            });
            var fallback = new System.Windows.Forms.Timer { Interval = 2500 };
            fallback.Tick += (s, e) => { fallback.Dispose(); show(null); };
            fallback.Start();

            selectAfterScan = file;
            Rescan();
            UpdateStatus();
        }

        void OnSaveFailed(string msg)
        {
            statusMsg = "";
            stRight.Text = "Speichern fehlgeschlagen";
            SoundFx.PlayError(settings);
            if (settings.OverlayToast && !(ContainsFocus && Visible))
                OverlayToast.ShowMessage("Clip nicht gespeichert", msg, Theme.Danger, CaptureScreen(), settings.OverlayPos);
            Toast.Show(this, "Clip nicht gespeichert", msg, "error", Theme.Danger);
            if (settings.NotifyBalloon) { updateBalloon = false; tray.ShowBalloonTip(4000, "Clip nicht gespeichert", msg, ToolTipIcon.Error); }
            UpdateStatus();
        }

        void UpdateMeters()
        {
            if (!Visible || WindowState == FormWindowState.Minimized) return;
            var st = recorder.IsActive ? recorder.GetAudioStatus() : null;
            var levels = new List<KeyValuePair<string, float>>();
            if (st != null) foreach (var t in st) levels.Add(new KeyValuePair<string, float>(t.Name, t.Level));
            else foreach (var t in settings.Tracks) levels.Add(new KeyValuePair<string, float>(t.Name, 0));
            header.SetLevels(levels);
        }

        void UpdateStatus()
        {
            bool on = recorder.IsActive;
            Icon want = on ? iconOn : iconOff;
            if (tray.Icon != want) { tray.Icon = want; Icon = want; }
            trayToggle.Text = on ? "Replay-Buffer stoppen" : "Replay-Buffer starten";
            btnToggle.Text = on ? "Buffer stoppen" : (starting ? "Starte …" : "Buffer starten");
            btnToggle.IconName = on ? "stop" : "record";
            btnToggle.Kind = on ? FxKind.Normal : FxKind.Primary;
            btnSaveNow.Enabled = on && !recorder.IsSaving;
            string hk = HotkeyBox.Format(settings.SaveHotkeyMods, settings.SaveHotkeyKey);
            btnSaveNow.Hint = settings.SaveHotkeyKey != 0 ? hk : null;

            if (recorder.IsSaving)
            {
                header.State = HeaderBar.RecState.Saving;
                header.StatusLine = "Speichere Clip …";
            }
            else if (on)
            {
                starting = false;
                header.State = HeaderBar.RecState.Recording;
                var span = DateTime.Now - recorder.ActiveSince;
                double max = settings.BufferMinutes * 60.0;
                double filled = Math.Max(0, Math.Min(span.TotalSeconds, max));
                header.StatusLine = "Aufnahme läuft";
                header.SubLine = string.Format("Puffer {0}:{1:00} / {2}:00  ·  {3} speichert", (int)filled / 60, (int)filled % 60, settings.BufferMinutes, hk);
                header.BufferFill = (float)(filled / max);
            }
            else if (starting)
            {
                header.State = HeaderBar.RecState.Starting;
                header.StatusLine = "Starte …";
                header.SubLine = "Bildschirm und Ton werden vorbereitet";
                header.BufferFill = 0;
            }
            else
            {
                header.State = HeaderBar.RecState.Off;
                header.StatusLine = "Bereit";
                header.SubLine = statusMsg.Length > 0 && statusMsg.Length < 90 ? statusMsg : "Replay-Buffer ist aus";
                header.BufferFill = 0;
            }
            header.Invalidate();
            string tt = on ? "ReplayClip " + Program.Version + " – aktiv (" + hk + ")" : "ReplayClip " + Program.Version + " – aus";
            tray.Text = tt.Length > 63 ? tt.Substring(0, 63) : tt;

            if (on)
            {
                string err = recorder.AudioError;
                if (!string.IsNullOrEmpty(err)) stRight.Text = "⚠ " + err;
            }
        }
    }

    /// <summary>Kleiner Eingabedialog (dunkel).</summary>
    public static class InputBox
    {
        public static string Show(IWin32Window owner, string title, string prompt, string value)
        {
            using (var f = new Form())
            {
                f.Text = title;
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.StartPosition = FormStartPosition.CenterParent;
                f.MinimizeBox = f.MaximizeBox = false;
                f.ShowInTaskbar = false;
                f.Font = Theme.F(9.5f);
                f.AutoScaleMode = AutoScaleMode.None;
                f.ClientSize = new Size(Theme.S(440), Theme.S(140));
                f.BackColor = Theme.Panel;
                Theme.DarkTitleBar(f);
                var l = new Label { Text = prompt, Location = new Point(Theme.S(18), Theme.S(16)), AutoSize = true, ForeColor = Theme.TextDim };
                var t = new TextBox { Text = value };
                var field = Ui.Field(t, Theme.S(404), null, "edit");
                field.Location = new Point(Theme.S(18), Theme.S(42));
                var ok = new FxButton { Text = "OK", Kind = FxKind.Primary, DialogResult = DialogResult.OK, Size = new Size(Theme.S(96), Theme.S(34)), Location = new Point(Theme.S(222), Theme.S(92)) };
                var cancel = new FxButton { Text = "Abbrechen", DialogResult = DialogResult.Cancel, Size = new Size(Theme.S(100), Theme.S(34)), Location = new Point(Theme.S(322), Theme.S(92)) };
                f.Controls.AddRange(new Control[] { l, field, ok, cancel });
                f.AcceptButton = ok;
                f.CancelButton = cancel;
                f.Shown += (s, e) => { t.Focus(); t.SelectAll(); };
                return f.ShowDialog(owner) == DialogResult.OK ? t.Text : null;
            }
        }
    }
}
