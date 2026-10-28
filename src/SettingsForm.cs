using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Net;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ReplayClip
{
    /// <summary>Was das Einstellungsfenster vom Hauptfenster braucht.</summary>
    public interface ISettingsHost
    {
        Recorder Recorder { get; }
        void RestartNeeded();
        void RegisterHotkeys();
        void UnregisterHotkeys();
        string HotkeyWarning { get; }
        void LibraryChanged();
        void FfmpegReady();
        void AppearanceChanged();
        void RestartApp();
        void OfferUpdate(UpdateInfo u, bool fromSettings);
        UpdateInfo PendingUpdate { get; }
        void ShowIntro();
    }

    public static class Shell
    {
        public static void OpenFile(string path)
        {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
        }

        public static void OpenFolder(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + dir + "\"") { UseShellExecute = true });
            }
            catch { }
        }

        public static void SelectInExplorer(string file)
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + file + "\"") { UseShellExecute = true }); } catch { }
        }
    }

    /// <summary>Seitentitel mit Beschreibung und einlaufender Akzent-Linie.</summary>
    sealed class PageTitle : Control, IFxControl
    {
        string title = "", sub = "";
        readonly Tween line;

        public PageTitle()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            line = new Tween(this, 0, 10);
            Height = Theme.S(70);
        }

        public void Set(string t, string s)
        {
            title = t; sub = s;
            line.Set(0);
            line.To(1);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.EffectiveBack(Parent));
            float v = line.Value;
            int dx = (int)((1 - Anim.EaseOut(v)) * Theme.S(14));
            TextRenderer.DrawText(g, title, Theme.Semi(17f), new Rectangle(Theme.S(2) + dx, 0, Width, Theme.S(36)), Gfx.Lerp(Theme.Bg, Theme.Text, 0.3f + 0.7f * v), Gfx.Left);
            TextRenderer.DrawText(g, sub, Theme.F(9f), new Rectangle(Theme.S(2) + dx / 2, Theme.S(36), Width, Theme.S(20)), Theme.TextDim, Gfx.Left);
            var r = new RectangleF(Theme.S(2), Theme.S(62), Theme.S(60) * Anim.EaseOut(v), Theme.S(3));
            using (var b = Gfx.Gradient(new RectangleF(r.X, r.Y, Theme.S(60), r.Height), Theme.Accent, Theme.Secondary, 0)) Gfx.FillRound(g, b, r, r.Height / 2);
        }
    }

    /// <summary>Einstellungen in Kategorien (links Navigation, rechts Inhalt). Änderungen gelten sofort.</summary>
    public sealed class SettingsForm : Form
    {
        const string FfmpegZipUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";
        readonly Settings settings;
        readonly ISettingsHost host;
        bool loading = true;
        static int Px(float v) { return Theme.S(v); }

        NavList nav;
        Panel content;
        PageTitle pageTitle;
        Label lblStatus;
        readonly List<string[]> pageInfo = new List<string[]>();
        readonly List<Panel> pages = new List<Panel>();
        Panel current, pageAudio, hotkeyPage;

        // Aufnahme
        FxCombo cmbMonitor, cmbFps, cmbCodec, cmbBitrate, cmbEncoder, cmbController;
        Label lblEncoder;
        FxNumber numMinutes;
        FxToggle chkMouse, chkReset;
        Label lblEstimate;
        // Tastenkürzel
        HotkeyBox hkSave, hkToggle, hkShot;
        Label lblHotkeyWarn;
        // Audio
        ListBox lstTracks;
        TextBox txtTrackName;
        FxCombo cmbTrackType, cmbDevice;
        ListBox lstApps;
        FxSlider trkVolume;
        FxToggle chkDenoise;
        Label lblVolume, lblDevice, lblApps;
        Panel appsPanel, edScroll;
        Card grpEditor;
        FxNumber numOffset;
        FxCombo cmbAudioBitrate;
        List<AudioDeviceInfo> captureDevices;
        MeterPanel meter;
        Panel meterScroll;
        FxButton btnTest;
        Label lblTestInfo;
        AudioEngine testEngine;
        DateTime testRestartAt = DateTime.MaxValue;
        System.Windows.Forms.Timer meterTimer;
        bool editorLoading;
        // Bibliothek
        TextBox txtSaveDir;
        FxCombo cmbStructure, cmbAfterExport;
        ListBox lstQuick;
        TextBox txtQuick;
        // Aussehen
        SwatchPicker swAccent, swSecondary;
        FxCombo cmbFont;
        FxRadio rbAnimFull, rbAnimReduced, rbAnimOff;
        Label lblFontHint;
        FxButton btnRestart;
        // Sound
        FxCombo cmbSound, cmbOverlayPos;
        TextBox txtSoundFile;
        Control soundFileRow;
        FxSlider trkSoundVol;
        Label lblSoundVol;
        FxToggle chkErrorSound, chkHotkeySound, chkOverlay, chkBalloon;
        // Speicher & FFmpeg
        TextBox txtBufferDir, txtFfmpeg;
        FxProgress dlProgress;
        Label lblFfmpegState;
        FxButton btnDownload;
        // Allgemein
        FxToggle chkAutostart, chkAutoBuffer, chkMinimized, chkUpdate;
        TextBox txtRepo;
        Label lblUpdate;

        public SettingsForm(Settings s, ISettingsHost h, string startPage)
        {
            settings = s;
            host = h;
            SuspendLayout();
            AutoScaleMode = AutoScaleMode.None;
            Font = Theme.F(9f);
            Text = "Einstellungen – ReplayClip " + Program.Version;
            Size = new Size(Px(1080), Px(800));
            MinimumSize = new Size(Px(860), Px(620));
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            DoubleBuffered = true;
            Theme.DarkTitleBar(this);

            nav = new NavList { Dock = DockStyle.Fill, ItemHeight = 38 };
            nav.SelectedIndexChanged += (o, e) => { var it = nav.SelectedItem; if (it != null && it.Value != null) ShowPage(int.Parse(it.Value)); };
            // Suche über alle Einstellungen (Seitennamen und alle Texte auf den Seiten)
            var navPanel = new Panel { Dock = DockStyle.Left, Width = Px(240), BackColor = Theme.Panel, Tag = "keep" };
            txtFind = new TextBox();
            var find = Ui.Field(txtFind, Px(208), "Einstellung suchen …", "search");
            var findWrap = new Panel { Dock = DockStyle.Top, Height = Px(54), Padding = new Padding(Px(14), Px(12), Px(14), 0), BackColor = Theme.Panel, Tag = "keep" };
            find.Dock = DockStyle.Fill;
            findWrap.Controls.Add(find);
            navPanel.Controls.Add(nav);
            navPanel.Controls.Add(findWrap);
            txtFind.TextChanged += (o, e) => BuildNav(txtFind.Text);
            txtFind.KeyDown += (o, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; foreach (var it in nav.Items) if (!it.Header) { nav.SelectedIndex = nav.Items.IndexOf(it); break; } } };
            var navLine = new Panel { Dock = DockStyle.Left, Width = 1, BackColor = Theme.Border, Tag = "keep" };

            var right = new Panel { Dock = DockStyle.Fill, Padding = new Padding(Px(28), Px(18), Px(22), Px(10)), BackColor = Theme.Bg };
            pageTitle = new PageTitle { Dock = DockStyle.Top };
            content = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            var bottom = new Panel { Dock = DockStyle.Bottom, Height = Px(50), Padding = new Padding(0, Px(10), 0, 0) };
            var close = new FxButton { Text = "Fertig", Kind = FxKind.Primary, Dock = DockStyle.Right, Width = Px(120), IconName = "check" };
            close.Click += (o, e) => Close();
            lblStatus = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.TextDim, Font = Theme.F(8.5f), Text = "Änderungen werden sofort gespeichert.", Tag = "keep" };
            bottom.Controls.Add(lblStatus);
            bottom.Controls.Add(close);
            right.Controls.Add(content);
            right.Controls.Add(bottom);
            right.Controls.Add(pageTitle);
            Controls.Add(right);
            Controls.Add(navLine);
            Controls.Add(navPanel);

            // Reihenfolge = Gruppen in der Navigation (siehe BuildNav)
            AddPage("Video & Rewind", "video", "Monitor, Bildrate, Encoder, Qualität und Rewind-Puffer", BuildRecordPage());
            AddPage("Dauer-Aufnahme", "record", "Dauerhafte Aufnahmen – z.B. für Turniere", BuildRecordModePage());
            AddPage("Screenshots", "camera", "Speicherort, Ordner und Format", BuildShotPage());
            pageAudio = AddPage("Audiospuren", "tracks", "Welcher Ton auf welcher Spur landet – mit Live-Pegel zum Testen", BuildAudioPage());
            AddPage("Tastenkürzel", "keyboard", "Globale Tasten und Controller – funktionieren auch im Spiel", BuildHotkeyPage());
            AddPage("Bibliothek", "library", "Speicherort, Ordnerstruktur, Vorschau, Discord und Schnitte", BuildLibraryPage());
            AddPage("Tags", "tag", "Schnell-Tags auf den Tasten 1–9", BuildQuickTagPage());
            AddPage("Aussehen", "color", "Farben, Schrift und Animationen", BuildLookPage());
            AddPage("Sound & Hinweise", "bell", "Sounds für Clips, Screenshots und Aufnahmen – plus Einblendungen", BuildSoundPage());
            AddPage("Speicher & FFmpeg", "storage", "Puffer-Ordner und Encoder", BuildSystemPage());
            AddPage("Allgemein", "general", "Start-Verhalten", BuildGeneralPage());
            AddPage("Updates", "refresh", "Neue Versionen suchen und installieren", BuildUpdatesPage());
            AddPage("Über", "info", "Version und Diagnose", BuildAboutPage());


            LoadSettingsIntoUi();
            loading = false;
            Theme.Apply(this);
            ResumeLayout(false);
            PerformLayout();

            // Suchtext je Seite: Titel, Untertitel und alle Beschriftungen
            for (int i = 0; i < pages.Count; i++)
            {
                var sb = new System.Text.StringBuilder(pageInfo[i][0] + " " + pageInfo[i][2] + " ");
                CollectText(pages[i], sb);
                pageText.Add(sb.ToString().ToLowerInvariant());
            }
            BuildNav("");
            Shown += (o, e) => ShowPageByName(startPage ?? pageInfo[0][0]);
        }

        TextBox txtFind;
        readonly List<string> pageText = new List<string>();
        static readonly string[][] Groups =
        {
            new[] { "Aufnahme", "Video & Rewind", "Dauer-Aufnahme", "Screenshots", "Audiospuren", "Tastenkürzel" },
            new[] { "Bibliothek", "Bibliothek", "Tags" },
            new[] { "Programm", "Aussehen", "Sound & Hinweise", "Speicher & FFmpeg", "Allgemein", "Updates", "Über" },
        };

        static void CollectText(Control c, System.Text.StringBuilder sb)
        {
            if (!(c is TextBox) && !string.IsNullOrEmpty(c.Text)) sb.Append(c.Text).Append(' ');
            var cb = c as FxCombo;
            if (cb != null) foreach (var o in cb.Items) sb.Append(o).Append(' ');
            foreach (Control k in c.Controls) CollectText(k, sb);
        }

        /// <summary>Navigation in Gruppen aufbauen – mit Suchtext nur passende Seiten.</summary>
        void BuildNav(string query)
        {
            string q = (query ?? "").Trim().ToLowerInvariant();
            var items = new List<NavEntry>();
            foreach (var grp in Groups)
            {
                bool head = false;
                for (int k = 1; k < grp.Length; k++)
                {
                    int i = pageInfo.FindIndex(p => p[0] == grp[k]);
                    if (i < 0) continue;
                    if (q.Length > 0 && pageText[i].IndexOf(q, StringComparison.Ordinal) < 0) continue;
                    if (!head) { items.Add(new NavEntry { Header = true, Text = grp[0] }); head = true; }
                    items.Add(new NavEntry { Text = pageInfo[i][0], Icon = pageInfo[i][1], Value = i.ToString() });
                }
            }
            if (items.Count == 0) items.Add(new NavEntry { Header = true, Text = "Nichts gefunden" });
            int sel = -1;
            if (current != null)
                for (int j = 0; j < items.Count; j++) if (items[j].Value != null && pages[int.Parse(items[j].Value)] == current) sel = j;
            nav.SetItems(items, sel);
        }

        void ShowPageByName(string name)
        {
            if (txtFind != null && txtFind.Text.Length > 0) txtFind.Text = "";
            for (int j = 0; j < nav.Items.Count; j++)
                if (nav.Items[j].Value != null && pageInfo[int.Parse(nav.Items[j].Value)][0] == name) { nav.SelectedIndex = j; return; }
            ShowPage(0);
        }

        Panel AddPage(string name, string icon, string sub, Panel p)
        {
            p.Dock = DockStyle.Fill;
            p.Visible = false;
            content.Controls.Add(p);
            pages.Add(p);
            pageInfo.Add(new[] { name, icon, sub });
            return p;
        }

        void ShowPage(int i)
        {
            if (i < 0 || i >= pages.Count) return;
            pageTitle.Set(pageInfo[i][0], pageInfo[i][2]);
            current = pages[i];
            current.Visible = true;
            current.BringToFront();
            foreach (var p in pages) if (p != current) p.Visible = false;
            if (current == pageAudio && captureDevices == null) LoadDevices();
            if (current != pageAudio) StopTest();
            if (current == hotkeyPage) lblHotkeyWarn.Text = host.HotkeyWarning ?? "";
        }

        static Panel ScrollPage()
        {
            return new Panel { AutoScroll = true, BackColor = Theme.Bg, Padding = new Padding(0, Px(6), Px(8), Px(10)) };
        }

        /// <summary>Karten von oben nach unten auf eine Scroll-Seite legen.</summary>
        static void Stack(Panel page, params Control[] groups)
        {
            for (int i = groups.Length - 1; i >= 0; i--)
            {
                groups[i].Dock = DockStyle.Top;
                page.Controls.Add(groups[i]);
                if (i > 0) page.Controls.Add(new Panel { Dock = DockStyle.Top, Height = Px(14), BackColor = Theme.Bg });
            }
        }

        void MarkRestartNeeded()
        {
            if (loading) return;
            settings.Save();
            if (testEngine != null) testRestartAt = DateTime.UtcNow.AddMilliseconds(700);
            host.RestartNeeded();
        }

        static Label Dim(string text, int maxW) { return Ui.Hint(text, Px(maxW)); }

        // ======================= Seiten =======================

        Panel BuildRecordPage()
        {
            var page = ScrollPage();
            var video = Ui.Grid();
            cmbMonitor = Ui.Combo(Px(340));
            Ui.Row(video, "Monitor", Ui.Flow(cmbMonitor, Ui.IconButton("refresh", "Monitore neu einlesen", (s, e) => LoadMonitors())));
            cmbFps = Ui.Combo(Px(140));
            foreach (var f in new[] { 30, 60, 120, 144 }) cmbFps.Items.Add(f + " FPS");
            Ui.Row(video, "Bildrate", cmbFps);
            cmbEncoder = Ui.Combo(Px(340));
            foreach (var n in Encoders.PrefNames) cmbEncoder.Items.Add(n);
            lblEncoder = new Label { AutoSize = true, ForeColor = Theme.TextDim, Margin = new Padding(Px(10), Px(8), 0, 0), Tag = "keep" };
            Ui.Row(video, "Encoder", Ui.Flow(cmbEncoder, lblEncoder));
            cmbCodec = Ui.Combo(Px(340));
            cmbCodec.Items.Add("H.264 – überall abspielbar");
            cmbCodec.Items.Add("HEVC / H.265 – kleinere Dateien");
            Ui.Row(video, "Codec", cmbCodec);
            cmbBitrate = Ui.Combo(Px(170));
            foreach (var b in new[] { 10, 15, 20, 30, 40, 50, 80 }) cmbBitrate.Items.Add(b + " Mbit/s");
            Ui.Row(video, "Bitrate", cmbBitrate);
            chkMouse = new FxToggle { Text = "Mauszeiger aufnehmen" };
            Ui.Row(video, "", chkMouse);

            var buf = Ui.Grid();
            numMinutes = new FxNumber { Minimum = 1, Maximum = 20, Width = Px(130) };
            Ui.Row(buf, "Pufferlänge", Ui.Flow(numMinutes, new Label { Text = "Minuten", AutoSize = true, Margin = new Padding(Px(8), Px(8), 0, 0), ForeColor = Theme.TextDim }));
            var chkPause = new FxToggle { Text = "Rewind pausieren (sonst läuft der Puffer im Rewind-Modus immer)", Checked = settings.RewindPaused, MaximumSize = new Size(Px(560), 0) };
            Ui.Row(buf, "", chkPause);
            chkPause.CheckedChanged += (s, e) => { settings.RewindPaused = chkPause.Checked; settings.Save(); };
            chkReset = new FxToggle { Text = "Puffer nach dem Speichern leeren", MaximumSize = new Size(Px(560), 0) };
            Ui.Row(buf, "", chkReset);
            Ui.Row(buf, "", Dim("An: Wenn du mehrere Clips hintereinander speicherst, enthält jeder nur die Zeit seit dem letzten Clip – keine doppelten Szenen. Aus: Jeder Clip enthält immer die vollen letzten Minuten.", 560));
            lblEstimate = Dim("", 560);
            Ui.Row(buf, null, lblEstimate);

            Stack(page, Ui.Group("Video (gilt für Rewind und Aufnahme)", video), Ui.Group("Rewind (Replay-Puffer)", buf));

            cmbMonitor.SelectedIndexChanged += (s, e) =>
            {
                var m = cmbMonitor.SelectedItem as MonitorInfo;
                if (m != null && !loading) { settings.MonitorIndex = m.Index; MarkRestartNeeded(); }
            };
            cmbFps.SelectedIndexChanged += (s, e) => { if (!loading) { settings.Fps = new[] { 30, 60, 120, 144 }[cmbFps.SelectedIndex]; UpdateEstimate(); MarkRestartNeeded(); } };
            cmbCodec.SelectedIndexChanged += (s, e) => { if (!loading) { settings.Codec = cmbCodec.SelectedIndex == 1 ? "hevc" : "h264"; MarkRestartNeeded(); DetectEncoder(); } };
            cmbEncoder.SelectedIndexChanged += (s, e) => { if (!loading) { settings.Encoder = Encoders.Prefs[Math.Max(0, cmbEncoder.SelectedIndex)]; MarkRestartNeeded(); DetectEncoder(); } };
            cmbBitrate.SelectedIndexChanged += (s, e) => { if (!loading) { settings.BitrateMbps = new[] { 10, 15, 20, 30, 40, 50, 80 }[cmbBitrate.SelectedIndex]; UpdateEstimate(); MarkRestartNeeded(); } };
            numMinutes.ValueChanged += (s, e) => { if (!loading) { settings.BufferMinutes = (int)numMinutes.Value; settings.Save(); UpdateEstimate(); } };
            chkMouse.CheckedChanged += (s, e) => { if (!loading) { settings.DrawMouse = chkMouse.Checked; MarkRestartNeeded(); } };
            chkReset.CheckedChanged += (s, e) => { if (!loading) { settings.ResetBufferAfterSave = chkReset.Checked; settings.Save(); } };
            return page;
        }

        Panel BuildHotkeyPage()
        {
            var page = ScrollPage();
            hotkeyPage = page;
            var keys = Ui.Grid();
            hkSave = new HotkeyBox();
            hkToggle = new HotkeyBox();
            hkShot = new HotkeyBox();
            Ui.Row(keys, "Clip speichern", Ui.Flow(Ui.Field(hkSave, Px(230), null, "save"), Ui.IconButton("delete", "Kürzel entfernen (Esc)", (s2, e2) => { hkSave.ClearHotkey(); host.RegisterHotkeys(); lblHotkeyWarn.Text = host.HotkeyWarning ?? ""; })));
            Ui.Row(keys, "Aufnahme an/aus", Ui.Flow(Ui.Field(hkToggle, Px(230), null, "record"), Ui.IconButton("delete", "Kürzel entfernen (Esc)", (s2, e2) => { hkToggle.ClearHotkey(); host.RegisterHotkeys(); lblHotkeyWarn.Text = host.HotkeyWarning ?? ""; })));
            Ui.Row(keys, "Screenshot", Ui.Flow(Ui.Field(hkShot, Px(230), null, "camera"), Ui.IconButton("delete", "Kürzel entfernen (Esc)", (s2, e2) => { hkShot.ClearHotkey(); host.RegisterHotkeys(); lblHotkeyWarn.Text = host.HotkeyWarning ?? ""; })));
            hkShot.Enter += (s, e) => host.UnregisterHotkeys();
            hkShot.Leave += (s, e) => { host.RegisterHotkeys(); lblHotkeyWarn.Text = host.HotkeyWarning ?? ""; };
            hkShot.HotkeyChanged += (s, e) => { settings.ShotHotkeyMods = hkShot.Mods; settings.ShotHotkeyKey = hkShot.Key; settings.Save(); };
            cmbController = Ui.Combo(Px(260));
            foreach (var n in Gamepad.Names) cmbController.Items.Add(n);
            Ui.Row(keys, "Controller", cmbController);
            cmbController.SelectedIndexChanged += (s, e) =>
            {
                if (loading) return;
                settings.ControllerCombo = cmbController.SelectedIndex;
                settings.Save();
                Gamepad.SetCombo(settings.ControllerCombo);
            };
            Ui.Row(keys, null, Dim("Ins Feld klicken und die gewünschte Kombination drücken. Esc oder Entf (oder ✕) = kein Tastenkürzel.", 560));
            lblHotkeyWarn = new Label { AutoSize = true, ForeColor = Theme.Danger, MaximumSize = new Size(Px(560), 0), Tag = "keep" };
            Ui.Row(keys, null, lblHotkeyWarn);

            var lib = Ui.Grid();
            string[,] rows =
            {
                { "1 – 9", "Schnell-Tag 1–9 an/aus" }, { "F", "Favorit an/aus" }, { "Enter / Doppelklick", "Clip bearbeiten / schneiden" },
                { "Entf", "In den Papierkorb" }, { "Strg + C", "Clip kopieren (z.B. in Discord einfügen)" }, { "F2", "Umbenennen" },
                { "Strg + F", "Suchen" }, { "F5", "Bibliothek neu einlesen" }
            };
            for (int i = 0; i < rows.GetLength(0); i++) Ui.Row(lib, null, KeyRow(rows[i, 0], rows[i, 1]));
            // Editor-Tasten: alle anpassbar (Feld anklicken, Taste drücken; ✕ = keine Taste)
            var ed = Ui.Grid();
            foreach (var act in Settings.EditorActions)
            {
                string id = act[0];
                var box = new HotkeyBox { CaptureAll = true };
                box.SetKeys(settings.EditorKey(id));
                box.HotkeyChanged += (s2, e2) => { settings.EditorKeys[id] = (int)box.AsKeys; settings.Save(); };
                Ui.Row(ed, act[1], Ui.Flow(Ui.Field(box, Px(200), null, "keyboard"),
                    Ui.IconButton("delete", "Keine Taste", (s2, e2) => box.ClearHotkey())));
            }
            Ui.Row(ed, null, Dim("Fest: ← → = 1 s (Umschalt: 5 s), Komma/Punkt = 1 Bild, Pos1/Ende = Anfang/Ende der Auswahl, Mausrad = Zoom. Änderungen gelten ab dem nächsten Öffnen des Editors.", 600));

            Stack(page, Ui.Group("Global (funktionieren auch im Spiel)", keys), Ui.Group("In der Bibliothek", lib), Ui.Group("Im Editor", ed));
            hkSave.Enter += (s, e) => host.UnregisterHotkeys();
            hkToggle.Enter += (s, e) => host.UnregisterHotkeys();
            hkSave.Leave += (s, e) => { host.RegisterHotkeys(); lblHotkeyWarn.Text = host.HotkeyWarning ?? ""; };
            hkToggle.Leave += (s, e) => { host.RegisterHotkeys(); lblHotkeyWarn.Text = host.HotkeyWarning ?? ""; };
            hkSave.HotkeyChanged += (s, e) => { settings.SaveHotkeyMods = hkSave.Mods; settings.SaveHotkeyKey = hkSave.Key; settings.Save(); };
            hkToggle.HotkeyChanged += (s, e) => { settings.ToggleHotkeyMods = hkToggle.Mods; settings.ToggleHotkeyKey = hkToggle.Key; settings.Save(); };
            return page;
        }

        static Control KeyRow(string keys, string what)
        {
            var key = new KeyCap { Text = keys, Margin = new Padding(0, Px(2), Px(14), Px(2)) };
            var l = new Label { Text = what, AutoSize = true, Margin = new Padding(0, Px(6), 0, 0), Tag = "keep", ForeColor = Theme.Text, Font = Theme.F(9f) };
            return Ui.Flow(key, l);
        }

        /// <summary>Taste als kleine Kappe gezeichnet.</summary>
        sealed class KeyCap : Control, IFxControl
        {
            public KeyCap()
            {
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
                Font = Theme.Medium(8.5f);
                Size = new Size(Px(150), Px(28));
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Theme.EffectiveBack(Parent));
                var ts = Gfx.Measure(Text, Font);
                var r = new RectangleF(0.5f, 0.5f, Math.Min(Width - 1.5f, ts.Width + Px(18)), Height - 3);
                Gfx.FillRound(g, Theme.Border, new RectangleF(r.X, r.Y + 2, r.Width, r.Height), Px(5));
                Gfx.FillRound(g, Theme.Panel2, r, Px(5));
                Gfx.DrawRound(g, Theme.Border, r, Px(5), 1);
                TextRenderer.DrawText(g, Text, Font, Rectangle.Round(r), Theme.Text, Gfx.Center);
            }
        }

        Panel BuildAudioPage()
        {
            var page = new Panel { BackColor = Theme.Bg };
            pageAudio = page;
            BuildAudioTab();
            return page;
        }

        /// <summary>Ordnerwahl + Strukturauswahl für Aufnahmen/Screenshots.</summary>
        void DirRow(TableLayoutPanel g, string label, Func<string> get, Action<string> set)
        {
            var box = new TextBox { ReadOnly = true, Text = get() };
            Ui.Row(g, label, Ui.Flow(Ui.Field(box, Px(400), null, "folder"),
                Ui.Button("Ändern …", (s, e) =>
                {
                    using (var d = new FolderBrowserDialog { SelectedPath = box.Text, ShowNewFolderButton = true })
                        if (d.ShowDialog(this) == DialogResult.OK) { box.Text = d.SelectedPath; set(d.SelectedPath); settings.Save(); host.LibraryChanged(); }
                }),
                Ui.IconButton("open", "Ordner öffnen", (s, e) => { try { Directory.CreateDirectory(get()); } catch { } Shell.OpenFolder(get()); })));
        }

        FxCombo StructureCombo(int value, Action<int> set)
        {
            var c = Ui.Combo(Px(320));
            foreach (var n in Library.StructureNames) c.Items.Add(n);
            c.SelectedIndex = Math.Max(0, Math.Min(Library.StructureNames.Length - 1, value));
            c.SelectedIndexChanged += (s, e) => { set(c.SelectedIndex); settings.Save(); };
            return c;
        }

        Panel BuildRecordModePage()
        {
            var page = ScrollPage();
            var g = Ui.Grid();
            DirRow(g, "Speichern in", () => settings.RecordDir, v => settings.RecordDir = v);
            Ui.Row(g, "Ordner", StructureCombo(settings.RecordStructure, v => settings.RecordStructure = v));
            var num = new FxNumber { Minimum = 0, Maximum = 500, Width = Px(110), Value = settings.RecordMinFreeGB };
            num.ValueChanged += (s, e) => { settings.RecordMinFreeGB = (int)num.Value; settings.Save(); };
            var free = new Label { AutoSize = true, ForeColor = Theme.TextDim, Margin = new Padding(Px(10), Px(8), 0, 0), Tag = "keep" };
            try { free.Text = "GB frei halten  ·  aktuell " + ClipBrowser.FmtSize(new DriveInfo(Path.GetPathRoot(Path.GetFullPath(settings.RecordDir))).AvailableFreeSpace) + " frei"; } catch { free.Text = "GB frei halten"; }
            Ui.Row(g, "Speicherplatz", Ui.Flow(num, free));
            Ui.Row(g, null, Dim("Die Aufnahme stoppt automatisch, bevor die Platte voll ist (0 = aus). Pro Stunde braucht eine Aufnahme ca. " +
                (settings.BitrateMbps * 3600 / 8 / 1000.0).ToString("0.0", CultureInfo.CurrentCulture) + " GB (bei " + settings.BitrateMbps + " Mbit/s, einstellbar unter Aufnahme).", 600));
            Ui.Row(g, null, Dim("Bild, Ton und Encoder kommen aus „Aufnahme“ und „Audiospuren“. Während einer Aufnahme ist Rewind aus.\nSicher gegen Abstürze: Es wird laufend in eine MKV geschrieben und beim Stoppen in eine MP4 umgepackt. Bricht die Aufnahme ab (z.B. Auflösungswechsel), geht es automatisch als neuer Teil weiter.", 600));
            Stack(page, Ui.Group("Dauerhafte Aufnahme", g));
            return page;
        }

        Panel BuildShotPage()
        {
            var page = ScrollPage();
            var g = Ui.Grid();
            DirRow(g, "Speichern in", () => settings.ShotDir, v => settings.ShotDir = v);
            Ui.Row(g, "Ordner", StructureCombo(settings.ShotStructure, v => settings.ShotStructure = v));
            var fmt = Ui.Combo(Px(220));
            fmt.Items.Add("PNG (verlustfrei)");
            fmt.Items.Add("JPG (kleiner)");
            fmt.SelectedIndex = settings.ShotFormat == "jpg" ? 1 : 0;
            var q = new FxSlider { Minimum = 50, Maximum = 100, Value = settings.ShotQuality, DefaultValue = 92, Width = Px(200) };
            var ql = new Label { AutoSize = true, ForeColor = Theme.TextDim, Margin = new Padding(Px(8), Px(6), 0, 0), Tag = "keep", Text = "JPG-Qualität " + settings.ShotQuality + " %" };
            Ui.Row(g, "Format", Ui.Flow(fmt, q, ql));
            fmt.SelectedIndexChanged += (s, e) => { settings.ShotFormat = fmt.SelectedIndex == 1 ? "jpg" : "png"; settings.Save(); };
            q.ValueChanged += (s, e) => { settings.ShotQuality = q.Value; ql.Text = "JPG-Qualität " + q.Value + " %"; settings.Save(); };
            var copy = new FxToggle { Text = "Screenshot auch in die Zwischenablage legen", Checked = settings.ShotCopy };
            copy.CheckedChanged += (s, e) => { settings.ShotCopy = copy.Checked; settings.Save(); };
            Ui.Row(g, "", copy);
            Ui.Row(g, null, Dim("Den Auslöser-Sound wählst du unter „Sound & Hinweise“.", 600));
            Ui.Row(g, null, Dim("Tastenkürzel unter „Tastenkürzel“ (Standard Alt+F12) – funktioniert in jedem Modus. Aufgenommen wird der Monitor aus „Aufnahme“. Doppelklick auf einen Screenshot öffnet den Bild-Editor (zuschneiden, Größe, Format).", 600));
            Stack(page, Ui.Group("Screenshots", g));
            return page;
        }

        Panel BuildLibraryPage()
        {
            var page = ScrollPage();
            var g = Ui.Grid();
            txtSaveDir = new TextBox { ReadOnly = true };
            Ui.Row(g, "Clips speichern in", Ui.Flow(Ui.Field(txtSaveDir, Px(400), null, "folder"),
                Ui.Button("Ändern …", (s, e) => PickFolder(txtSaveDir, true)),
                Ui.IconButton("open", "Ordner öffnen", (s, e) => Shell.OpenFolder(settings.SaveDir))));
            cmbStructure = Ui.Combo(Px(300));
            for (int i = 0; i < 3; i++) cmbStructure.Items.Add(Settings.FolderStructureLabel(i));
            Ui.Row(g, "Ordnerstruktur", cmbStructure);
            var ex = Dim("", 600);
            Ui.Row(g, null, ex);
            Action updEx = () =>
            {
                string f = Library.FolderFor(settings, "Counter-Strike 2", DateTime.Now);
                ex.Text = "Beispiel: " + Path.Combine(f, "Counter-Strike 2 " + DateTime.Now.ToString("yyyy.MM.dd - HH.mm.ss") + ".mp4") +
                          "\nGilt für neue Clips. Die Bibliothek findet Clips in allen Unterordnern.";
            };
            cmbStructure.SelectedIndexChanged += (s, e) => { if (!loading) { settings.FolderStructure = cmbStructure.SelectedIndex; settings.Save(); } updEx(); };
            // Speicherlimit für Clips
            var numLimit = new FxNumber { Minimum = 0, Maximum = 100000, Width = Px(120), Value = settings.ClipLimitGB };
            var lblLimit = new Label { AutoSize = true, ForeColor = Theme.TextDim, Margin = new Padding(Px(10), Px(8), 0, 0), Tag = "keep", Text = "GB  (0 = kein Limit)" };
            Ui.Row(g, "Speicherlimit", Ui.Flow(numLimit, lblLimit));
            var chkProtect = new FxToggle { Text = "Favoriten und Clips mit Tags nie automatisch löschen", Checked = settings.ClipLimitProtect };
            Ui.Row(g, "", chkProtect);
            Ui.Row(g, null, Dim("Wird das Limit überschritten, löscht ReplayClip die ältesten Clips (endgültig, nicht Papierkorb), bis wieder genug Platz ist. Gilt nur für Rewind-Clips im Speicherort oben.", 600));
            numLimit.ValueChanged += (s, e) => { settings.ClipLimitGB = (int)numLimit.Value; settings.Save(); };
            chkProtect.CheckedChanged += (s, e) => { settings.ClipLimitProtect = chkProtect.Checked; settings.Save(); };
            string sizeRoot = settings.SaveDir;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                long tot = 0;
                try { foreach (var it in Library.Scan(sizeRoot)) tot += it.Size; } catch { }
                OnUi(() => lblLimit.Text = "GB  (0 = kein Limit)  ·  aktuell " + ClipBrowser.FmtSize(tot) + " Clips");
            });

            // Weitere Ordner -> ein gemeinsamer Pool
            var lst = new ListBox { Width = Px(400), Height = Px(90) };
            Ui.DarkList(lst, Px(26));
            foreach (var d in settings.ExtraDirs) lst.Items.Add(d);
            var bAdd = Ui.Button("add", "Ordner hinzufügen …", (s, e) =>
            {
                using (var d = new FolderBrowserDialog { ShowNewFolderButton = false })
                    if (d.ShowDialog(this) == DialogResult.OK && !settings.ExtraDirs.Contains(d.SelectedPath))
                    { settings.ExtraDirs.Add(d.SelectedPath); lst.Items.Add(d.SelectedPath); settings.Save(); host.LibraryChanged(); }
            });
            var bDel = Ui.Button("remove", "Entfernen", (s, e) =>
            {
                if (lst.SelectedIndex < 0) return;
                settings.ExtraDirs.RemoveAt(lst.SelectedIndex); lst.Items.RemoveAt(lst.SelectedIndex); settings.Save(); host.LibraryChanged();
            });
            var extraCol = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            extraCol.Controls.Add(bAdd);
            extraCol.Controls.Add(bDel);
            Ui.Row(g, "Weitere Ordner", Ui.Flow(lst, extraCol));
            Ui.Row(g, null, Dim("Für Fortgeschrittene: Diese Ordner (z.B. eine zweite Platte oder alte Clips) werden zusätzlich durchsucht und zusammen mit dem Speicherort als ein großer Pool angezeigt – Videos bei Clips/Aufnahmen, Bilder bei Screenshots.", 600));
            var chkHover = new FxToggle { Text = "Vorschau beim Überfahren (Maus links/rechts bewegen = durch den Clip spulen)", Checked = settings.HoverPreview };
            Ui.Row(g, "Vorschau", chkHover);
            chkHover.CheckedChanged += (s, e) => { settings.HoverPreview = chkHover.Checked; settings.Save(); host.LibraryChanged(); };
            var cmbDiscord = Ui.Combo(Px(300));
            foreach (var n in DiscordExport.LimitNames) cmbDiscord.Items.Add(n);
            cmbDiscord.SelectedIndex = Math.Max(0, Array.IndexOf(DiscordExport.Limits, settings.DiscordLimitMB));
            Ui.Row(g, "„Für Discord“", cmbDiscord);
            Ui.Row(g, null, Dim("Rechnet den Clip so klein, dass er unter Discords Upload-Grenze passt, und legt ihn in die Zwischenablage (Strg+V in Discord).", 600));
            cmbDiscord.SelectedIndexChanged += (s, e) => { settings.DiscordLimitMB = DiscordExport.Limits[Math.Max(0, cmbDiscord.SelectedIndex)]; settings.Save(); };

            var exp = Ui.Grid();
            cmbAfterExport = Ui.Combo(Px(300));
            cmbAfterExport.Items.Add("Jedes Mal fragen");
            cmbAfterExport.Items.Add("Original behalten");
            cmbAfterExport.Items.Add("Original in den Papierkorb");
            Ui.Row(exp, "Nach dem Schneiden", cmbAfterExport);
            Ui.Row(exp, null, Dim("Geschnittene Clips werden neben dem Original als „… (Schnitt).mp4“ gespeichert, übernehmen dessen Tags und sind in der Bibliothek als SCHNITT markiert.", 600));
            cmbAfterExport.SelectedIndexChanged += (s, e) => { if (!loading) { settings.AfterExport = cmbAfterExport.SelectedIndex; settings.Save(); } };

            Stack(page, Ui.Group("Speicherort", g), Ui.Group("Schnitte", exp));
            Load += (s, e) => updEx();
            return page;
        }

        Panel BuildQuickTagPage()
        {
            var page = ScrollPage();
            var g = Ui.Grid();
            lstQuick = new ListBox { Width = Px(280), Height = Px(260), IntegralHeight = false };
            Ui.DarkList(lstQuick, 34);
            txtQuick = new TextBox();
            var btns = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(Px(12), 0, 0, 0) };
            btns.Controls.Add(Ui.Flow(Ui.Field(txtQuick, Px(200), "Neuer Tag …", "tag"), Ui.Button("add", "Hinzufügen", (s, e) => AddQuickTag())));
            btns.Controls.Add(Ui.Button("remove", "Entfernen", (s, e) => { int i = lstQuick.SelectedIndex; if (i >= 0) { settings.QuickTags.RemoveAt(i); QuickChanged(i); } }));
            btns.Controls.Add(Ui.Button("up", "Nach oben", (s, e) => MoveQuick(-1)));
            btns.Controls.Add(Ui.Button("down", "Nach unten", (s, e) => MoveQuick(1)));
            Ui.Row(g, null, Ui.Flow(lstQuick, btns));
            Ui.Row(g, null, Dim("Die ersten 9 Tags liegen in der Bibliothek auf den Tasten 1–9 und als Knöpfe im Detailbereich. Eigene Tags kannst du dort jederzeit zusätzlich frei eintippen.", 600));
            txtQuick.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { AddQuickTag(); e.SuppressKeyPress = true; } };
            Stack(page, Ui.Group("Schnell-Tags", g));
            return page;
        }

        void RefreshQuickList(int sel)
        {
            lstQuick.Items.Clear();
            for (int i = 0; i < settings.QuickTags.Count; i++)
                lstQuick.Items.Add((i < 9 ? (i + 1) + "    " : "      ") + settings.QuickTags[i]);
            if (lstQuick.Items.Count > 0) lstQuick.SelectedIndex = Math.Max(0, Math.Min(sel, lstQuick.Items.Count - 1));
        }

        void QuickChanged(int sel)
        {
            settings.Save();
            RefreshQuickList(sel);
            host.LibraryChanged();
        }

        void AddQuickTag()
        {
            string t = txtQuick.Text.Trim().Replace(";", "").Replace("|", "");
            if (t.Length == 0) return;
            foreach (var x in settings.QuickTags) if (string.Equals(x, t, StringComparison.CurrentCultureIgnoreCase)) return;
            settings.QuickTags.Add(t);
            txtQuick.Text = "";
            QuickChanged(settings.QuickTags.Count - 1);
        }

        void MoveQuick(int d)
        {
            int i = lstQuick.SelectedIndex, j = i + d;
            if (i < 0 || j < 0 || j >= settings.QuickTags.Count) return;
            var t = settings.QuickTags[i];
            settings.QuickTags[i] = settings.QuickTags[j];
            settings.QuickTags[j] = t;
            QuickChanged(j);
        }

        // ---------------- Aussehen ----------------

        Panel BuildLookPage()
        {
            var page = ScrollPage();
            var colors = Ui.Grid();
            swAccent = new SwatchPicker();
            swSecondary = new SwatchPicker();
            Ui.Row(colors, "Primärfarbe", swAccent);
            Ui.Row(colors, "Sekundärfarbe", swSecondary);
            Ui.Row(colors, null, Dim("Primärfarbe: Buttons, Auswahl, Pegel. Sekundärfarbe: Verläufe, Links und SCHNITT-Markierung. Das letzte Feld (+) öffnet eine freie Farbauswahl.", 600));
            Ui.Row(colors, null, Ui.Button("refresh", "Standardfarben", (s, e) =>
            {
                swAccent.Value = Color.FromArgb(46, 204, 113);
                swSecondary.Value = Color.FromArgb(77, 163, 255);
                ColorsChanged();
            }));
            swAccent.ValueChanged += (s, e) => ColorsChanged();
            swSecondary.ValueChanged += (s, e) => ColorsChanged();

            var preview = Ui.Grid();
            var pb = new FxButton { Text = "Primär", IconName = "record", Kind = FxKind.Primary, AutoSize = true };
            var nb = new FxButton { Text = "Normal", IconName = "cut", AutoSize = true };
            var tb = new FxButton { Text = "Aktiv", IconName = "starfill", AutoSize = true, Toggled = true };
            var tg = new FxToggle { Text = "Schalter", Checked = true };
            var sl = new FxSlider { Width = Px(180), Value = 65 };
            var pr = new FxProgress { Width = Px(180), Value = 60, Margin = new Padding(Px(12), Px(12), 0, 0) };
            Ui.Row(preview, null, Ui.Flow(pb, nb, tb, Spacer(12), tg));
            Ui.Row(preview, null, Ui.Flow(sl, pr));

            var font = Ui.Grid();
            cmbFont = Ui.Combo(Px(260));
            foreach (var n in Theme.FontNames) cmbFont.Items.Add(n);
            Ui.Row(font, "Schriftart", cmbFont);
            lblFontHint = Dim("Wird nach einem Neustart von ReplayClip übernommen.", 560);
            btnRestart = Ui.Button("refresh", "Jetzt neu starten", (s, e) => { settings.Save(); host.RestartApp(); });
            btnRestart.Visible = false;
            Ui.Row(font, null, lblFontHint);
            Ui.Row(font, null, btnRestart);
            cmbFont.SelectedIndexChanged += (s, e) =>
            {
                if (loading) return;
                settings.FontChoice = cmbFont.SelectedIndex;
                settings.Save();
                btnRestart.Visible = true;
            };

            var anim = Ui.Grid();
            rbAnimFull = new FxRadio { Text = "Voll – alle Übergänge und Effekte" };
            rbAnimReduced = new FxRadio { Text = "Reduziert – schneller und dezenter" };
            rbAnimOff = new FxRadio { Text = "Aus – keine Animationen" };
            var animFlow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            animFlow.Controls.Add(rbAnimFull);
            animFlow.Controls.Add(rbAnimReduced);
            animFlow.Controls.Add(rbAnimOff);
            Ui.Row(anim, null, animFlow);
            EventHandler animChanged = (s, e) =>
            {
                if (loading || !((RadioButton)s).Checked) return;
                settings.Animations = rbAnimFull.Checked ? 0 : rbAnimReduced.Checked ? 1 : 2;
                Theme.Animations = settings.Animations;
                settings.Save();
            };
            rbAnimFull.CheckedChanged += animChanged;
            rbAnimReduced.CheckedChanged += animChanged;
            rbAnimOff.CheckedChanged += animChanged;

            Stack(page, Ui.Group("Farben", colors), Ui.Group("Vorschau", preview), Ui.Group("Schrift", font), Ui.Group("Animationen", anim));
            return page;
        }

        static Control Spacer(int w) { return new Panel { Width = Px(w), Height = 1 }; }

        void ColorsChanged()
        {
            if (loading) return;
            settings.AccentArgb = swAccent.Value.ToArgb();
            settings.SecondaryArgb = swSecondary.Value.ToArgb();
            settings.Save();
            Theme.Configure(settings);
            host.AppearanceChanged();
            Theme.DarkTitleBar(this);
            Theme.RefreshAll();
        }

        // ---------------- Sound & Hinweise ----------------

        Panel BuildSoundPage()
        {
            var page = ScrollPage();
            var snd = Ui.Grid();
            cmbSound = Ui.Combo(Px(260));
            foreach (var c in SoundFx.Choices) cmbSound.Items.Add(c.Value);
            var play = Ui.Button("play", "Anhören", (s, e) => SoundFx.Play(settings.SaveSound, settings.SaveSoundFile, settings.SoundVolume, settings));
            Ui.Row(snd, "Clip gespeichert", Ui.Flow(cmbSound, play));
            txtSoundFile = new TextBox { ReadOnly = true };
            soundFileRow = Ui.Flow(Ui.Field(txtSoundFile, Px(330), "Keine Datei gewählt", "bell"), Ui.Button("Datei wählen …", (s, e) => PickSoundFile()));
            Ui.Row(snd, "Eigene Datei", soundFileRow);
            chkHotkeySound = new FxToggle();   // ersetzt durch eigene Sound-Zeile "Hotkey gedrückt"
            SoundRow(snd, "Hotkey gedrückt", () => settings.PressSound, v => settings.PressSound = v, () => settings.PressSoundFile, v => settings.PressSoundFile = v);
            SoundRow(snd, "Screenshot", () => settings.ShotSoundId, v => settings.ShotSoundId = v, () => settings.ShotSoundFile, v => settings.ShotSoundFile = v);
            SoundRow(snd, "Aufnahme startet", () => settings.RecStartSound, v => settings.RecStartSound = v, () => settings.RecStartSoundFile, v => settings.RecStartSoundFile = v);
            SoundRow(snd, "Aufnahme gespeichert", () => settings.RecStopSound, v => settings.RecStopSound = v, () => settings.RecStopSoundFile, v => settings.RecStopSoundFile = v);
            trkSoundVol = new FxSlider { Minimum = 0, Maximum = 100, Width = Px(240), DefaultValue = 70, SmallChange = 5 };
            lblSoundVol = new Label { AutoSize = true, Margin = new Padding(Px(8), Px(6), 0, 0), Tag = "keep", ForeColor = Theme.Text };
            Ui.Row(snd, "Lautstärke", Ui.Flow(trkSoundVol, lblSoundVol));
            chkErrorSound = new FxToggle { Text = "Warnton, wenn das Speichern fehlschlägt" };
            Ui.Row(snd, "", chkErrorSound);
            Ui.Row(snd, null, Dim("Eigene Datei: WAV, MP3, OGG … (die ersten 6 Sekunden werden verwendet).", 560));

            var ov = Ui.Grid();
            chkOverlay = new FxToggle { Text = "Kurze Einblendung über dem Spiel, wenn ein Clip gespeichert wurde" };
            Ui.Row(ov, null, chkOverlay);
            cmbOverlayPos = Ui.Combo(Px(200));
            cmbOverlayPos.Items.AddRange(new object[] { "Oben rechts", "Oben links", "Unten rechts", "Unten links" });
            Ui.Row(ov, "Position", Ui.Flow(cmbOverlayPos, Ui.Button("sparkle", "Testen", (s, e) =>
                OverlayToast.ShowMessage("Clip gespeichert", "So sieht die Einblendung aus  ·  5:00", Theme.Accent, Screen.FromControl(this).Bounds, settings.OverlayPos))));
            Ui.Row(ov, null, Dim("Funktioniert über Spielen im Vollbild-Fenstermodus (randlos). Bei exklusivem Vollbild zeichnet Windows keine anderen Fenster darüber – dann hörst du nur den Sound.", 600));
            chkBalloon = new FxToggle { Text = "Zusätzlich Windows-Benachrichtigung anzeigen" };
            Ui.Row(ov, null, chkBalloon);

            Stack(page, Ui.Group("Sound", snd), Ui.Group("Einblendungen", ov));

            cmbSound.SelectedIndexChanged += (s, e) =>
            {
                if (cmbSound.SelectedIndex < 0) return;
                string id = SoundFx.Choices[cmbSound.SelectedIndex].Key;
                SetRowVisible(soundFileRow, id == "file");
                if (loading) return;
                settings.SaveSound = id;
                settings.Save();
                if (id == "file" && string.IsNullOrEmpty(settings.SaveSoundFile)) PickSoundFile();
                else SoundFx.Play(id, settings.SaveSoundFile, settings.SoundVolume, settings);
            };
            trkSoundVol.ValueChanged += (s, e) =>
            {
                lblSoundVol.Text = trkSoundVol.Value + " %";
                if (loading) return;
                settings.SoundVolume = trkSoundVol.Value;
                settings.Save();
            };
            trkSoundVol.MouseUp += (s, e) => SoundFx.Play(settings.SaveSound, settings.SaveSoundFile, settings.SoundVolume, settings);
            chkHotkeySound.CheckedChanged += (s, e) =>
            {
                if (loading) return;
                settings.HotkeySound = chkHotkeySound.Checked; settings.Save();
                if (chkHotkeySound.Checked) SoundFx.Play("press", null, settings.SoundVolume, settings);
            };
            chkErrorSound.CheckedChanged += (s, e) => { if (!loading) { settings.ErrorSound = chkErrorSound.Checked; settings.Save(); } };
            chkOverlay.CheckedChanged += (s, e) => { if (!loading) { settings.OverlayToast = chkOverlay.Checked; settings.Save(); } };
            cmbOverlayPos.SelectedIndexChanged += (s, e) => { if (!loading) { settings.OverlayPos = cmbOverlayPos.SelectedIndex; settings.Save(); } };
            chkBalloon.CheckedChanged += (s, e) => { if (!loading) { settings.NotifyBalloon = chkBalloon.Checked; settings.Save(); } };
            return page;
        }

        static void SetRowVisible(Control rowContent, bool visible)
        {
            rowContent.Visible = visible;
            var t = rowContent.Parent as TableLayoutPanel;
            if (t == null) return;
            var pos = t.GetPositionFromControl(rowContent);
            var lbl = t.GetControlFromPosition(0, pos.Row);
            if (lbl != null && lbl != rowContent) lbl.Visible = visible;
        }

        /// <summary>Sound-Auswahl für ein Ereignis: Liste + Anhören + eigene Datei.</summary>
        void SoundRow(TableLayoutPanel g, string label, Func<string> getId, Action<string> setId, Func<string> getFile, Action<string> setFile)
        {
            var cmb = Ui.Combo(Px(260));
            foreach (var c in SoundFx.Choices) cmb.Items.Add(c.Value);
            int si = 0;
            for (int i = 0; i < SoundFx.Choices.Length; i++) if (SoundFx.Choices[i].Key == getId()) si = i;
            cmb.SelectedIndex = si;
            Action pick = () =>
            {
                using (var d = new OpenFileDialog { Filter = "Audio (*.wav;*.mp3;*.ogg;*.flac;*.m4a)|*.wav;*.mp3;*.ogg;*.flac;*.m4a|Alle Dateien|*.*", Title = "Sound für „" + label + "“" })
                    if (d.ShowDialog(this) == DialogResult.OK) { setFile(d.FileName); settings.Save(); SoundFx.Play("file", d.FileName, settings.SoundVolume, settings); }
            };
            var file = Ui.Button("bell", "Datei …", (s, e) => pick());
            file.Visible = getId() == "file";
            var play = Ui.Button("play", "Anhören", (s, e) => SoundFx.Play(getId(), getFile(), settings.SoundVolume, settings));
            Ui.Row(g, label, Ui.Flow(cmb, play, file));
            cmb.SelectedIndexChanged += (s, e) =>
            {
                if (cmb.SelectedIndex < 0) return;
                string id = SoundFx.Choices[cmb.SelectedIndex].Key;
                file.Visible = id == "file";
                setId(id);
                settings.Save();
                if (id == "file" && string.IsNullOrEmpty(getFile())) pick();
                else SoundFx.Play(id, getFile(), settings.SoundVolume, settings);
            };
        }

        void PickSoundFile()
        {
            using (var d = new OpenFileDialog { Filter = "Audio (*.wav;*.mp3;*.ogg;*.flac;*.m4a)|*.wav;*.mp3;*.ogg;*.flac;*.m4a|Alle Dateien|*.*", Title = "Sound auswählen" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                settings.SaveSoundFile = d.FileName;
                txtSoundFile.Text = d.FileName;
                settings.Save();
                SoundFx.Play("file", settings.SaveSoundFile, settings.SoundVolume, settings);
            }
        }

        Panel BuildSystemPage()
        {
            var page = ScrollPage();
            var paths = Ui.Grid();
            txtBufferDir = new TextBox { ReadOnly = true };
            Ui.Row(paths, "Puffer-Ordner", Ui.Flow(Ui.Field(txtBufferDir, Px(400), null, "storage"), Ui.Button("Ändern …", (s, e) => PickFolder(txtBufferDir, false))));
            Ui.Row(paths, null, Dim("Tipp: Puffer-Ordner auf eine SSD legen. Er wird beim Stoppen automatisch geleert.", 600));

            var ffg = Ui.Grid();
            txtFfmpeg = new TextBox { ReadOnly = true };
            btnDownload = Ui.Button("save", "Herunterladen", (s, e) => DownloadFfmpeg());
            Ui.Row(ffg, "ffmpeg.exe", Ui.Flow(Ui.Field(txtFfmpeg, Px(400), "nicht gefunden", "video"), Ui.Button("Auswählen …", (s, e) => PickFfmpeg()), btnDownload));
            lblFfmpegState = Dim("", 600);
            dlProgress = new FxProgress { Width = Px(420), Visible = false, Margin = new Padding(0, Px(6), 0, Px(6)) };
            Ui.Row(ffg, null, lblFfmpegState);
            Ui.Row(ffg, null, dlProgress);
            Stack(page, Ui.Group("Aufnahme-Puffer", paths), Ui.Group("FFmpeg (Encoder)", ffg));
            return page;
        }

        Panel BuildGeneralPage()
        {
            var page = ScrollPage();
            var beh = Ui.Grid();
            chkAutostart = new FxToggle { Text = "Mit Windows starten (minimiert im Infobereich)" };
            chkAutoBuffer = new FxToggle { Text = "Replay-Buffer beim Programmstart automatisch aktivieren" };
            chkMinimized = new FxToggle { Text = "Beim Start nur im Infobereich (Tray) starten" };
            foreach (var c in new[] { chkAutostart, chkMinimized }) Ui.Row(beh, null, c);
            Stack(page, Ui.Group("Verhalten", beh));
            chkAutostart.CheckedChanged += (s, e) => { if (!loading) SetAutostart(chkAutostart.Checked); };
            chkAutoBuffer.CheckedChanged += (s, e) => { if (!loading) { settings.StartBufferOnLaunch = chkAutoBuffer.Checked; settings.Save(); } };
            chkMinimized.CheckedChanged += (s, e) => { if (!loading) { settings.StartMinimized = chkMinimized.Checked; settings.Save(); } };
            return page;
        }

        int detectToken;

        /// <summary>Im Hintergrund testen, welcher Encoder mit der aktuellen Auswahl tatsächlich benutzt wird.</summary>
        void DetectEncoder()
        {
            string ff = Ffmpeg.Find(settings);
            if (ff == null) { lblEncoder.ForeColor = Theme.TextDim; lblEncoder.Text = "FFmpeg fehlt noch"; return; }
            int token = ++detectToken;
            string pref = settings.Encoder;
            bool wantHevc = settings.Codec == "hevc";
            lblEncoder.ForeColor = Theme.TextDim;
            lblEncoder.Text = "prüfe Grafikkarte …";
            ThreadPool.QueueUserWorkItem(_ =>
            {
                bool hevc = wantHevc;
                var k = Encoders.Resolve(ff, pref, ref hevc);
                OnUi(() =>
                {
                    if (token != detectToken || IsDisposed) return;
                    bool fallback = pref != "auto" && Encoders.Prefs[(int)k + 1] != pref;
                    string txt = (fallback ? "nicht verfügbar → " : "✓ ") + Encoders.Label(k) + (wantHevc && !hevc ? " (nur H.264)" : "");
                    if (k == EncKind.Cpu) txt += " – höhere CPU-Last";
                    lblEncoder.ForeColor = fallback || k == EncKind.Cpu ? Theme.Warning : Theme.Accent;
                    lblEncoder.Text = txt;
                });
            });
        }

        UpdateInfo foundUpdate;
        Label lblUpdNotes;
        FxButton btnInstall;
        Card updCard;

        Panel BuildUpdatesPage()
        {
            var page = ScrollPage();
            var st = Ui.Grid();
            var ver = new Label { Text = "ReplayClip " + Program.Version, AutoSize = true, Font = Theme.Semi(15f), ForeColor = Theme.Accent, Tag = "keep" };
            Ui.Row(st, "Installiert", ver);
            lblUpdate = new Label { AutoSize = true, ForeColor = Theme.TextDim, MaximumSize = new Size(Px(560), 0), Margin = new Padding(0, Px(6), 0, 0), Tag = "keep" };
            Ui.Row(st, "Status", lblUpdate);
            Ui.Row(st, "", Ui.Flow(Ui.Button("refresh", "Jetzt nach Updates suchen", (s, e) => CheckUpdateNow()),
                Ui.Button("open", "Alle Versionen auf GitHub", (s, e) => Shell.OpenFile("https://github.com/" + settings.UpdateRepo + "/releases"))));

            // Neue Version (nur sichtbar, wenn gefunden)
            var nv = Ui.Grid();
            lblUpdNotes = new Label { AutoSize = true, ForeColor = Theme.Text, MaximumSize = new Size(Px(600), 0), Tag = "keep" };
            Ui.Row(nv, null, lblUpdNotes);
            btnInstall = new FxButton { Text = "Jetzt aktualisieren", IconName = "save", Kind = FxKind.Primary, AutoSize = true };
            btnInstall.Click += (s, e) => { if (foundUpdate != null) host.OfferUpdate(foundUpdate, true); };
            Ui.Row(nv, null, btnInstall);
            updCard = Ui.Group("Neue Version verfügbar", nv);
            updCard.Visible = false;

            var opt = Ui.Grid();
            chkUpdate = new FxToggle { Text = "Automatisch nach Updates suchen (beim Start und alle 6 Stunden) und Bescheid geben" };
            Ui.Row(opt, null, chkUpdate);
            txtRepo = new TextBox();
            Ui.Row(opt, "GitHub-Repo", Ui.Field(txtRepo, Px(320), "Besitzer/ReplayClip", "open"));
            Ui.Row(opt, null, Dim("Neue Versionen kommen als Release aus diesem GitHub-Repository. ReplayClip lädt sie auf Wunsch selbst herunter, tauscht die Programmdatei aus und startet neu.", 600));

            Stack(page, Ui.Group("Version", st), updCard, Ui.Group("Einstellungen", opt));
            chkUpdate.CheckedChanged += (s, e) => { if (!loading) { settings.UpdateCheck = chkUpdate.Checked; settings.Save(); } };
            txtRepo.Leave += (s, e) =>
            {
                string r = txtRepo.Text.Trim().Replace("https://github.com/", "").Trim('/');
                if (r.Length == 0) r = Updater.DefaultRepo;
                txtRepo.Text = r;
                if (r != settings.UpdateRepo) { settings.UpdateRepo = r; settings.SkipUpdate = ""; settings.Save(); }
            };
            ShowUpdateStatus(host.PendingUpdate, null);
            return page;
        }

        void ShowUpdateStatus(UpdateInfo u, string err)
        {
            if (err != null) { lblUpdate.ForeColor = Theme.Danger; lblUpdate.Text = err; return; }
            DateTime last;
            string when = DateTime.TryParse(settings.LastUpdateCheck, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out last)
                ? "Zuletzt gesucht: " + last.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.CurrentCulture) : "Noch nicht gesucht.";
            foundUpdate = u;
            if (u == null)
            {
                lblUpdate.ForeColor = settings.LastUpdateCheck.Length > 0 ? Theme.Accent : Theme.TextDim;
                lblUpdate.Text = (settings.LastUpdateCheck.Length > 0 ? "✓  Du hast die neueste Version.   " : "") + when;
                updCard.Visible = false;
                return;
            }
            lblUpdate.ForeColor = Theme.Accent;
            lblUpdate.Text = "Version " + u.Display + " ist verfügbar.   " + when;
            string notes = (u.Notes ?? "").Trim();
            if (notes.Length > 1200) notes = notes.Substring(0, 1200) + " …";
            lblUpdNotes.Text = "ReplayClip " + u.Display + "\n\n" + (notes.Length > 0 ? notes : "(keine Beschreibung)");
            btnInstall.Text = "Auf " + u.Display + " aktualisieren";
            updCard.Visible = true;
        }

        void CheckUpdateNow()
        {
            string r = txtRepo.Text.Trim().Replace("https://github.com/", "").Trim('/');
            if (r.Length > 0 && r != settings.UpdateRepo) { settings.UpdateRepo = r; settings.Save(); }
            if (!Updater.RepoValid(settings.UpdateRepo)) { ShowUpdateStatus(null, "Bitte zuerst das GitHub-Repo eintragen (z.B. name/ReplayClip)."); return; }
            lblUpdate.ForeColor = Theme.TextDim;
            lblUpdate.Text = "Suche …";
            Updater.CheckAsync(this, settings.UpdateRepo, (u, err) =>
            {
                if (IsDisposed) return;
                if (err == null) { settings.LastUpdateCheck = DateTime.UtcNow.ToString("o"); settings.Save(); }
                ShowUpdateStatus(u, err);
            });
        }

        Panel BuildAboutPage()
        {
            var page = ScrollPage();
            var g = Ui.Grid();
            var ver = new Label { Text = "ReplayClip " + Program.Version, AutoSize = true, Font = Theme.Semi(16f), ForeColor = Theme.Accent, Tag = "keep" };
            Ui.Row(g, null, ver);
            Ui.Row(g, null, Dim("Replay-Buffer mit Grafikkarten-Encoder (NVIDIA, AMD, Intel), getrennten Tonspuren, Bibliothek mit Tags und Schnitt-Editor.\nDie Versionsnummer steht auch in der Titelleiste und oben neben dem Namen – so siehst du, dass die richtige Datei läuft.", 600));
            Ui.Row(g, "Programmdatei", new Label { Text = Application.ExecutablePath, AutoSize = true, MaximumSize = new Size(Px(560), 0) });
            Ui.Row(g, "Einstellungen", Ui.Flow(new Label { Text = Settings.AppDataDir, AutoSize = true, Margin = new Padding(0, Px(8), Px(8), 0) },
                Ui.IconButton("open", "Ordner öffnen", (s, e) => Shell.OpenFolder(Settings.AppDataDir))));
            Ui.Row(g, "Diagnose", Ui.Button("copy", "Diagnose in Zwischenablage kopieren", (s, e) => CopyDiagnostics()));
            Ui.Row(g, "Einführung", Ui.Button("info", "Kurzanleitung nochmal zeigen", (s, e) => host.ShowIntro()));
            Ui.Row(g, "GitHub", Ui.Button("open", "Projektseite öffnen", (s, e) =>
            {
                if (Updater.RepoValid(settings.UpdateRepo)) Shell.OpenFile("https://github.com/" + settings.UpdateRepo);
                else ShowPageByName("Allgemein");
            }));
            Ui.Row(g, "Schrift", Dim("Inter – © The Inter Project Authors, SIL Open Font License 1.1", 560));
            Stack(page, Ui.Group("Über", g));
            return page;
        }

        // ======================= Einstellungen -> UI =======================

        void LoadSettingsIntoUi()
        {
            LoadMonitors();
            cmbFps.SelectedIndex = Math.Max(0, Array.IndexOf(new[] { 30, 60, 120, 144 }, settings.Fps));
            cmbCodec.SelectedIndex = settings.Codec == "hevc" ? 1 : 0;
            cmbEncoder.SelectedIndex = Math.Max(0, Array.IndexOf(Encoders.Prefs, settings.Encoder));
            DetectEncoder();
            int bi = Array.IndexOf(new[] { 10, 15, 20, 30, 40, 50, 80 }, settings.BitrateMbps);
            cmbBitrate.SelectedIndex = bi < 0 ? 3 : bi;
            numMinutes.Value = Math.Max(1, Math.Min(20, settings.BufferMinutes));
            chkMouse.Checked = settings.DrawMouse;
            chkReset.Checked = settings.ResetBufferAfterSave;
            hkSave.SetHotkey(settings.SaveHotkeyMods, settings.SaveHotkeyKey);
            cmbController.SelectedIndex = Math.Max(0, Math.Min(Gamepad.Names.Length - 1, settings.ControllerCombo));
            hkToggle.SetHotkey(settings.ToggleHotkeyMods, settings.ToggleHotkeyKey);
            hkShot.SetHotkey(settings.ShotHotkeyMods, settings.ShotHotkeyKey);
            UpdateEstimate();

            numOffset.Value = Math.Max(-1000, Math.Min(1000, settings.AudioOffsetMs));
            int ai = Array.IndexOf(new[] { 128, 160, 192, 256, 320 }, settings.AudioBitrateK);
            cmbAudioBitrate.SelectedIndex = ai < 0 ? 2 : ai;
            RefreshTrackList(0);

            txtSaveDir.Text = settings.SaveDir;
            cmbStructure.SelectedIndex = settings.FolderStructure;
            cmbAfterExport.SelectedIndex = settings.AfterExport;
            RefreshQuickList(0);

            swAccent.Value = Theme.Accent;
            swSecondary.Value = Theme.Secondary;
            cmbFont.SelectedIndex = Math.Max(0, Math.Min(Theme.FontNames.Length - 1, settings.FontChoice));
            rbAnimFull.Checked = settings.Animations == 0;
            rbAnimReduced.Checked = settings.Animations == 1;
            rbAnimOff.Checked = settings.Animations == 2;

            int si = 0;
            for (int i = 0; i < SoundFx.Choices.Length; i++) if (SoundFx.Choices[i].Key == settings.SaveSound) si = i;
            cmbSound.SelectedIndex = si;
            SetRowVisible(soundFileRow, settings.SaveSound == "file");
            txtSoundFile.Text = settings.SaveSoundFile;
            trkSoundVol.Value = settings.SoundVolume;
            lblSoundVol.Text = settings.SoundVolume + " %";
            chkErrorSound.Checked = settings.ErrorSound;
            chkHotkeySound.Checked = settings.HotkeySound;
            chkOverlay.Checked = settings.OverlayToast;
            cmbOverlayPos.SelectedIndex = settings.OverlayPos;
            chkBalloon.Checked = settings.NotifyBalloon;

            txtBufferDir.Text = settings.BufferDir;
            UpdateFfmpegState();
            chkAutostart.Checked = GetAutostart();
            chkAutoBuffer.Checked = settings.StartBufferOnLaunch;
            chkMinimized.Checked = settings.StartMinimized;
            chkUpdate.Checked = settings.UpdateCheck;
            txtRepo.Text = settings.UpdateRepo;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            StopTest();
            if (meterTimer != null) meterTimer.Stop();
            host.RegisterHotkeys();
            settings.Save();
            base.OnFormClosing(e);
        }

        // UI-Aufrufe aus Hintergrund-Threads: Solange das Fenster noch kein Handle hat, meldet InvokeRequired
        // fälschlich "false" – dann würde der Code im falschen Thread laufen (Absturz im Layout). Deshalb puffern.
        readonly int uiThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
        readonly List<Action> pendingUi = new List<Action>();

        void OnUi(Action a)
        {
            if (IsDisposed) return;
            try
            {
                if (IsHandleCreated) { if (InvokeRequired) BeginInvoke(a); else a(); return; }
                if (System.Threading.Thread.CurrentThread.ManagedThreadId == uiThreadId) { a(); return; }
                lock (pendingUi) pendingUi.Add(a);
                if (IsHandleCreated) FlushPendingUi();
            }
            catch (InvalidOperationException) { }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            FlushPendingUi();
        }

        void FlushPendingUi()
        {
            Action[] q;
            lock (pendingUi) { q = pendingUi.ToArray(); pendingUi.Clear(); }
            foreach (var x in q) { try { BeginInvoke(x); } catch (InvalidOperationException) { } }
        }

        void LoadMonitors()
        {
            bool was = loading;
            loading = true;
            cmbMonitor.Items.Clear();
            var mons = Native.GetMonitors();
            foreach (var m in mons) cmbMonitor.Items.Add(m);
            int sel = 0;
            for (int i = 0; i < mons.Count; i++) if (mons[i].Index == settings.MonitorIndex) sel = i;
            if (cmbMonitor.Items.Count > 0) { cmbMonitor.SelectedIndex = -1; cmbMonitor.SelectedIndex = sel; }
            loading = was;
        }

        void UpdateEstimate()
        {
            double mbPerMin = settings.BitrateMbps * 60.0 / 8.0;
            double clip = mbPerMin * settings.BufferMinutes;
            lblEstimate.Text = string.Format(CultureInfo.CurrentCulture,
                "Ein Clip mit voller Länge ist ca. {0:0.0} GB groß. Der Puffer liegt als ~2-s-Stücke auf der Platte (Puffer-Ordner), nicht im RAM.",
                clip / 1024.0);
        }

        // ---------------- Audio ----------------

        void BuildAudioTab()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, BackColor = Theme.Bg, Padding = new Padding(0, Px(6), 0, 0) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Px(310)));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 56));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 44));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            // Spurliste
            var left = new Card { Text = "Tonspuren", Subtitle = "= Spur 1, 2, 3 …", Dock = DockStyle.Fill, Margin = new Padding(0, 0, Px(12), Px(12)) };
            lstTracks = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = Px(50), BorderStyle = BorderStyle.None, BackColor = Theme.Panel, Tag = "keep" };
            Theme.DarkScroll(lstTracks);
            lstTracks.DrawItem += DrawTrackItem;
            lstTracks.SelectedIndexChanged += (s, e) => { LoadEditor(); lstTracks.Invalidate(); };
            var listButtons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(0, Px(6), 0, 0) };
            listButtons.Controls.Add(Ui.Button("add", "Spur", (s, e) => AddTrack()));
            listButtons.Controls.Add(Ui.IconButton("delete", "Spur entfernen", (s, e) => RemoveTrack()));
            listButtons.Controls.Add(Ui.IconButton("up", "Nach oben", (s, e) => MoveTrack(-1)));
            listButtons.Controls.Add(Ui.IconButton("down", "Nach unten", (s, e) => MoveTrack(1)));
            left.Controls.Add(lstTracks);
            left.Controls.Add(listButtons);

            // Editor
            var ed = Ui.Grid();
            txtTrackName = new TextBox();
            Ui.Row(ed, "Name", Ui.Field(txtTrackName, Px(280), null, "edit"));
            cmbTrackType = Ui.Combo(Px(280));
            foreach (TrackType t in Enum.GetValues(typeof(TrackType))) cmbTrackType.Items.Add(TrackConfig.TypeLabel(t));
            Ui.Row(ed, "Quelle", cmbTrackType);

            cmbDevice = Ui.Combo(Px(340));
            Ui.Row(ed, "Gerät", Ui.Flow(cmbDevice, Ui.IconButton("refresh", "Geräte neu einlesen", (s, e) => LoadDevices())));
            lblDevice = (Label)ed.GetControlFromPosition(0, ed.RowCount - 1);

            appsPanel = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            lstApps = new ListBox { Size = new Size(Px(240), Px(118)), IntegralHeight = false };
            Ui.DarkList(lstApps, 30);
            var appBtns = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(Px(8), 0, 0, 0) };
            appBtns.Controls.Add(Ui.Button("add", "App hinzufügen …", (s, e) => AddApp()));
            appBtns.Controls.Add(Ui.Button("remove", "Entfernen", (s, e) => RemoveApp()));
            appsPanel.Controls.Add(lstApps);
            appsPanel.Controls.Add(appBtns);
            Ui.Row(ed, "Apps", appsPanel);
            lblApps = (Label)ed.GetControlFromPosition(0, ed.RowCount - 1);

            trkVolume = new FxSlider { Minimum = 0, Maximum = 500, Width = Px(260), SmallChange = 5, LargeChange = 25, DefaultValue = 100 };
            lblVolume = new Label { AutoSize = true, Margin = new Padding(Px(8), Px(6), 0, 0), Tag = "keep", ForeColor = Theme.Text };
            Ui.Row(ed, "Lautstärke", Ui.Flow(trkVolume, lblVolume));
            chkDenoise = new FxToggle { Text = "Rauschunterdrückung (filtert Tastatur, Lüfter, Rauschen – ideal fürs Mikrofon)" };
            Ui.Row(ed, "", chkDenoise);

            Ui.Row(ed, null, Dim("•  Bestimmte Apps: nur der Ton dieser Programme (inkl. Unterprozesse), z.B. Discord.\n" +
                       "•  Restlicher PC-Sound: alles, was keiner App-Spur zugeordnet ist (Spiele, Browser, Systemklänge …).\n" +
                       "•  Gesamter PC-Sound: wirklich alles, unabhängig von anderen Spuren.\n" +
                       "•  Mix aller Spuren: Summe der anderen Spuren – als Spur 1 anlegen, damit normale Player alles abspielen.\n" +
                       "Eine App muss nicht laufen: Sobald sie startet, wird sie automatisch erkannt.", 480));

            edScroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Panel };
            ed.Dock = DockStyle.Top;
            edScroll.Controls.Add(ed);
            grpEditor = new Card { Text = "Spur bearbeiten", Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, Px(12)) };
            grpEditor.Controls.Add(edScroll);

            // Unten: globale Audio-Optionen
            var opts = new Card { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0) };
            var of = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true };
            numOffset = new FxNumber { Minimum = -1000, Maximum = 1000, Increment = 10, Width = Px(130) };
            cmbAudioBitrate = Ui.Combo(Px(120));
            foreach (var k in new[] { 128, 160, 192, 256, 320 }) cmbAudioBitrate.Items.Add(k + " kbit/s");
            of.Controls.Add(new Label { Text = "Ton-Versatz (ms, + = Ton später)", AutoSize = true, Margin = new Padding(0, Px(9), Px(8), 0), ForeColor = Theme.TextDim, Tag = "keep" });
            of.Controls.Add(numOffset);
            of.Controls.Add(new Label { Text = "AAC-Bitrate je Spur", AutoSize = true, Margin = new Padding(Px(28), Px(9), Px(8), 0), ForeColor = Theme.TextDim, Tag = "keep" });
            of.Controls.Add(cmbAudioBitrate);
            opts.Controls.Add(of);

            root.Controls.Add(left, 0, 0);
            root.Controls.Add(grpEditor, 1, 0);
            var grpMeter = BuildMeterGroup();
            root.Controls.Add(grpMeter, 0, 1);
            root.SetColumnSpan(grpMeter, 2);
            root.Controls.Add(opts, 0, 2);
            root.SetColumnSpan(opts, 2);
            pageAudio.Controls.Add(root);

            txtTrackName.TextChanged += (s, e) =>
            {
                var t = CurrentTrack();
                if (t == null || editorLoading) return;
                t.Name = txtTrackName.Text.Trim().Length > 0 ? txtTrackName.Text.Trim() : "Spur";
                lstTracks.Invalidate();
                MarkRestartNeeded();
            };
            cmbTrackType.SelectedIndexChanged += (s, e) =>
            {
                var t = CurrentTrack();
                if (t == null || editorLoading) return;
                t.Type = (TrackType)cmbTrackType.SelectedIndex;
                UpdateEditorVisibility();
                lstTracks.Invalidate();
                MarkRestartNeeded();
            };
            cmbDevice.SelectedIndexChanged += (s, e) =>
            {
                var t = CurrentTrack();
                if (t == null || editorLoading) return;
                var d = cmbDevice.SelectedItem as AudioDeviceInfo;
                t.DeviceId = d == null ? "" : (d.Id ?? "");
                lstTracks.Invalidate();
                MarkRestartNeeded();
            };
            chkDenoise.CheckedChanged += (s, e) =>
            {
                var t = CurrentTrack();
                if (t == null || editorLoading) return;
                t.Denoise = chkDenoise.Checked;
                MarkRestartNeeded();
            };
            trkVolume.ValueChanged += (s, e) =>
            {
                lblVolume.Text = trkVolume.Value + " %";
                var t = CurrentTrack();
                if (t == null || editorLoading) return;
                t.Volume = trkVolume.Value;
                MarkRestartNeeded();
            };
            numOffset.ValueChanged += (s, e) => { if (!loading) { settings.AudioOffsetMs = (int)numOffset.Value; MarkRestartNeeded(); } };
            cmbAudioBitrate.SelectedIndexChanged += (s, e) => { if (!loading) { settings.AudioBitrateK = new[] { 128, 160, 192, 256, 320 }[cmbAudioBitrate.SelectedIndex]; MarkRestartNeeded(); } };
        }

        void DrawTrackItem(object sender, DrawItemEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var b = new SolidBrush(lstTracks.BackColor)) g.FillRectangle(b, e.Bounds);
            if (e.Index < 0 || e.Index >= settings.Tracks.Count) return;
            var t = settings.Tracks[e.Index];
            string detail = TrackConfig.TypeLabel(t.Type);
            if (t.Type == TrackType.Apps) detail += t.Apps.Count > 0 ? ":  " + string.Join(", ", t.Apps.ToArray()) : "  (noch keine)";
            bool sel = (e.State & DrawItemState.Selected) != 0;
            var r = new RectangleF(e.Bounds.X + Px(2), e.Bounds.Y + Px(3), e.Bounds.Width - Px(6), e.Bounds.Height - Px(6));
            var c = Theme.Palette(e.Index);
            Gfx.FillRound(g, sel ? Gfx.Lerp(Theme.Panel2, c, 0.18f) : Theme.Panel2, r, Px(8));
            if (sel) Gfx.DrawRound(g, Gfx.Alpha(c, 200), r, Px(8), 1.2f);
            var num = new RectangleF(r.X + Px(10), r.Y + r.Height / 2 - Px(14), Px(28), Px(28));
            Gfx.FillRound(g, Gfx.Alpha(c, sel ? 255 : 60), num, Px(7));
            TextRenderer.DrawText(g, (e.Index + 1).ToString(), Theme.Semi(10f), Rectangle.Round(num), sel ? (Gfx.Lum(c) > 0.62f ? Color.Black : Color.White) : c, Gfx.Center);
            int x = (int)num.Right + Px(12);
            TextRenderer.DrawText(g, t.Name, Theme.Semi(9.5f), new Rectangle(x, (int)r.Y + Px(6), (int)r.Right - x - Px(6), Px(20)), Theme.Text, Gfx.Left);
            TextRenderer.DrawText(g, detail, Theme.F(8.5f), new Rectangle(x, (int)r.Y + Px(24), (int)r.Right - x - Px(6), Px(18)), Theme.TextDim, Gfx.Left);
        }

        TrackConfig CurrentTrack()
        {
            int i = lstTracks.SelectedIndex;
            return i >= 0 && i < settings.Tracks.Count ? settings.Tracks[i] : null;
        }

        void RefreshTrackList(int select)
        {
            lstTracks.BeginUpdate();
            lstTracks.Items.Clear();
            foreach (var t in settings.Tracks) lstTracks.Items.Add(t.Name);
            lstTracks.EndUpdate();
            if (settings.Tracks.Count > 0) lstTracks.SelectedIndex = Math.Max(0, Math.Min(select, settings.Tracks.Count - 1));
            LoadEditor();
        }

        void LoadEditor()
        {
            var t = CurrentTrack();
            edScroll.Visible = t != null;
            if (t == null) return;
            editorLoading = true;
            txtTrackName.Text = t.Name;
            cmbTrackType.SelectedIndex = (int)t.Type;
            FillDeviceCombo(t);
            lstApps.Items.Clear();
            foreach (var a in t.Apps) lstApps.Items.Add(a + ".exe");
            trkVolume.Value = Math.Max(0, Math.Min(500, t.Volume));
            chkDenoise.Checked = t.Denoise;
            lblVolume.Text = trkVolume.Value + " %";
            editorLoading = false;
            UpdateEditorVisibility();
        }

        void FillDeviceCombo(TrackConfig t)
        {
            cmbDevice.Items.Clear();
            cmbDevice.Items.Add(new AudioDeviceInfo { Id = "", Name = "Standard-Aufnahmegerät (Windows)" });
            cmbDevice.Items.Add(new AudioDeviceInfo { Id = TrackConfig.DefaultCommunications, Name = "Standard-Kommunikationsgerät (wie Discord/Teams)" });
            int sel = t.DeviceId == TrackConfig.DefaultCommunications ? 1 : 0;
            if (captureDevices != null)
            {
                foreach (var d in captureDevices)
                {
                    cmbDevice.Items.Add(d);
                    if (d.Id == t.DeviceId) sel = cmbDevice.Items.Count - 1;
                }
            }
            if (sel == 0 && !string.IsNullOrEmpty(t.DeviceId) && t.DeviceId != TrackConfig.DefaultCommunications)
            {
                cmbDevice.Items.Add(new AudioDeviceInfo { Id = t.DeviceId, Name = "(gespeichertes Gerät, gerade nicht verfügbar)" });
                sel = cmbDevice.Items.Count - 1;
            }
            cmbDevice.SelectedIndex = -1;
            cmbDevice.SelectedIndex = sel;
        }

        void UpdateEditorVisibility()
        {
            var t = CurrentTrack();
            if (t == null) return;
            bool mic = t.Type == TrackType.Microphone;
            bool apps = t.Type == TrackType.Apps;
            cmbDevice.Parent.Visible = mic;
            if (lblDevice != null) lblDevice.Visible = mic;
            appsPanel.Visible = apps;
            if (lblApps != null) lblApps.Visible = apps;
        }

        void LoadDevices()
        {
            captureDevices = Native.RunMta(() => CoreAudio.ListDevices(CoreAudio.eCapture), new List<AudioDeviceInfo>());
            var t = CurrentTrack();
            if (t != null)
            {
                editorLoading = true;
                FillDeviceCombo(t);
                editorLoading = false;
            }
        }

        void AddTrack()
        {
            if (settings.Tracks.Count >= 6) { FxDialog.Info(this, "Maximal 6 Tonspuren", "Mehr als 6 Tonspuren pro Clip werden nicht unterstützt.", "warning"); return; }
            settings.Tracks.Add(new TrackConfig { Name = "Spur " + (settings.Tracks.Count + 1), Type = TrackType.Apps });
            RefreshTrackList(settings.Tracks.Count - 1);
            MarkRestartNeeded();
        }

        void RemoveTrack()
        {
            int i = lstTracks.SelectedIndex;
            if (i < 0) return;
            settings.Tracks.RemoveAt(i);
            RefreshTrackList(i);
            MarkRestartNeeded();
        }

        void MoveTrack(int dir)
        {
            int i = lstTracks.SelectedIndex;
            int j = i + dir;
            if (i < 0 || j < 0 || j >= settings.Tracks.Count) return;
            var t = settings.Tracks[i];
            settings.Tracks[i] = settings.Tracks[j];
            settings.Tracks[j] = t;
            RefreshTrackList(j);
            MarkRestartNeeded();
        }

        void AddApp()
        {
            var t = CurrentTrack();
            if (t == null) return;
            using (var dlg = new AppPickerForm())
            {
                if (dlg.ShowDialog(this) == DialogResult.OK && !string.IsNullOrEmpty(dlg.Selected))
                {
                    string key = Native.ExeKey(dlg.Selected);
                    if (!t.Apps.Contains(key))
                    {
                        t.Apps.Add(key);
                        foreach (var other in settings.Tracks)
                            if (other != t) other.Apps.Remove(key);
                        if (t.Name.StartsWith("Spur ") && t.Apps.Count == 1) t.Name = char.ToUpper(key[0]) + key.Substring(1);
                    }
                    LoadEditor();
                    lstTracks.Invalidate();
                    MarkRestartNeeded();
                }
            }
        }

        void RemoveApp()
        {
            var t = CurrentTrack();
            if (t == null || lstApps.SelectedIndex < 0) return;
            t.Apps.RemoveAt(lstApps.SelectedIndex);
            LoadEditor();
            lstTracks.Invalidate();
            MarkRestartNeeded();
        }

        // ---------------- Pegel & Test ----------------

        Card BuildMeterGroup()
        {
            var g = new Card { Text = "Pegel und Test", Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, Px(12)) };
            var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false, Padding = new Padding(0, 0, 0, Px(8)) };
            btnTest = Ui.Button("play", "Test starten", (s, e) => { if (testEngine != null) StopTest(); else StartTest(); });
            btnTest.Kind = FxKind.Primary;
            var copy = Ui.Button("copy", "Diagnose kopieren", (s, e) => CopyDiagnostics());
            lblTestInfo = new Label { AutoSize = true, ForeColor = Theme.TextDim, Margin = new Padding(Px(10), Px(9), 0, 0), Tag = "keep" };
            top.Controls.Add(btnTest);
            top.Controls.Add(copy);
            top.Controls.Add(lblTestInfo);
            meterScroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Panel2 };
            Theme.DarkScroll(meterScroll);
            meter = new MeterPanel { Dock = DockStyle.Top, Height = Px(60) };
            meter.Hint = "„Test starten“ nimmt nur den Ton auf (ohne Video, ohne Speichern) und zeigt hier pro Spur, ob etwas ankommt. Läuft der Replay-Buffer, siehst du die Pegel live.";
            meterScroll.Controls.Add(meter);
            g.Controls.Add(meterScroll);
            g.Controls.Add(top);

            meterTimer = new System.Windows.Forms.Timer { Interval = 50 };
            meterTimer.Tick += (s, e) => UpdateMeters();
            meterTimer.Start();
            return g;
        }

        void StartTest()
        {
            if (host.Recorder.IsActive || testEngine != null) return;
            try
            {
                var cfg = settings.Clone();
                testEngine = new AudioEngine(cfg.Tracks, null);
                testEngine.Start();
                testEngine.StartClock();
            }
            catch (Exception ex)
            {
                testEngine = null;
                FxDialog.Info(this, "Test fehlgeschlagen", "Test konnte nicht gestartet werden: " + ex.Message, "error");
            }
            testRestartAt = DateTime.MaxValue;
            UpdateMeters();
        }

        void StopTest()
        {
            var e = testEngine;
            testEngine = null;
            testRestartAt = DateTime.MaxValue;
            if (e != null) ThreadPool.QueueUserWorkItem(_ => { try { e.Stop(); } catch { } });
            if (meter != null) UpdateMeters();
        }

        void UpdateMeters()
        {
            if (meter == null) return;
            if (testEngine != null && DateTime.UtcNow >= testRestartAt)
            {
                StopTest();
                StartTest();
            }
            if (!Visible || current != pageAudio) return;
            List<TrackStatus> data = null;
            if (host.Recorder.IsActive)
            {
                data = host.Recorder.GetAudioStatus();
                btnTest.Enabled = false;
                btnTest.Text = "Test starten";
                lblTestInfo.Text = "Replay-Buffer läuft – Pegel live aus der Aufnahme.";
            }
            else
            {
                btnTest.Enabled = true;
                if (testEngine != null)
                {
                    data = testEngine.GetStatus();
                    btnTest.Text = "Test stoppen";
                    btnTest.IconName = "stop";
                    btnTest.Kind = FxKind.Danger;
                    lblTestInfo.Text = "Test läuft – sprich ins Mikro oder spiel etwas ab. Änderungen an Spuren gelten sofort.";
                }
                else
                {
                    btnTest.Text = "Test starten";
                    btnTest.IconName = "play";
                    btnTest.Kind = FxKind.Primary;
                    lblTestInfo.Text = "";
                }
            }
            meter.Data = data;
            int h = Math.Max(meter.NeededHeight(), meterScroll.ClientSize.Height);
            if (meter.Height != h) meter.Height = h;
            meter.Invalidate();
        }

        void CopyDiagnostics()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("ReplayClip " + Program.Version + " – Diagnose " + DateTime.Now.ToString("g"));
            sb.AppendLine("Windows: " + Environment.OSVersion.VersionString + (Environment.Is64BitOperatingSystem ? " (64 Bit)" : ""));
            sb.AppendLine("FFmpeg: " + (Ffmpeg.Find(settings) ?? "nicht gefunden"));
            sb.AppendLine("Mikrofon-Datenschutz blockiert: " + (Native.MicPrivacyBlocked() ? "JA" : "nein"));
            sb.AppendLine("Buffer aktiv: " + (host.Recorder.IsActive ? "ja" : "nein") + ", Test aktiv: " + (testEngine != null ? "ja" : "nein"));
            sb.AppendLine("Encoder: Einstellung " + settings.Encoder + ", benutzt " + (host.Recorder.EncoderLabel.Length > 0 ? host.Recorder.EncoderLabel : (Encoders.Last.HasValue ? Encoders.Label(Encoders.Last.Value) : "?")));
            sb.AppendLine("Puffer nach Speichern leeren: " + (settings.ResetBufferAfterSave ? "ja" : "nein"));
            var err = host.Recorder.AudioError;
            if (!string.IsNullOrEmpty(err)) sb.AppendLine("Letzter Audiofehler: " + err);
            var data = host.Recorder.IsActive ? host.Recorder.GetAudioStatus() : (testEngine != null ? testEngine.GetStatus() : null);
            for (int i = 0; i < settings.Tracks.Count; i++)
            {
                var t = settings.Tracks[i];
                sb.AppendFormat("Spur {0}: {1} [{2}] Apps={3} Gerät={4} Lautstärke={5}%", i + 1, t.Name, TrackConfig.TypeLabel(t.Type),
                    string.Join(",", t.Apps.ToArray()), string.IsNullOrEmpty(t.DeviceId) ? "Standard" : t.DeviceId, t.Volume).AppendLine();
            }
            if (data == null) sb.AppendLine("(Kein Test/Buffer aktiv – für Pegeldaten erst „Test starten“.)");
            else
            {
                foreach (var t in data)
                {
                    double db = t.Level > 0.000001f ? 20 * Math.Log10(t.Level) : -99;
                    sb.AppendFormat("== Spur {0} {1}: Pegel {2:0} dB – {3}", t.Index + 1, t.Name, db, t.Summary).AppendLine();
                    foreach (var so in t.Sources)
                        sb.AppendLine("   - " + so.Description + " | " + so.State + (string.IsNullOrEmpty(so.Format) ? "" : " | " + so.Format));
                }
            }
            try { Clipboard.SetText(sb.ToString()); lblStatus.Text = "Diagnose in die Zwischenablage kopiert."; lblStatus.ForeColor = Theme.Accent; }
            catch { }
        }

        void UpdateFfmpegState()
        {
            string f = Ffmpeg.Find(settings);
            txtFfmpeg.Text = f ?? "";
            lblFfmpegState.Text = f != null
                ? "Gefunden. Aufnahme nutzt ddagrab (Bildschirm per GPU) + den Encoder deiner Grafikkarte – die CPU bleibt fast unbelastet."
                : "Nicht gefunden. Klicke auf „Herunterladen“ (ca. 200 MB, offizieller Windows-Build von GitHub/BtbN) oder wähle eine vorhandene ffmpeg.exe (mind. Version 6).";
            lblFfmpegState.ForeColor = f != null ? Theme.TextDim : Theme.Danger;
        }

        void PickFolder(TextBox box, bool save)
        {
            using (var d = new FolderBrowserDialog())
            {
                d.SelectedPath = box.Text;
                d.ShowNewFolderButton = true;
                if (d.ShowDialog(this) != DialogResult.OK) return;
                box.Text = d.SelectedPath;
                if (save) { settings.SaveDir = d.SelectedPath; settings.Save(); host.LibraryChanged(); }
                else { settings.BufferDir = d.SelectedPath; MarkRestartNeeded(); settings.Save(); }
            }
        }

        void PickFfmpeg()
        {
            using (var d = new OpenFileDialog { Filter = "ffmpeg.exe|ffmpeg.exe|Programme (*.exe)|*.exe", Title = "ffmpeg.exe auswählen" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                settings.FfmpegPath = d.FileName;
                settings.Save();
                UpdateFfmpegState();
                host.FfmpegReady();
            }
        }

        void DownloadFfmpeg()
        {
            if (!btnDownload.Enabled) return;
            btnDownload.Enabled = false;
            dlProgress.Visible = true;
            dlProgress.Value = 0;
            lblFfmpegState.ForeColor = Theme.TextDim;
            lblFfmpegState.Text = "Lade FFmpeg herunter …";
            string zip = Path.Combine(Path.GetTempPath(), "replayclip_ffmpeg.zip");
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                var wc = new WebClient();
                wc.Headers.Add("User-Agent", "ReplayClip");
                wc.DownloadProgressChanged += (s, e) =>
                {
                    dlProgress.Value = Math.Max(0, Math.Min(100, e.ProgressPercentage));
                    lblFfmpegState.Text = string.Format(CultureInfo.CurrentCulture, "Lade FFmpeg herunter … {0:0} / {1:0} MB", e.BytesReceived / 1048576.0, Math.Max(e.TotalBytesToReceive, 0) / 1048576.0);
                };
                wc.DownloadFileCompleted += (s, e) =>
                {
                    wc.Dispose();
                    if (e.Error != null)
                    {
                        DownloadDone(null, "Download fehlgeschlagen: " + e.Error.Message);
                        return;
                    }
                    lblFfmpegState.Text = "Entpacke …";
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        string result = null, error = null;
                        try { result = ExtractFfmpeg(zip); }
                        catch (Exception ex) { error = "Entpacken fehlgeschlagen: " + ex.Message; }
                        try { File.Delete(zip); } catch { }
                        OnUi(() => DownloadDone(result, error));
                    });
                };
                wc.DownloadFileAsync(new Uri(FfmpegZipUrl), zip);
            }
            catch (Exception ex)
            {
                DownloadDone(null, "Download fehlgeschlagen: " + ex.Message);
            }
        }

        static string ExtractFfmpeg(string zip)
        {
            // Windows 10/11 bringen tar.exe (bsdtar) mit – das entpackt auch ZIP-Dateien.
            string targetDir = Path.Combine(Settings.AppDataDir, "ffmpeg");
            string tmp = Path.Combine(Path.GetTempPath(), "replayclip_ffmpeg_x");
            try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }
            Directory.CreateDirectory(tmp);
            Directory.CreateDirectory(targetDir);
            string tar = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "tar.exe");
            if (!File.Exists(tar)) tar = "tar.exe";
            try
            {
                foreach (var extra in new[] { " --include=*bin/ffmpeg.exe", "" })
                {
                    var psi = new ProcessStartInfo(tar, "-xf \"" + zip + "\" -C \"" + tmp + "\"" + extra);
                    psi.UseShellExecute = false;
                    psi.CreateNoWindow = true;
                    using (var p = Process.Start(psi)) p.WaitForExit(300000);
                    var found = Directory.GetFiles(tmp, "ffmpeg.exe", SearchOption.AllDirectories);
                    if (found.Length > 0)
                    {
                        string target = Path.Combine(targetDir, "ffmpeg.exe");
                        File.Copy(found[0], target, true);
                        return target;
                    }
                }
            }
            finally
            {
                try { Directory.Delete(tmp, true); } catch { }
            }
            throw new Exception("ffmpeg.exe nicht im Archiv gefunden.");
        }

        void DownloadDone(string path, string error)
        {
            btnDownload.Enabled = true;
            dlProgress.Visible = false;
            if (path != null)
            {
                settings.FfmpegPath = path;
                settings.Save();
                UpdateFfmpegState();
                host.FfmpegReady();
            }
            else
            {
                UpdateFfmpegState();
                lblFfmpegState.Text = error + "\nAlternative: in einer Konsole „winget install Gyan.FFmpeg“ ausführen und ReplayClip neu starten.";
                lblFfmpegState.ForeColor = Theme.Danger;
            }
        }

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        static bool GetAutostart()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return k != null && k.GetValue("ReplayClip") != null;
            }
            catch { return false; }
        }

        void SetAutostart(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (on) k.SetValue("ReplayClip", "\"" + Application.ExecutablePath + "\" --minimized");
                    else k.DeleteValue("ReplayClip", false);
                }
            }
            catch (Exception ex) { FxDialog.Info(this, "Autostart", "Autostart konnte nicht gesetzt werden: " + ex.Message, "error"); }
        }
    }
}
