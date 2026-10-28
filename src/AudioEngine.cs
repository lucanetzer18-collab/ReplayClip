using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;

namespace ReplayClip
{
    public sealed class SourceStatus
    {
        public string Description;
        public string State;
        public string Format;
        public float Level;
        public bool Problem;
    }

    public sealed class TrackStatus
    {
        public int Index;
        public string Name;
        public TrackType Type;
        public float Level;
        public string Summary;
        public bool Problem;
        public readonly List<SourceStatus> Sources = new List<SourceStatus>();
    }

    /// <summary>
    /// Nimmt alle Audioquellen auf, mischt sie pro Spur und schreibt je Spur einen
    /// lückenlosen 48-kHz-Stereo-Float-Strom in eine Named Pipe, die FFmpeg liest.
    /// Der Takt kommt von der Systemuhr (QPC) – damit bleibt der Ton synchron zum Bild.
    /// Ohne Pipe-Präfix läuft die Engine nur zum Testen (Pegelanzeige).
    /// </summary>
    public sealed class AudioEngine
    {
        public const int Rate = 48000;

        sealed class Track
        {
            public TrackConfig Cfg;
            public int Index;
            public string PipeName;
            public NamedPipeServerStream Pipe;
            public volatile bool Connected;
            public volatile bool Broken;
            public readonly object SrcLock = new object();
            public readonly object PipeLock = new object();
            public readonly List<AudioSource> Sources = new List<AudioSource>();
            public MemoryStream Pending = new MemoryStream();
            public float[] Mix = new float[0];
            public byte[] Bytes = new byte[0];
            public float Peak;
            public long PeakTicks;
            public volatile string Note = "";
            public volatile bool NoteProblem;
        }

        sealed class Active
        {
            public Track Track;
            public AudioSource Source;
            public WasapiCapture Capture;
        }

        sealed class Failure
        {
            public Track Track;
            public string Description;
            public string Error;
        }

        readonly List<Track> tracks = new List<Track>();
        readonly Dictionary<string, Active> active = new Dictionary<string, Active>();
        readonly Dictionary<string, DateTime> retryAfter = new Dictionary<string, DateTime>();
        readonly Dictionary<string, Failure> failures = new Dictionary<string, Failure>();
        readonly int ownPid = Process.GetCurrentProcess().Id;
        readonly bool withPipes;
        Thread mixer, watcher;
        volatile bool stop;
        Stopwatch clock;
        public volatile string LastError;
        public volatile string SourceStatus = "";
        volatile bool micBlocked;

        public AudioEngine(List<TrackConfig> cfgs, string pipePrefix)
        {
            withPipes = pipePrefix != null;
            for (int i = 0; i < cfgs.Count; i++)
            {
                var t = new Track();
                t.Cfg = cfgs[i];
                t.Index = i;
                t.PipeName = pipePrefix + "_" + i;
                tracks.Add(t);
            }
        }

