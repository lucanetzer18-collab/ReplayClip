using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace ReplayClip
{
    public enum TrackType
    {
        Apps = 0,        // nur bestimmte Programme (z.B. Discord)
        Rest = 1,        // alles, was keiner App-Spur zugeordnet ist
        Microphone = 2,  // Mikrofon / Eingabegerät
        AllSystem = 3,   // gesamter PC-Sound
        Mix = 4          // Summe aller anderen Spuren
    }

    public sealed class TrackConfig
    {
        public string Name = "Spur";
        public TrackType Type = TrackType.Rest;
        public const string DefaultCommunications = "@comm";
        public string DeviceId = "";            // nur Mikrofon, leer = Standardgerät, "@comm" = Standard-Kommunikationsgerät
        public List<string> Apps = new List<string>();
        public int Volume = 100;                 // Prozent
        public bool Denoise;                     // Rauschunterdrückung (Tastatur, Lüfter) – v.a. fürs Mikrofon

        public TrackConfig Clone()
        {
            var t = (TrackConfig)MemberwiseClone();
            t.Apps = new List<string>(Apps);
            return t;
        }

        public static string TypeLabel(TrackType t)
        {
            switch (t)
            {
                case TrackType.Apps: return "Bestimmte Apps";
                case TrackType.Rest: return "Restlicher PC-Sound";
                case TrackType.Microphone: return "Mikrofon";
                case TrackType.AllSystem: return "Gesamter PC-Sound";
                case TrackType.Mix: return "Mix aller Spuren";
            }
            return t.ToString();
        }
    }

    public sealed class Settings
    {
        public int MonitorIndex = 0;
        public int Fps = 60;
        public string Codec = "h264";          // h264 | hevc
        public string Encoder = "auto";
        public int ControllerCombo = 0;
        public int DiscordLimitMB = 10;
        public bool HoverPreview = true;       // Bibliothek: Vorschau beim Überfahren        // Größenlimit für "Für Discord" (10 / 50 / 500)        // Controller-Kombination zum Speichern (0 = aus, siehe Gamepad.Names)        // auto | nvidia | amd | intel | cpu
        public int BitrateMbps = 30;
        public int BufferMinutes = 5;
        public bool DrawMouse = true;
        public int AudioBitrateK = 192;
        public int AudioOffsetMs = 0;          // positiv = Ton später

        public uint SaveHotkeyMods = Native.MOD_ALT;
        public int SaveHotkeyKey = 0x79;       // F10
        public uint ToggleHotkeyMods = Native.MOD_ALT;   // 2.0: Aufnahme starten/stoppen
        public int ToggleHotkeyKey = 0x78;     // F9

        public string SaveDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "ReplayClip");
        public string BufferDir = Path.Combine(AppDataDir, "Puffer");
        public string FfmpegPath = "";

        public bool StartBufferOnLaunch = true;
        public bool StartMinimized = false;
        public bool NotifySound = true;
        public bool NotifyBalloon = false;
        public bool ResetBufferAfterSave = true;

        // Aussehen
        public int AccentArgb = unchecked((int)0xFF966EFF);     // 2.0: Violett
        public int SecondaryArgb = unchecked((int)0xFFFF4D8D);  // 2.0: Pink
        public int SettingsVersion = 2;

        // ---- Modi (2.0) ----
        public int Mode = 0;                   // 0 = Rewind (Replay-Puffer), 1 = Aufnahme (dauerhaft)
        public int LibView = 0;                // Bibliothek zeigt: 0 = Clips, 1 = Aufnahmen, 2 = Screenshots
        public bool OnboardingDone = false;    // Kurzanleitung beim ersten Start gezeigt
        public bool RewindPaused = false;      // Rewind-Puffer pausiert (Einstellungen)
        public string RecordDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "ReplayClip Aufnahmen");
        public int RecordStructure = 1;        // siehe Library.StructureNames
        public int RecordMinFreeGB = 5;        // Aufnahme stoppt automatisch, wenn weniger frei ist
        public string ShotDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "ReplayClip");
        public int ShotStructure = 1;
        public string ShotFormat = "png";      // png | jpg
        public int ShotQuality = 92;
        public bool ShotCopy = true;           // Screenshot auch in die Zwischenablage
        public bool ShotSound = true;
        public uint ShotHotkeyMods = Native.MOD_ALT;
        public int ShotHotkeyKey = 0x7B;       // F12
        public int FontChoice = 0;             // 0 Inter, 1 Segoe UI Variable, 2 Bahnschrift, 3 Segoe UI
        public int Animations = 0;             // 0 voll, 1 reduziert, 2 aus

        // Sound & Hinweise
        public string SaveSound = "pling";     // off, pling, double, camera, coin, bell, chime, pop, windows, file
        public string SaveSoundFile = "";
        public int SoundVolume = 70;
        public bool ErrorSound = true;
        public bool HotkeySound = true;
        // Updates (GitHub Releases)
        public bool UpdateCheck = true;
        public string UpdateRepo = Updater.DefaultRepo;
        public string SkipUpdate = "";
        public string LastUpdateCheck = "";    // Zeitpunkt der letzten erfolgreichen Suche (ISO)
        public string NotifiedUpdate = "";     // für diese Version wurde schon eine Windows-Mitteilung gezeigt        // tiefer Ton beim Drücken, Speicher-Sound danach höher
        public bool OverlayToast = true;
        public int OverlayPos = 0;             // 0 oben rechts, 1 oben links, 2 unten rechts, 3 unten links

        public List<TrackConfig> Tracks = new List<TrackConfig>();

        // Bibliothek
        public int FolderStructure = 0;        // 0 = App\Clip, 1 = App\Monat\Clip, 2 = Monat\App\Clip
        public int AfterExport = 0;            // 0 = fragen, 1 = Original behalten, 2 = Original in den Papierkorb
        public List<string> QuickTags = new List<string>(new[] { "Highlight", "Witzig", "Fail", "Clutch", "Ace", "Montage" });
        /// <summary>Selbst gewählte Tag-Farben (Tag -> ARGB). Fehlt ein Tag, gibt es eine automatische Farbe.</summary>
        public Dictionary<string, int> TagColors = new Dictionary<string, int>(StringComparer.CurrentCultureIgnoreCase);
        public bool GalleryList = false;
        public int TileSize = 1;               // 0 klein, 1 mittel, 2 groß
        public int GallerySort = 0;            // 0 neueste, 1 älteste, 2 Name, 3 Größe, 4 Dauer
        public bool ShowDetails = true;

        public static string FolderStructureLabel(int i)
        {
            switch (i)
            {
                case 1: return "Spiel/App \\ Monat \\ Clip";
                case 2: return "Monat \\ Spiel/App \\ Clip";
                default: return "Spiel/App \\ Clip";
            }
        }

        public static string AppDataDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReplayClip"); }
        }

        static string FilePath { get { return Path.Combine(AppDataDir, "settings.ini"); } }

        public static Settings CreateDefault()
        {
            var s = new Settings();
            var discord = new TrackConfig { Name = "Discord", Type = TrackType.Apps };
            discord.Apps.Add("discord");
            s.Tracks.Add(discord);
            s.Tracks.Add(new TrackConfig { Name = "PC-Sound", Type = TrackType.Rest });
            s.Tracks.Add(new TrackConfig { Name = "Mikrofon", Type = TrackType.Microphone });
            return s;
        }

        public Settings Clone()
        {
            var s = (Settings)MemberwiseClone();
            s.Tracks = new List<TrackConfig>();
            foreach (var t in Tracks) s.Tracks.Add(t.Clone());
            s.QuickTags = new List<string>(QuickTags);
            s.TagColors = new Dictionary<string, int>(TagColors, StringComparer.CurrentCultureIgnoreCase);
            return s;
        }

        // ---------------- einfache INI-Datei ----------------

        public void Save()
        {
            var sb = new StringBuilder();
            var ci = CultureInfo.InvariantCulture;
            Action<string, object> w = (k, v) => sb.AppendLine(k + "=" + Convert.ToString(v, ci));
            w("MonitorIndex", MonitorIndex);
            w("Fps", Fps);
            w("Codec", Codec);
            w("Encoder", Encoder);
            w("ControllerCombo", ControllerCombo);
            w("DiscordLimitMB", DiscordLimitMB);
            w("HoverPreview", HoverPreview);
            w("BitrateMbps", BitrateMbps);
            w("BufferMinutes", BufferMinutes);
            w("DrawMouse", DrawMouse);
            w("AudioBitrateK", AudioBitrateK);
            w("AudioOffsetMs", AudioOffsetMs);
            w("SaveHotkeyMods", SaveHotkeyMods);
            w("SaveHotkeyKey", SaveHotkeyKey);
            w("ToggleHotkeyMods", ToggleHotkeyMods);
            w("ToggleHotkeyKey", ToggleHotkeyKey);
            w("SaveDir", SaveDir);
            w("BufferDir", BufferDir);
            w("FfmpegPath", FfmpegPath);
            w("StartBufferOnLaunch", StartBufferOnLaunch);
            w("SettingsVersion", SettingsVersion);
            w("Mode", Mode);
            w("LibView", LibView);
            w("OnboardingDone", OnboardingDone);
            w("RewindPaused", RewindPaused);
            w("RecordDir", RecordDir);
            w("RecordStructure", RecordStructure);
            w("RecordMinFreeGB", RecordMinFreeGB);
            w("ShotDir", ShotDir);
            w("ShotStructure", ShotStructure);
            w("ShotFormat", ShotFormat);
            w("ShotQuality", ShotQuality);
            w("ShotCopy", ShotCopy);
            w("ShotSound", ShotSound);
            w("ShotHotkeyMods", ShotHotkeyMods);
            w("ShotHotkeyKey", ShotHotkeyKey);
            w("StartMinimized", StartMinimized);
            w("NotifySound", NotifySound);
            w("NotifyBalloon", NotifyBalloon);
            w("ResetBufferAfterSave", ResetBufferAfterSave);
            w("AccentArgb", AccentArgb);
            w("SecondaryArgb", SecondaryArgb);
            w("FontChoice", FontChoice);
            w("Animations", Animations);
            w("SaveSound", SaveSound);
            w("SaveSoundFile", SaveSoundFile);
            w("SoundVolume", SoundVolume);
            w("ErrorSound", ErrorSound);
            w("HotkeySound", HotkeySound);
            w("UpdateCheck", UpdateCheck);
            w("UpdateRepo", UpdateRepo);
            w("SkipUpdate", SkipUpdate);
            w("LastUpdateCheck", LastUpdateCheck);
            w("NotifiedUpdate", NotifiedUpdate);
            w("OverlayToast", OverlayToast);
            w("OverlayPos", OverlayPos);
            w("FolderStructure", FolderStructure);
            w("AfterExport", AfterExport);
            w("QuickTags", string.Join(";", QuickTags.ToArray()));
            var tc = new List<string>();
            foreach (var kv in TagColors) tc.Add(Uri.EscapeDataString(kv.Key) + ":" + kv.Value.ToString("X8"));
            w("TagColors", string.Join(";", tc.ToArray()));
            w("GalleryList", GalleryList);
            w("TileSize", TileSize);
            w("GallerySort", GallerySort);
            w("ShowDetails", ShowDetails);
            w("TrackCount", Tracks.Count);
            for (int i = 0; i < Tracks.Count; i++)
            {
                var t = Tracks[i];
                string p = "Track" + i + ".";
                w(p + "Name", t.Name);
                w(p + "Type", (int)t.Type);
                w(p + "DeviceId", t.DeviceId);
                w(p + "Apps", string.Join(";", t.Apps.ToArray()));
                w(p + "Volume", t.Volume);
                w(p + "Denoise", t.Denoise);
            }
            try
            {
                Directory.CreateDirectory(AppDataDir);
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), Encoding.UTF8);
                if (File.Exists(FilePath)) File.Delete(FilePath);
                File.Move(tmp, FilePath);
            }
            catch { }
        }

        public static Settings Load()
        {
            if (!File.Exists(FilePath)) return CreateDefault();
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var line in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    int i = line.IndexOf('=');
                    if (i > 0) d[line.Substring(0, i).Trim()] = line.Substring(i + 1);
                }
            }
            catch { return CreateDefault(); }

            var s = new Settings();
            s.MonitorIndex = GetInt(d, "MonitorIndex", s.MonitorIndex);
            s.Fps = GetInt(d, "Fps", s.Fps);
            s.Codec = GetStr(d, "Codec", s.Codec);
            s.Encoder = GetStr(d, "Encoder", "auto");
            s.ControllerCombo = GetInt(d, "ControllerCombo", 0);
            s.DiscordLimitMB = GetInt(d, "DiscordLimitMB", 10);
            s.HoverPreview = GetBool(d, "HoverPreview", true);
            if (Array.IndexOf(DiscordExport.Limits, s.DiscordLimitMB) < 0) s.DiscordLimitMB = 10;
            if (Array.IndexOf(Encoders.Prefs, s.Encoder) < 0) s.Encoder = "auto";
            s.BitrateMbps = GetInt(d, "BitrateMbps", s.BitrateMbps);
            s.BufferMinutes = Math.Max(1, Math.Min(20, GetInt(d, "BufferMinutes", s.BufferMinutes)));
            s.DrawMouse = GetBool(d, "DrawMouse", s.DrawMouse);
            s.AudioBitrateK = GetInt(d, "AudioBitrateK", s.AudioBitrateK);
            s.AudioOffsetMs = GetInt(d, "AudioOffsetMs", s.AudioOffsetMs);
            s.SaveHotkeyMods = (uint)GetInt(d, "SaveHotkeyMods", (int)s.SaveHotkeyMods);
            s.SaveHotkeyKey = GetInt(d, "SaveHotkeyKey", s.SaveHotkeyKey);
            s.ToggleHotkeyMods = (uint)GetInt(d, "ToggleHotkeyMods", (int)s.ToggleHotkeyMods);
            s.ToggleHotkeyKey = GetInt(d, "ToggleHotkeyKey", s.ToggleHotkeyKey);
            s.SaveDir = GetStr(d, "SaveDir", s.SaveDir);
            s.BufferDir = GetStr(d, "BufferDir", s.BufferDir);
            s.FfmpegPath = GetStr(d, "FfmpegPath", s.FfmpegPath);
            s.StartBufferOnLaunch = GetBool(d, "StartBufferOnLaunch", s.StartBufferOnLaunch);
            s.StartMinimized = GetBool(d, "StartMinimized", s.StartMinimized);
            s.NotifySound = GetBool(d, "NotifySound", s.NotifySound);
            s.NotifyBalloon = GetBool(d, "NotifyBalloon", s.NotifyBalloon);
            s.ResetBufferAfterSave = GetBool(d, "ResetBufferAfterSave", true);
            s.AccentArgb = GetInt(d, "AccentArgb", s.AccentArgb);
            s.SecondaryArgb = GetInt(d, "SecondaryArgb", s.SecondaryArgb);
            s.FontChoice = Math.Max(0, Math.Min(3, GetInt(d, "FontChoice", 0)));
            s.Animations = Math.Max(0, Math.Min(2, GetInt(d, "Animations", 0)));
            s.SaveSound = GetStr(d, "SaveSound", s.NotifySound ? "pling" : "off");
            s.SaveSoundFile = GetStr(d, "SaveSoundFile", "");
            s.SoundVolume = Math.Max(0, Math.Min(100, GetInt(d, "SoundVolume", 70)));
            s.ErrorSound = GetBool(d, "ErrorSound", true);
            s.HotkeySound = GetBool(d, "HotkeySound", true);
            s.UpdateCheck = GetBool(d, "UpdateCheck", true);
            s.UpdateRepo = GetStr(d, "UpdateRepo", Updater.DefaultRepo).Trim();
            // Platzhalter aus alten Versionen durch das fest eingebaute Repo ersetzen
            if (!Updater.RepoValid(s.UpdateRepo)) s.UpdateRepo = Updater.DefaultRepo;
            s.SkipUpdate = GetStr(d, "SkipUpdate", "");
            s.LastUpdateCheck = GetStr(d, "LastUpdateCheck", "");
            s.NotifiedUpdate = GetStr(d, "NotifiedUpdate", "");
            s.OverlayToast = GetBool(d, "OverlayToast", true);
            s.OverlayPos = Math.Max(0, Math.Min(3, GetInt(d, "OverlayPos", 0)));
            s.FolderStructure = Math.Max(0, Math.Min(2, GetInt(d, "FolderStructure", 0)));
            s.AfterExport = Math.Max(0, Math.Min(2, GetInt(d, "AfterExport", 0)));
            string qt;
            if (d.TryGetValue("QuickTags", out qt))
            {
                s.QuickTags = new List<string>();
                foreach (var t in qt.Split(';')) if (t.Trim().Length > 0) s.QuickTags.Add(t.Trim());
            }
            string tcs;
            if (d.TryGetValue("TagColors", out tcs))
                foreach (var part in tcs.Split(';'))
                {
                    int k = part.LastIndexOf(':');
                    int argb;
                    if (k > 0 && int.TryParse(part.Substring(k + 1), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out argb))
                        try { s.TagColors[Uri.UnescapeDataString(part.Substring(0, k))] = argb; } catch { }
                }
            s.GalleryList = GetBool(d, "GalleryList", false);
            s.TileSize = Math.Max(0, Math.Min(2, GetInt(d, "TileSize", 1)));
            s.GallerySort = Math.Max(0, Math.Min(4, GetInt(d, "GallerySort", 0)));
            s.ShowDetails = GetBool(d, "ShowDetails", true);
            int n = GetInt(d, "TrackCount", 0);
            if (!d.ContainsKey("TrackCount")) s.Tracks = CreateDefault().Tracks;
            for (int i = 0; i < n && i < 6; i++)
            {
                string p = "Track" + i + ".";
                var t = new TrackConfig();
                t.Name = GetStr(d, p + "Name", "Spur " + (i + 1));
                t.Type = (TrackType)GetInt(d, p + "Type", 1);
                t.DeviceId = GetStr(d, p + "DeviceId", "");
                foreach (var a in GetStr(d, p + "Apps", "").Split(';'))
                    if (a.Trim().Length > 0) t.Apps.Add(Native.ExeKey(a));
                t.Volume = Math.Max(0, Math.Min(500, GetInt(d, p + "Volume", 100)));
                t.Denoise = GetBool(d, p + "Denoise", false);
                s.Tracks.Add(t);
            }
            s.Mode = Math.Max(0, Math.Min(2, GetInt(d, "Mode", 0)));
            s.LibView = Math.Max(0, Math.Min(2, GetInt(d, "LibView", s.Mode)));
            if (s.Mode == 2) s.Mode = 0;           // 2.0-Screenshot-Modus gibt es nicht mehr (Screenshot ist ein eigener Knopf)
            s.OnboardingDone = GetBool(d, "OnboardingDone", false);
            s.RewindPaused = GetBool(d, "RewindPaused", false);
            s.RecordDir = GetStr(d, "RecordDir", s.RecordDir);
            s.RecordStructure = Math.Max(0, Math.Min(4, GetInt(d, "RecordStructure", 1)));
            s.RecordMinFreeGB = Math.Max(0, Math.Min(500, GetInt(d, "RecordMinFreeGB", 5)));
            s.ShotDir = GetStr(d, "ShotDir", s.ShotDir);
            s.ShotStructure = Math.Max(0, Math.Min(4, GetInt(d, "ShotStructure", 1)));
            s.ShotFormat = GetStr(d, "ShotFormat", "png") == "jpg" ? "jpg" : "png";
            s.ShotQuality = Math.Max(50, Math.Min(100, GetInt(d, "ShotQuality", 92)));
            s.ShotCopy = GetBool(d, "ShotCopy", true);
            s.ShotSound = GetBool(d, "ShotSound", true);
            s.ShotHotkeyMods = (uint)GetInt(d, "ShotHotkeyMods", (int)s.ShotHotkeyMods);
            s.ShotHotkeyKey = GetInt(d, "ShotHotkeyKey", s.ShotHotkeyKey);
            s.SettingsVersion = GetInt(d, "SettingsVersion", 1);
            if (s.SettingsVersion < 2)
            {
                // Umstieg auf 2.0: neue Farben (wenn noch die alten Standardfarben eingestellt waren),
                // das alte "Buffer an/aus"-Kürzel wird zum Aufnahme-Kürzel Alt+F9
                if (s.AccentArgb == unchecked((int)0xFF2ECC71) && s.SecondaryArgb == unchecked((int)0xFF4DA3FF))
                {
                    s.AccentArgb = unchecked((int)0xFF966EFF);
                    s.SecondaryArgb = unchecked((int)0xFFFF4D8D);
                }
                s.ToggleHotkeyMods = Native.MOD_ALT;
                s.ToggleHotkeyKey = 0x78; // F9
                s.SettingsVersion = 2;
            }
            return s;
        }

        static string GetStr(Dictionary<string, string> d, string k, string def)
        {
            string v;
            return d.TryGetValue(k, out v) ? v : def;
        }

        static int GetInt(Dictionary<string, string> d, string k, int def)
        {
            string v;
            long r;
            if (d.TryGetValue(k, out v) && long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out r)) return (int)r;
            return def;
        }

        static bool GetBool(Dictionary<string, string> d, string k, bool def)
        {
            string v;
            bool r;
            if (d.TryGetValue(k, out v) && bool.TryParse(v, out r)) return r;
            return def;
        }
    }
}
