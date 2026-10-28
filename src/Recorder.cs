using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace ReplayClip
{
    public static class Ffmpeg
    {
        public static string Find(Settings s)
        {
            var candidates = new List<string>();
            if (!string.IsNullOrEmpty(s.FfmpegPath)) candidates.Add(s.FfmpegPath);
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            candidates.Add(Path.Combine(exeDir, "ffmpeg.exe"));
            candidates.Add(Path.Combine(exeDir, "ffmpeg", "ffmpeg.exe"));
            candidates.Add(Path.Combine(exeDir, "ffmpeg", "bin", "ffmpeg.exe"));
            candidates.Add(Path.Combine(Settings.AppDataDir, "ffmpeg", "ffmpeg.exe"));
            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in path.Split(';'))
            {
                if (dir.Trim().Length == 0) continue;
                try { candidates.Add(Path.Combine(dir.Trim().Trim('"'), "ffmpeg.exe")); } catch { }
            }
            foreach (var c in candidates)
            {
                try { if (File.Exists(c)) return c; } catch { }
            }
            return null;
        }

        public static string Q(string path) { return "\"" + path + "\""; }

        /// <summary>Kurzer Prozesslauf mit gesammelter Fehlerausgabe.</summary>
        public static Process Launch(string exe, string args, bool stdin, StringBuilder errOut, bool highPriority = false, bool stdout = false)
        {
            var psi = new ProcessStartInfo(exe, args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardError = true;
            psi.RedirectStandardInput = stdin;
            psi.RedirectStandardOutput = stdout;
            psi.StandardErrorEncoding = Encoding.UTF8;
            var p = new Process();
            p.StartInfo = psi;
            p.ErrorDataReceived += (s, e) =>
            {
                if (e.Data == null || errOut == null) return;
                lock (errOut)
                {
                    if (errOut.Length > 8000) errOut.Remove(0, 4000);
                    errOut.AppendLine(e.Data);
                }
            };
            p.Start();
            ChildJob.Add(p); // FFmpeg endet automatisch mit ReplayClip (auch bei "Task beenden")
            p.BeginErrorReadLine();
            try { p.PriorityClass = highPriority ? ProcessPriorityClass.AboveNormal : ProcessPriorityClass.BelowNormal; } catch { }
            return p;
        }
    }

    /// <summary>Replay-Buffer: FFmpeg (ddagrab + NVENC) schreibt 2-s-Segmente auf die Platte, ältere werden gelöscht.</summary>
    public sealed class Recorder
    {
        const int SegmentSeconds = 2;
        readonly object sync = new object();
        Settings cfg;
        Func<Settings> liveSettings;
        AudioEngine engine;
        Process ff;
        StringBuilder ffErr;
        string sessionDir;
        string ffPath;
        volatile bool active;
        volatile bool stopping;
        int saving;
        Thread monitor, cleaner;
        int failCount;
        DateTime lastStart;
        public DateTime ActiveSince;          // Beginn des aktuell gepufferten Bereichs
        volatile int minSegment;              // ältere Segmente gehören zu einem schon gespeicherten Clip
        public double LastClipSeconds;

        public event Action<bool, string> StateChanged;   // aktiv?, Meldung
        public event Action<string> ClipSaved;
        public event Action<string> SaveFailed;
        public event Action SaveStarted;

        public Recorder(Func<Settings> live) { liveSettings = live; }

        public bool IsActive { get { return active; } }
        public bool IsSaving { get { return saving != 0; } }
        public string AudioStatus { get { var e = engine; return e == null ? "" : e.SourceStatus; } }
        public string AudioError { get { var e = engine; return e == null ? null : e.LastError; } }
        public List<TrackStatus> GetAudioStatus() { var e = engine; return e == null ? null : e.GetStatus(); }

        void Raise(bool on, string msg)
        {
            var h = StateChanged;
            if (h != null) h(on, msg);
        }

        public void Start(Settings s)
        {
            lock (sync)
            {
                if (active) return;
                failCount = 0;
                try
                {
                    StartInternal(s.Clone());
                    Raise(true, "Replay-Buffer läuft");
                }
                catch (Exception ex)
                {
                    TearDown();
                    Raise(false, ex.Message);
                }
            }
        }

        void StartInternal(Settings s)
        {
            cfg = s;
            ffPath = Ffmpeg.Find(cfg);
            if (ffPath == null) throw new Exception("FFmpeg nicht gefunden – bitte unter Einstellungen herunterladen oder Pfad angeben.");

            Directory.CreateDirectory(cfg.BufferDir);
            CleanOldSessions();
            sessionDir = Path.Combine(cfg.BufferDir, "session_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff"));
            Directory.CreateDirectory(sessionDir);

            engine = new AudioEngine(cfg.Tracks, "ReplayClip_" + Process.GetCurrentProcess().Id + "_" + Environment.TickCount);
            engine.Start();
            long startUs = engine.StartClock();

            ffErr = new StringBuilder();
            ff = Ffmpeg.Launch(ffPath, BuildArgs(startUs), true, ffErr, true);
            stopping = false;
            active = true;
            lastStart = DateTime.UtcNow;
            ActiveSince = DateTime.Now;
            minSegment = 0;

            var proc = ff;
            monitor = new Thread(() => MonitorLoop(proc));
            monitor.IsBackground = true;
            monitor.Start();
            if (cleaner == null || !cleaner.IsAlive)
            {
                cleaner = new Thread(CleanLoop);
                cleaner.IsBackground = true;
                cleaner.Start();
            }
        }

        string BuildArgs(long startUs)
        {
            var ci = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.Append("-hide_banner -loglevel warning -y ");
            for (int i = 0; i < engine.TrackCount; i++)
                sb.Append("-f f32le -ar 48000 -ac 2 -i ").Append(engine.PipePath(i)).Append(' ');

            // Bildzeitstempel an die Audio-Uhr koppeln: erster Frame bekommt seinen echten Aufnahmezeitpunkt
            // relativ zum Audio-Start (startUs). Zusätzlich Ausgleich der Audio-Latenz + Benutzer-Versatz.
            long offUs = AudioSource.LatencyMs * 1000L - cfg.AudioOffsetMs * 1000L;
            string off = offUs >= 0 ? "+" + offUs.ToString(ci) : "-" + (-offUs).ToString(ci);
            string setpts = "setpts='PTS+(if(eq(N,0),st(0,RTCTIME-T*1000000),ld(0))-" + startUs.ToString(ci) + off + ")/(TB*1000000)'";
            sb.Append("-filter_complex \"ddagrab=output_idx=").Append(cfg.MonitorIndex)
              .Append(":framerate=").Append(cfg.Fps)
              .Append(":draw_mouse=").Append(cfg.DrawMouse ? 1 : 0)
              .Append(',').Append(setpts).Append("[v]\" ");
            sb.Append("-map \"[v]\" ");
            for (int i = 0; i < engine.TrackCount; i++) sb.Append("-map ").Append(i).Append(":a ");

            int b = Math.Max(2, cfg.BitrateMbps);
            string enc = cfg.Codec == "hevc" ? "hevc_nvenc" : "h264_nvenc";
            sb.Append("-c:v ").Append(enc).Append(" -preset p4 -tune hq -rc vbr ");
            sb.AppendFormat(ci, "-b:v {0}M -maxrate {1}M -bufsize {2}M ", b, b * 3 / 2, b * 2);
            sb.Append("-g ").Append(cfg.Fps * SegmentSeconds).Append(' ');
            if (cfg.Codec != "hevc") sb.Append("-profile:v high ");
            if (engine.TrackCount > 0) sb.Append("-c:a aac -b:a ").Append(cfg.AudioBitrateK).Append("k ");
            sb.Append("-f segment -segment_time ").Append(SegmentSeconds)
              .Append(" -segment_format mpegts -segment_start_number 0 -reset_timestamps 0 ")
              .Append(Ffmpeg.Q(Path.Combine(sessionDir, "seg_%08d.ts")));
            return sb.ToString();
        }

        public void Stop()
        {
            lock (sync)
            {
                if (!active) return;
                stopping = true;
                TearDown();
                Raise(false, "Replay-Buffer gestoppt");
            }
        }

        void TearDown()
        {
            active = false;
            var p = ff;
            try { if (p != null && !p.HasExited) { p.StandardInput.Write("q"); p.StandardInput.Flush(); } } catch { }
            if (engine != null) { engine.Stop(); engine = null; }
            try { if (p != null && !p.WaitForExit(5000)) p.Kill(); } catch { }
            ff = null;
            string dir = sessionDir;
            sessionDir = null;
            if (dir != null && saving == 0)
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        void MonitorLoop(Process p)
        {
            try { p.WaitForExit(); } catch { return; }
            lock (sync)
            {
                if (stopping || ff != p) return;
                string err;
                lock (ffErr) err = LastLines(ffErr.ToString(), 4);
                TearDown();
                bool quickFail = (DateTime.UtcNow - lastStart).TotalSeconds < 20;
                failCount = quickFail ? failCount + 1 : 1;
                if (failCount <= 3)
                {
                    // z.B. Auflösungswechsel im Spiel -> ddagrab bricht ab -> automatisch neu starten
                    Raise(false, "FFmpeg beendet, starte neu …" + (err.Length > 0 ? "\n" + err : ""));
                    Thread.Sleep(1500);
                    try
                    {
                        StartInternal(cfg);
                        Raise(true, "Replay-Buffer läuft (neu gestartet)");
                    }
                    catch (Exception ex) { TearDown(); Raise(false, ex.Message); }
                }
                else
                {
                    Raise(false, "FFmpeg-Fehler:\n" + (err.Length > 0 ? err : "unbekannt"));
                }
            }
        }

        static string LastLines(string s, int n)
        {
            var lines = s.Replace("\r", "").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            int from = Math.Max(0, lines.Length - n);
            var r = new List<string>();
            for (int i = from; i < lines.Length; i++) r.Add(lines[i]);
            return string.Join("\n", r.ToArray());
        }

        void CleanOldSessions()
        {
            try
            {
                foreach (var d in Directory.GetDirectories(cfg.BufferDir, "session_*"))
                {
                    try { Directory.Delete(d, true); } catch { }
                }
            }
            catch { }
        }

        static List<int> SegmentIndices(string dir)
        {
            var list = new List<int>();
            if (dir == null || !Directory.Exists(dir)) return list;
            foreach (var f in Directory.GetFiles(dir, "seg_*.ts"))
            {
                string n = Path.GetFileNameWithoutExtension(f).Substring(4);
                int i;
                if (int.TryParse(n, out i)) list.Add(i);
            }
            list.Sort();
            return list;
        }

        static string SegPath(string dir, int i) { return Path.Combine(dir, "seg_" + i.ToString("D8") + ".ts"); }

        int NeededSegments
        {
            get
            {
                var s = liveSettings();
                return (int)Math.Ceiling(s.BufferMinutes * 60.0 / SegmentSeconds);
            }
        }

        void CleanLoop()
        {
            while (true)
            {
                Thread.Sleep(3000);
                if (!active) continue;
                if (saving != 0) continue;
                string dir = sessionDir;
                try
                {
                    var idx = SegmentIndices(dir);
                    if (idx.Count == 0) continue;
                    int keepFrom = Math.Max(idx[idx.Count - 1] - NeededSegments - 3, minSegment);
                    foreach (int i in idx)
                    {
                        if (i >= keepFrom) break;
                        try { File.Delete(SegPath(dir, i)); } catch { }
                    }
                }
                catch { }
            }
        }

        // ---------------- Clip speichern ----------------

        public void SaveClip()
        {
            if (!active)
            {
                var h = SaveFailed;
                if (h != null) h("Replay-Buffer ist nicht aktiv.");
                return;
            }
            if (Interlocked.CompareExchange(ref saving, 1, 0) != 0) return;
            string app = Native.ForegroundAppName();
            DateTime when = DateTime.Now;
            string dir = sessionDir;
            var s = liveSettings();
            var codec = cfg.Codec;
            var trackNames = new List<string>();
            foreach (var t in cfg.Tracks) trackNames.Add(t.Name);
            var started = SaveStarted;
            if (started != null) started();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    string file = DoSave(dir, app, when, s, codec, trackNames);
                    var h = ClipSaved;
                    if (h != null) h(file);
                }
                catch (Exception ex)
                {
                    var h = SaveFailed;
                    if (h != null) h(ex.Message);
                }
                finally { Interlocked.Exchange(ref saving, 0); }
            });
        }

        string DoSave(string dir, string app, DateTime when, Settings s, string codec, List<string> trackNames)
        {
            var idx = SegmentIndices(dir);
            if (idx.Count == 0) throw new Exception("Noch keine Aufnahme im Puffer.");
            int current = idx[idx.Count - 1];
            // Warten, bis das gerade geschriebene Segment fertig ist (max. ~5 s), damit die letzten Sekunden drin sind
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 5000 && !File.Exists(SegPath(dir, current + 1)) && active)
                Thread.Sleep(100);
            int last = File.Exists(SegPath(dir, current + 1)) ? current : current - 1;
            int need = (int)Math.Ceiling(s.BufferMinutes * 60.0 / SegmentSeconds);
            var files = new List<string>();
            int from = Math.Max(Math.Max(0, last - need + 1), minSegment);
            for (int i = from; i <= last; i++)
            {
                string p = SegPath(dir, i);
                if (File.Exists(p)) files.Add(p);
            }
            if (files.Count == 0)
                throw new Exception(minSegment > 0 ? "Seit dem letzten Clip wurde noch nichts Neues aufgenommen." : "Noch keine fertigen Segmente im Puffer.");

            string safeApp = Native.SafeFileName(app);
            string folder = Library.FolderFor(s, safeApp, when);
            Directory.CreateDirectory(folder);
            string file = Path.Combine(folder, safeApp + " " + when.ToString("yyyy.MM.dd - HH.mm.ss", CultureInfo.InvariantCulture) + ".mp4");
            int dup = 2;
            while (File.Exists(file))
                file = Path.Combine(folder, safeApp + " " + when.ToString("yyyy.MM.dd - HH.mm.ss", CultureInfo.InvariantCulture) + " (" + dup++ + ").mp4");

            var args = new StringBuilder("-hide_banner -loglevel error -y -f mpegts -i pipe:0 -map 0:v:0 -map 0:a? -c copy ");
            if (codec == "hevc") args.Append("-tag:v hvc1 ");
            for (int i = 0; i < trackNames.Count; i++)
            {
                string n = (trackNames[i] ?? "").Replace("\"", "'");
                args.AppendFormat("-metadata:s:a:{0} title=\"{1}\" -metadata:s:a:{0} handler_name=\"{1}\" ", i, n);
            }
            args.Append("-movflags +faststart ").Append(Ffmpeg.Q(file));

            var err = new StringBuilder();
            var p2 = Ffmpeg.Launch(ffPath, args.ToString(), true, err);
            try
            {
                var stdin = p2.StandardInput.BaseStream;
                var buffer = new byte[1 << 20];
                foreach (var f in files)
                {
                    using (var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        int r;
                        while ((r = fs.Read(buffer, 0, buffer.Length)) > 0) stdin.Write(buffer, 0, r);
                    }
                }
                stdin.Close();
            }
            catch (IOException) { /* FFmpeg hat abgebrochen – Fehler unten */ }
            if (!p2.WaitForExit(180000)) { try { p2.Kill(); } catch { } throw new Exception("Speichern dauerte zu lange."); }
            p2.WaitForExit();
            if (p2.ExitCode != 0 || !File.Exists(file) || new FileInfo(file).Length < 1024)
            {
                string e;
                lock (err) e = LastLines(err.ToString(), 3);
                try { if (File.Exists(file)) File.Delete(file); } catch { }
                throw new Exception("FFmpeg konnte den Clip nicht schreiben. " + e);
            }
            var meta = Library.Get(file);
            meta.App = safeApp;
            Library.SaveSoon();
            LastClipSeconds = files.Count * SegmentSeconds;
            if (s.ResetBufferAfterSave && dir == sessionDir && active)
            {
                // Puffer "leeren": der nächste Clip beginnt nach diesem – alte Stücke räumt CleanLoop weg
                minSegment = last + 1;
                ActiveSince = DateTime.Now;
            }
            return file;
        }
    }

    /// <summary>Erzeugt Vorschaubilder für die Galerie (nacheinander, im Hintergrund).</summary>
    public static class Thumbnailer
    {
        static readonly Queue<KeyValuePair<string, Action<string, string>>> queue = new Queue<KeyValuePair<string, Action<string, string>>>();
        static Thread worker;
        public static Func<string> FfmpegPath;

        public static string CacheDir { get { return Path.Combine(Settings.AppDataDir, "Vorschau"); } }

        public static string ThumbPath(string video)
        {
            var fi = new FileInfo(video);
            string key = video.ToLowerInvariant() + "|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks;
            using (var md5 = System.Security.Cryptography.MD5.Create())
            {
                var h = md5.ComputeHash(Encoding.UTF8.GetBytes(key));
                return Path.Combine(CacheDir, BitConverter.ToString(h).Replace("-", "") + ".jpg");
            }
        }

        public static void Request(string video, Action<string, string> done)
        {
            string tp;
            try { tp = ThumbPath(video); } catch { return; }
            if (File.Exists(tp)) { done(video, tp); return; }
            lock (queue)
            {
                queue.Enqueue(new KeyValuePair<string, Action<string, string>>(video, done));
                if (worker == null || !worker.IsAlive)
                {
                    worker = new Thread(Work);
                    worker.IsBackground = true;
                    worker.Priority = ThreadPriority.BelowNormal;
                    worker.Start();
                }
            }
        }

        static void Work()
        {
            while (true)
            {
                KeyValuePair<string, Action<string, string>> item;
                lock (queue)
                {
                    if (queue.Count == 0) { worker = null; return; }
                    item = queue.Dequeue();
                }
                try
                {
                    string exe = FfmpegPath != null ? FfmpegPath() : null;
                    if (exe == null || !File.Exists(item.Key)) continue;
                    string tp = ThumbPath(item.Key);
                    if (!File.Exists(tp))
                    {
                        Directory.CreateDirectory(CacheDir);
                        foreach (string ss in new[] { "1", "0" })
                        {
                            string args = "-hide_banner -loglevel error -y -ss " + ss + " -i " + Ffmpeg.Q(item.Key) +
                                          " -frames:v 1 -vf scale=320:-2 -q:v 5 " + Ffmpeg.Q(tp);
                            var p = Ffmpeg.Launch(exe, args, false, null);
                            if (!p.WaitForExit(20000)) { try { p.Kill(); } catch { } }
                            if (File.Exists(tp)) break;
                        }
                    }
                    if (File.Exists(tp)) item.Value(item.Key, tp);
                }
                catch { }
            }
        }
    }
}