        public int TrackCount { get { return tracks.Count; } }
        public string PipePath(int i) { return @"\\.\pipe\" + tracks[i].PipeName; }

        public void Start()
        {
            if (withPipes)
            {
                foreach (var t in tracks)
                {
                    t.Pipe = new NamedPipeServerStream(t.PipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.None, 0, 1 << 20);
                    var tr = t;
                    var th = new Thread(() => WaitConnect(tr));
                    th.IsBackground = true;
                    th.Start();
                }
            }
            watcher = new Thread(WatchLoop);
            watcher.IsBackground = true;
            watcher.Name = "Audio-Watcher";
            watcher.SetApartmentState(ApartmentState.MTA);
            watcher.Start();
        }

        /// <summary>Startet den Mischtakt und liefert den Startzeitpunkt (UTC µs) – Sample 0 aller Spuren.</summary>
        public long StartClock()
        {
            long startUs = Native.UtcNowMicros();
            clock = Stopwatch.StartNew();
            mixer = new Thread(MixLoop);
            mixer.IsBackground = true;
            mixer.Name = "Audio-Mixer";
            mixer.Priority = ThreadPriority.Highest;
            mixer.Start();
            return startUs;
        }

        public void Stop()
        {
            stop = true;
            foreach (var t in tracks)
            {
                try { if (t.Pipe != null) t.Pipe.Dispose(); } catch { }
            }
            if (mixer != null) mixer.Join(2000);
            if (watcher != null) watcher.Join(3000);
            lock (active)
            {
                foreach (var a in active.Values) a.Capture.Stop();
                active.Clear();
            }
        }

        void WaitConnect(Track t)
        {
            try
            {
                t.Pipe.WaitForConnection();
                lock (t.PipeLock)
                {
                    if (t.Pending.Length > 0) t.Pipe.Write(t.Pending.GetBuffer(), 0, (int)t.Pending.Length);
                    t.Pending = new MemoryStream();
                    t.Connected = true;
                }
            }
            catch (Exception ex)
            {
                if (!stop) { t.Broken = true; LastError = "Pipe: " + ex.Message; }
            }
        }

        // ---------------- Mischer ----------------

        void MixLoop()
        {
            try { Native.timeBeginPeriod(1); } catch { }
            try
            {
                long written = 0;
                while (!stop)
                {
                    long due = (long)(clock.ElapsedTicks * (double)Rate / Stopwatch.Frequency);
                    long diff = due - written;
                    if (diff < 240) { Thread.Sleep(3); continue; }
                    int frames = (int)Math.Min(diff, Rate);
                    MixChunk(frames, frames > Rate / 10);
                    written += frames;
                }
            }
            catch (Exception ex)
            {
                if (!stop) LastError = "Mixer: " + ex.Message;
            }
            finally { try { Native.timeEndPeriod(1); } catch { } }
        }

        static float Decay(float level, long fromTicks, long nowTicks)
        {
            double dt = (nowTicks - fromTicks) / (double)Stopwatch.Frequency;
            return (float)(level * Math.Pow(0.5, dt / 0.3));
        }

        void MixChunk(int frames, bool catchUp)
        {
            int n = frames * 2;
            foreach (var t in tracks)
            {
                if (t.Mix.Length < n) { t.Mix = new float[n]; t.Bytes = new byte[n * 4]; }
                Array.Clear(t.Mix, 0, n);
                if (t.Cfg.Type == TrackType.Mix) continue;
                float g = t.Cfg.Volume / 100f;
                lock (t.SrcLock)
                {
                    foreach (var s in t.Sources) s.MixInto(t.Mix, frames, g, catchUp);
                }
            }
            foreach (var t in tracks)
            {
                if (t.Cfg.Type != TrackType.Mix) continue;
                float g = t.Cfg.Volume / 100f;
                foreach (var o in tracks)
                {
                    if (o.Cfg.Type == TrackType.Mix) continue;
                    for (int i = 0; i < n; i++) t.Mix[i] += o.Mix[i] * g;
                }
            }
            long now = Stopwatch.GetTimestamp();
            foreach (var t in tracks)
            {
                var m = t.Mix;
                float pk = 0f;
                for (int i = 0; i < n; i++)
                {
                    float v = m[i];
                    // Weicher Begrenzer (wichtig bei Verstärkung bis 500 %): ab 0,8 sanft gegen 1 statt hart abschneiden
                    if (v > 0.8f) m[i] = v = 0.8f + 0.2f * (float)Math.Tanh((v - 0.8f) * 5f);
                    else if (v < -0.8f) m[i] = v = -0.8f - 0.2f * (float)Math.Tanh((-v - 0.8f) * 5f);
                    if (v < 0) v = -v;
                    if (v > pk) pk = v;
                }
                t.Peak = Math.Max(Decay(t.Peak, t.PeakTicks, now), pk);
                t.PeakTicks = now;
                if (withPipes)
                {
                    Buffer.BlockCopy(m, 0, t.Bytes, 0, n * 4);
                    Output(t, t.Bytes, n * 4);
                }
            }
        }

        void Output(Track t, byte[] data, int len)
        {
            if (t.Broken) return;
            lock (t.PipeLock)
            {
                if (t.Connected)
                {
                    try { t.Pipe.Write(data, 0, len); }
                    catch (Exception ex)
                    {
                        t.Broken = true;
                        if (!stop) LastError = "Pipe geschlossen: " + ex.Message;
                    }
                }
                else if (t.Pending.Length < 64L * 1024 * 1024)
                {
                    t.Pending.Write(data, 0, len);
                }
            }
        }

        // ---------------- Quellen verwalten ----------------

        void WatchLoop()
        {
            int n = 0;
            while (!stop)
            {
                try
                {
                    if (n++ % 5 == 0) micBlocked = Native.MicPrivacyBlocked();
                    Refresh();
                }
                catch (Exception ex) { LastError = "Quellen: " + ex.Message; }
                for (int i = 0; i < 20 && !stop; i++) Thread.Sleep(100);
            }
        }

        sealed class Spec
        {
            public Track Track;
            public bool IsDevice;
            public string DeviceId;
            public int Pid;
            public bool Include;
            public string Description;
        }

        static string ProcLabel(Dictionary<int, ProcInfo> procs, int pid)
        {
            ProcInfo p;
            return procs.TryGetValue(pid, out p) ? p.Name + ".exe" : "PID " + pid;
        }

        void Refresh()
        {
            var procs = Native.SnapshotProcesses();
            var assigned = new HashSet<string>();
            foreach (var t in tracks)
                if (t.Cfg.Type == TrackType.Apps)
                    foreach (var a in t.Cfg.Apps) assigned.Add(Native.ExeKey(a));

            var assignedRoots = new List<int>();
            foreach (var name in assigned)
                foreach (var r in Native.FindRoots(procs, name))
                    if (!assignedRoots.Contains(r)) assignedRoots.Add(r);

            var desired = new Dictionary<string, Spec>();
            List<int> sessionPids = null;

            foreach (var t in tracks)
            {
                string note = "";
                bool problem = false;
                switch (t.Cfg.Type)
                {
                    case TrackType.Microphone:
                        {
                            string id = t.Cfg.DeviceId ?? "";
                            if (id == "") id = CoreAudio.GetDefaultDeviceId(CoreAudio.eCapture, CoreAudio.eConsole);
                            else if (id == TrackConfig.DefaultCommunications) id = CoreAudio.GetDefaultDeviceId(CoreAudio.eCapture, CoreAudio.eCommunications);
                            if (!string.IsNullOrEmpty(id))
                            {
                                string key = "mic|" + t.Index + "|" + id;
                                Active existing;
                                string desc;
                                lock (active) desc = active.TryGetValue(key, out existing) ? existing.Capture.Description : null;
                                if (desc == null) desc = "Mikrofon: " + (CoreAudio.GetDeviceName(id) ?? id);
                                desired[key] = new Spec { Track = t, IsDevice = true, DeviceId = id, Description = desc };
                            }
                            else { note = "Kein Aufnahmegerät gefunden."; problem = true; }
                            if (micBlocked)
                            {
                                note = "Windows blockiert den Mikrofonzugriff für Desktop-Apps (Einstellungen › Datenschutz › Mikrofon).";
                                problem = true;
                            }
                            break;
                        }
                    case TrackType.Apps:
                        {
                            int found = 0;
                            foreach (var a in t.Cfg.Apps)
                            {
                                foreach (var root in Native.FindRoots(procs, Native.ExeKey(a)))
                                {
                                    found++;
                                    desired["inc|" + t.Index + "|" + root] = new Spec
                                    {
                                        Track = t, Pid = root, Include = true,
                                        Description = ProcLabel(procs, root) + " (PID " + root + ", inkl. Unterprozesse)"
                                    };
                                }
                            }
                            if (t.Cfg.Apps.Count == 0) { note = "Keine App zugeordnet."; problem = true; }
                            else if (found == 0) { note = "Läuft gerade nicht: " + string.Join(", ", t.Cfg.Apps.ToArray()) + " – wird automatisch erkannt, sobald gestartet."; problem = true; }
                            break;
                        }
                    case TrackType.AllSystem:
                        desired["exc|" + t.Index + "|" + ownPid] = new Spec { Track = t, Pid = ownPid, Include = false, Description = "Gesamter PC-Sound" };
                        break;
                    case TrackType.Rest:
                        if (assignedRoots.Count <= 1)
                        {
                            // Genau eine zugeordnete App (z.B. Discord): alles außer deren Prozessbaum
                            int ex = assignedRoots.Count == 1 ? assignedRoots[0] : ownPid;
                            string d = assignedRoots.Count == 1 ? "Alles außer " + ProcLabel(procs, ex) + " (PID " + ex + ")" : "Gesamter PC-Sound (keine App-Spur aktiv)";
                            desired["exc|" + t.Index + "|" + ex] = new Spec { Track = t, Pid = ex, Include = false, Description = d };
                        }
                        else
                        {
                            // Mehrere zugeordnete Apps: jede andere App mit Audio-Session einzeln einsammeln
                            if (sessionPids == null) sessionPids = CoreAudio.ListSessionPids();
                            var set = new HashSet<int>(sessionPids);
                            foreach (int pid in sessionPids)
                            {
                                if (pid == ownPid || !procs.ContainsKey(pid)) continue;
                                if (Native.IsInTree(procs, pid, assigned)) continue;
                                if (Native.HasAncestorIn(procs, pid, set)) continue;
                                desired["inc|" + t.Index + "|" + pid] = new Spec
                                {
                                    Track = t, Pid = pid, Include = true,
                                    Description = ProcLabel(procs, pid) + " (PID " + pid + ")"
                                };
                            }
                            note = "Mehrere App-Spuren aktiv: Programme mit Ton werden einzeln eingesammelt (Systemklänge fehlen dabei).";
                        }
                        break;
                    case TrackType.Mix:
                        note = "Summe aller anderen Spuren.";
                        break;
                }
                t.Note = note;
                t.NoteProblem = problem;
            }

            var status = new List<string>();
            lock (active)
            {
                // Beenden, was nicht mehr gebraucht wird oder fehlgeschlagen ist
                var remove = new List<string>();
                foreach (var kv in active)
                {
                    if (!desired.ContainsKey(kv.Key)) remove.Add(kv.Key);
                    else if (kv.Value.Capture.Failed)
                    {
                        remove.Add(kv.Key);
                        retryAfter[kv.Key] = DateTime.UtcNow.AddSeconds(5);
                        failures[kv.Key] = new Failure { Track = kv.Value.Track, Description = kv.Value.Capture.Description, Error = kv.Value.Capture.Error };
                        LastError = "Spur " + (kv.Value.Track.Index + 1) + ": " + kv.Value.Capture.Error;
                    }
                }
                foreach (var k in remove)
                {
                    var a = active[k];
                    active.Remove(k);
                    lock (a.Track.SrcLock) a.Track.Sources.Remove(a.Source);
                    a.Capture.Stop();
                }
                var staleFail = new List<string>();
                foreach (var k in failures.Keys) if (!desired.ContainsKey(k)) staleFail.Add(k);
                foreach (var k in staleFail) failures.Remove(k);

                // Neue Quellen starten
                foreach (var kv in desired)
                {
                    if (active.ContainsKey(kv.Key)) continue;
                    DateTime until;
                    if (retryAfter.TryGetValue(kv.Key, out until) && DateTime.UtcNow < until) continue;
                    var sp = kv.Value;
                    var src = new AudioSource();
                    var cap = sp.IsDevice
                        ? WasapiCapture.ForDevice(sp.DeviceId, src, sp.Description)
                        : WasapiCapture.ForProcess(sp.Pid, sp.Include, src, sp.Description);
                    var act = new Active { Track = sp.Track, Source = src, Capture = cap };
                    lock (sp.Track.SrcLock) sp.Track.Sources.Add(src);
                    active[kv.Key] = act;
                    cap.Start();
                }

                foreach (var t in tracks)
                {
                    int n = 0;
                    foreach (var a in active.Values) if (a.Track == t && !a.Capture.Failed) n++;
                    status.Add(string.Format("Spur {0}: {1}", t.Index + 1, t.Cfg.Type == TrackType.Mix ? "Mix" : n + " Quelle(n)"));
                }
            }
            SourceStatus = string.Join("  |  ", status.ToArray());
        }

        // ---------------- Status für Pegelanzeige / Diagnose ----------------

        public List<TrackStatus> GetStatus()
        {
            var list = new List<TrackStatus>();
            long now = Stopwatch.GetTimestamp();
            double freq = Stopwatch.Frequency;
            lock (active)
            {
                foreach (var t in tracks)
                {
                    var ts = new TrackStatus { Index = t.Index, Name = t.Cfg.Name, Type = t.Cfg.Type, Level = Decay(t.Peak, t.PeakTicks, now) };
                    bool anyProblem = t.NoteProblem;
                    int receiving = 0;
                    foreach (var a in active.Values)
                    {
                        if (a.Track != t) continue;
                        var c = a.Capture;
                        var s = new SourceStatus { Description = c.Description, Format = c.FormatInfo, Level = a.Source.Level };
                        double age = (DateTime.UtcNow - c.Created).TotalSeconds;
                        double since = a.Source.LastDataTicks == 0 ? double.MaxValue : (now - a.Source.LastDataTicks) / freq;
                        if (c.Failed) { s.State = "Fehler: " + c.Error; s.Problem = true; }
                        else if (!c.Running) s.State = age < 5 ? "startet …" : "startet nicht (hängt)";
                        else if (c.Packets == 0)
                        {
                            s.State = c.IsDevice ? "läuft, aber Windows liefert keine Daten" : "läuft – noch kein Ton von der App";
                            s.Problem = c.IsDevice && age > 3;
                        }
                        else if (since > 1.5)
                        {
                            s.State = string.Format("keine Daten seit {0:0} s", since);
                            s.Problem = c.IsDevice;
                        }
                        else if (a.Source.MaxEver < 0.0003f)
                        {
                            s.State = c.IsDevice
                                ? "empfängt nur Stille – Mikro stumm/Pegel 0 oder falsches Gerät?"
                                : "empfängt Stille (App spielt gerade nichts ab)";
                            s.Problem = c.IsDevice && age > 3;
                            receiving++;
                        }
                        else { s.State = "empfängt Ton"; receiving++; }
                        if (c.SilentPackets > 0 && c.SilentPackets == c.Packets && c.Packets > 50)
                            s.State += " (Windows meldet nur Stille-Pakete)";
                        anyProblem |= s.Problem;
                        ts.Sources.Add(s);
                    }
                    foreach (var kv in failures)
                    {
                        if (kv.Value.Track != t || active.ContainsKey(kv.Key)) continue;
                        ts.Sources.Add(new SourceStatus { Description = kv.Value.Description, State = "Fehler: " + kv.Value.Error + " (neuer Versuch alle 5 s)", Problem = true });
                        anyProblem = true;
                    }
                    if (t.Note.Length > 0) ts.Summary = t.Note;
                    else if (t.Cfg.Type == TrackType.Mix) ts.Summary = "Summe aller anderen Spuren.";
                    else if (ts.Sources.Count == 0) ts.Summary = "Keine Quelle aktiv.";
                    else ts.Summary = receiving + " von " + ts.Sources.Count + " Quelle(n) liefern Daten.";
                    ts.Problem = anyProblem;
                    list.Add(ts);
                }
            }
            return list;
        }
    }
}
