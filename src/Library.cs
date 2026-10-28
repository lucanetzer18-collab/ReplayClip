using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace ReplayClip
{
    /// <summary>Zusatzinfos zu einem Clip (Tags, Favorit, Dauer, Herkunft).</summary>
    public sealed class ClipMeta
    {
        public string Path;
        public bool Favorite;
        public readonly List<string> Tags = new List<string>();
        public double Duration;            // 0 = unbekannt
        public string CutFrom = "";        // Original, aus dem dieser Schnitt stammt
        public string App = "";            // Spiel/App beim Speichern

        public bool HasTag(string t)
        {
            foreach (var x in Tags) if (string.Equals(x, t, StringComparison.CurrentCultureIgnoreCase)) return true;
            return false;
        }
    }

    /// <summary>Ein Eintrag in der Bibliotheksansicht.</summary>
    public sealed class ClipItem
    {
        public string Path;
        public string Name;
        public long Size;
        public DateTime Date;
        public string Game;
        public ClipMeta Meta;
        /// <summary>Platzhalter für einen Schnitt, der gerade im Hintergrund gerendert wird.</summary>
        public bool Placeholder;
        /// <summary>Vorschaubild von einer anderen Datei nehmen (Platzhalter zeigt das Original).</summary>
        public string ThumbFrom;
        public bool IsCut { get { return Meta.CutFrom.Length > 0; } }
    }

    /// <summary>Speichert Metadaten aller Clips in %LOCALAPPDATA%\ReplayClip\bibliothek.txt.</summary>
    public static class Library
    {
        static readonly Dictionary<string, ClipMeta> map = new Dictionary<string, ClipMeta>(StringComparer.OrdinalIgnoreCase);
        static readonly object l = new object();
        static bool loaded;
        static Timer saveTimer;

        static string FilePath { get { return System.IO.Path.Combine(Settings.AppDataDir, "bibliothek.txt"); } }

        static void EnsureLoaded()
        {
            if (loaded) return;
            loaded = true;
            try
            {
                if (!File.Exists(FilePath)) return;
                foreach (var line in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    var p = line.Split('\t');
                    if (p.Length < 6 || p[0].Length == 0) continue;
                    var m = new ClipMeta { Path = p[0], Favorite = p[1] == "1" };
                    foreach (var t in p[2].Split('|')) if (t.Length > 0) m.Tags.Add(t);
                    double.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out m.Duration);
                    m.CutFrom = p[4];
                    m.App = p[5];
                    map[m.Path] = m;
                }
            }
            catch { }
        }

        public static ClipMeta Get(string path)
        {
            lock (l)
            {
                EnsureLoaded();
                ClipMeta m;
                if (!map.TryGetValue(path, out m))
                {
                    m = new ClipMeta { Path = path };
                    map[path] = m;
                }
                return m;
            }
        }

        public static ClipMeta Find(string path)
        {
            lock (l)
            {
                EnsureLoaded();
                ClipMeta m;
                return map.TryGetValue(path, out m) ? m : null;
            }
        }

        public static void Rename(string oldPath, string newPath)
        {
            lock (l)
            {
                EnsureLoaded();
                ClipMeta m;
                if (map.TryGetValue(oldPath, out m))
                {
                    map.Remove(oldPath);
                    m.Path = newPath;
                    map[newPath] = m;
                }
                foreach (var x in map.Values)
                    if (string.Equals(x.CutFrom, oldPath, StringComparison.OrdinalIgnoreCase)) x.CutFrom = newPath;
            }
            SaveSoon();
        }

        public static void Remove(string path)
        {
            lock (l) { EnsureLoaded(); map.Remove(path); }
            SaveSoon();
        }

        static string Clean(string s) { return (s ?? "").Replace("\t", " ").Replace("\r", " ").Replace("\n", " ").Replace("|", "/"); }

        public static void SaveSoon()
        {
            lock (l)
            {
                if (saveTimer == null) saveTimer = new Timer(_ => SaveNow(), null, 800, Timeout.Infinite);
                else saveTimer.Change(800, Timeout.Infinite);
            }
        }

        public static void SaveNow()
        {
            var sb = new StringBuilder();
            lock (l)
            {
                foreach (var m in map.Values)
                {
                    // Nur Einträge mit Inhalt speichern, Leiche (Datei weg) überspringen
                    if (!m.Favorite && m.Tags.Count == 0 && m.Duration <= 0 && m.CutFrom.Length == 0 && m.App.Length == 0) continue;
                    if (!File.Exists(m.Path)) continue;
                    var tags = new List<string>();
                    foreach (var t in m.Tags) tags.Add(Clean(t));
                    sb.Append(Clean(m.Path)).Append('\t').Append(m.Favorite ? "1" : "0").Append('\t')
                      .Append(string.Join("|", tags.ToArray())).Append('\t')
                      .Append(m.Duration.ToString("0.###", CultureInfo.InvariantCulture)).Append('\t')
                      .Append(Clean(m.CutFrom)).Append('\t').Append(Clean(m.App)).Append('\n');
                }
            }
            try
            {
                Directory.CreateDirectory(Settings.AppDataDir);
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), Encoding.UTF8);
                if (File.Exists(FilePath)) File.Delete(FilePath);
                File.Move(tmp, FilePath);
            }
            catch { }
        }

        /// <summary>Alle Clips unter dem Speicherordner einlesen.</summary>
        public static List<ClipItem> Scan(string root)
        {
            var list = new List<ClipItem>();
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return list;
            string[] files;
            try { files = Directory.GetFiles(root, "*.mp4", SearchOption.AllDirectories); }
            catch { return list; }
            foreach (var f in files)
            {
                string name = System.IO.Path.GetFileName(f);
                if (name.StartsWith("~replayclip_edit_")) continue;
                FileInfo fi;
                try { fi = new FileInfo(f); } catch { continue; }
                var m = Get(f);
                var it = new ClipItem
                {
                    Path = f,
                    Name = System.IO.Path.GetFileNameWithoutExtension(f),
                    Size = fi.Length,
                    Date = fi.LastWriteTime,
                    Meta = m
                };
                it.Game = m.App.Length > 0 ? m.App : GameFromPath(root, f);
                list.Add(it);
            }
            return list;
        }

        static readonly Regex Month = new Regex(@"^\d{4}-\d{2}$");

        public static string GameFromPath(string root, string file)
        {
            string rel = file.Substring(Math.Min(file.Length, root.TrimEnd('\\', '/').Length)).TrimStart('\\', '/');
            var parts = rel.Split('\\', '/');
            if (parts.Length <= 1) return "Sonstige";
            if (Month.IsMatch(parts[0])) return parts.Length > 2 ? parts[1] : "Sonstige";
            return parts[0];
        }

        /// <summary>Zielordner für einen neuen Clip je nach gewählter Ordnerstruktur.</summary>
        public static string FolderFor(Settings s, string safeApp, DateTime when)
        {
            string month = when.ToString("yyyy-MM", CultureInfo.InvariantCulture);
            switch (s.FolderStructure)
            {
                case 1: return System.IO.Path.Combine(System.IO.Path.Combine(s.SaveDir, safeApp), month);
                case 2: return System.IO.Path.Combine(System.IO.Path.Combine(s.SaveDir, month), safeApp);
                default: return System.IO.Path.Combine(s.SaveDir, safeApp);
            }
        }
    }
}
