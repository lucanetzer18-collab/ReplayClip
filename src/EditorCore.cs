using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace ReplayClip
{
    /// <summary>Infos über einen Clip (aus der FFmpeg-Ausgabe gelesen).</summary>
    public sealed class ClipInfo
    {
        public double Duration;
        public int Width, Height;
        public double Fps = 30;
        public string VideoCodec = "";
        public readonly List<string> AudioNames = new List<string>();

        public static ClipInfo Probe(string ffmpeg, string file)
        {
            var err = new StringBuilder();
            var p = Ffmpeg.Launch(ffmpeg, "-hide_banner -nostdin -i " + Ffmpeg.Q(file), false, err);
            p.WaitForExit(15000);
            p.WaitForExit();
            string text;
            lock (err) text = err.ToString();

            var info = new ClipInfo();
            var md = Regex.Match(text, @"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");
            if (md.Success)
                info.Duration = int.Parse(md.Groups[1].Value) * 3600 + int.Parse(md.Groups[2].Value) * 60 +
                                double.Parse(md.Groups[3].Value, CultureInfo.InvariantCulture);
            string currentType = null;
            int audioIdx = -1;
            foreach (var raw in text.Replace("\r", "").Split('\n'))
            {
                var ms = Regex.Match(raw, @"^\s*Stream #\d+:\d+.*?: (Video|Audio|Data|Subtitle): (\w+)");
                if (ms.Success)
                {
                    currentType = ms.Groups[1].Value;
                    if (currentType == "Video" && info.Width == 0)
                    {
                        info.VideoCodec = ms.Groups[2].Value;
                        var sz = Regex.Match(raw, @", (\d{2,5})x(\d{2,5})");
                        if (sz.Success) { info.Width = int.Parse(sz.Groups[1].Value); info.Height = int.Parse(sz.Groups[2].Value); }
                        var fps = Regex.Match(raw, @", (\d+(?:\.\d+)?) fps");
                        if (fps.Success) info.Fps = double.Parse(fps.Groups[1].Value, CultureInfo.InvariantCulture);
                    }
                    else if (currentType == "Audio")
                    {
                        audioIdx++;
                        info.AudioNames.Add("Spur " + (audioIdx + 1));
                    }
                    continue;
                }
                if (currentType == "Audio" && audioIdx >= 0)
                {
                    var mt = Regex.Match(raw, @"^\s+(title|handler_name)\s*:\s*(.+?)\s*$");
                    if (mt.Success)
                    {
                        string v = mt.Groups[2].Value;
                        bool generic = v == "SoundHandler" || v.StartsWith("ISO Media") || v.Length == 0;
                        if (!generic && (mt.Groups[1].Value == "title" || info.AudioNames[audioIdx].StartsWith("Spur ")))
                            info.AudioNames[audioIdx] = v;
                    }
                }
                if (Regex.IsMatch(raw, @"^\S")) currentType = null;
            }
            return info;
        }
    }

    public enum ExportAudioMode { Separate = 0, MixOnly = 1, MixPlusSeparate = 2 }

    public static class EditorFilters
    {
        static string Vol(int percent)
        {
            string v = (percent / 100.0).ToString("0.000", CultureInfo.InvariantCulture);
            // Bei Verstärkung über 100 % weich begrenzen statt übersteuern
            return percent > 100 ? v + ",alimiter=limit=0.97:level=false" : v;
        }

        /// <summary>Filtergraph für alle aktiven Spuren. Liefert die Labels der Ausgänge und deren Namen.</summary>
        public static string Build(List<int> enabled, List<int> volumes, List<string> names, ExportAudioMode mode,
                                   List<string> outLabels, List<string> outNames)
        {
            var g = new List<string>();
            if (enabled.Count == 0) return "";
            if (mode == ExportAudioMode.Separate)
            {
                foreach (int i in enabled)
                {
                    g.Add(string.Format("[0:a:{0}]volume={1}[o{0}]", i, Vol(volumes[i])));
                    outLabels.Add("[o" + i + "]");
                    outNames.Add(names[i]);
                }
                return string.Join(";", g.ToArray());
            }
            bool plus = mode == ExportAudioMode.MixPlusSeparate;
            var mixIn = new StringBuilder();
            foreach (int i in enabled)
            {
                if (plus) g.Add(string.Format("[0:a:{0}]volume={1},asplit=2[m{0}][o{0}]", i, Vol(volumes[i])));
                else g.Add(string.Format("[0:a:{0}]volume={1}[m{0}]", i, Vol(volumes[i])));
                mixIn.Append("[m" + i + "]");
            }
            if (enabled.Count == 1) g.Add(mixIn + "anull[mix]");
            else g.Add(mixIn + "amix=inputs=" + enabled.Count + ":normalize=0:duration=longest[mix]");
            outLabels.Add("[mix]");
            outNames.Add("Mix");
            if (plus)
            {
                foreach (int i in enabled)
                {
                    outLabels.Add("[o" + i + "]");
                    outNames.Add(names[i]);
                }
            }
            return string.Join(";", g.ToArray());
        }

        public static string T(double seconds) { return seconds.ToString("0.000", CultureInfo.InvariantCulture); }

        public static string Scale(int w, int h)
        {
            return "scale=" + w + ":" + h + ":force_original_aspect_ratio=decrease:flags=bilinear,pad=" + w + ":" + h + ":(ow-iw)/2:(oh-ih)/2";
        }
    }

    // ======================= Bild direkt per GDI ausgeben (schnell, ohne GDI+) =======================

    public static class Blitter
    {
        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight;
            public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }

        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
        [DllImport("gdi32.dll")] static extern int SetDIBitsToDevice(IntPtr hdc, int x, int y, int w, int h, int sx, int sy, int start, int lines, byte[] bits, ref BITMAPINFOHEADER bmi, int usage);
        [DllImport("gdi32.dll")] static extern int StretchDIBits(IntPtr hdc, int x, int y, int w, int h, int sx, int sy, int sw, int sh, byte[] bits, ref BITMAPINFOHEADER bmi, int usage, int rop);
        [DllImport("gdi32.dll")] static extern int SetStretchBltMode(IntPtr hdc, int mode);
        [DllImport("gdi32.dll")] static extern bool SetBrushOrgEx(IntPtr hdc, int x, int y, IntPtr pt);

        public static bool Available = true;

        /// <summary>BGRA-Bild (w×h) in 'dest' zeichnen. 1:1 ohne Skalierung, sonst HALFTONE.</summary>
        public static void Draw(IntPtr hdc, Rectangle dest, byte[] bgra, int w, int h)
        {
            var bmi = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32, biCompression = 0 };
            if (dest.Width == w && dest.Height == h)
                SetDIBitsToDevice(hdc, dest.X, dest.Y, w, h, 0, 0, 0, h, bgra, ref bmi, 0);
            else
            {
                SetStretchBltMode(hdc, 4 /*HALFTONE*/);
                SetBrushOrgEx(hdc, 0, 0, IntPtr.Zero);
                StretchDIBits(hdc, dest.X, dest.Y, dest.Width, dest.Height, 0, 0, w, h, bgra, ref bmi, 0, 0x00CC0020 /*SRCCOPY*/);
            }
        }

        /// <summary>Aus einem Hintergrund-Thread direkt ins Fenster zeichnen.</summary>
        public static void DrawToWindow(IntPtr hwnd, Rectangle dest, byte[] bgra, int w, int h)
        {
            if (!Available || hwnd == IntPtr.Zero) return;
            try
            {
                IntPtr dc = GetDC(hwnd);
                if (dc == IntPtr.Zero) return;
                try { Draw(dc, dest, bgra, w, h); }
                finally { ReleaseDC(hwnd, dc); }
            }
            catch (EntryPointNotFoundException) { Available = false; }
            catch (DllNotFoundException) { Available = false; }
        }

        /// <summary>Fallback (z.B. ohne GDI): über GDI+ zeichnen.</summary>
        public static void DrawGdiPlus(Graphics g, Rectangle dest, byte[] bgra, int w, int h)
        {
            using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppRgb))
            {
                var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
                try
                {
                    if (bd.Stride == w * 4) Marshal.Copy(bgra, 0, bd.Scan0, w * h * 4);
                    else for (int y = 0; y < h; y++) Marshal.Copy(bgra, y * w * 4, new IntPtr(bd.Scan0.ToInt64() + (long)y * bd.Stride), w * 4);
                }
                finally { bmp.UnlockBits(bd); }
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
                g.DrawImage(bmp, dest);
            }
        }
    }

    // ======================= Audio-Ausgabe über waveOut =======================

    public sealed class WaveOut : IDisposable
    {
        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        struct WAVEFORMATEX
        {
            public short wFormatTag, nChannels;
            public int nSamplesPerSec, nAvgBytesPerSec;
            public short nBlockAlign, wBitsPerSample, cbSize;
        }

        [DllImport("winmm.dll")] static extern int waveOutOpen(out IntPtr h, int deviceId, ref WAVEFORMATEX fmt, IntPtr cb, IntPtr inst, int flags);
        [DllImport("winmm.dll")] static extern int waveOutPrepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutUnprepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutWrite(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutReset(IntPtr h);
        [DllImport("winmm.dll")] static extern int waveOutClose(IntPtr h);
        [DllImport("winmm.dll")] static extern int waveOutGetPosition(IntPtr h, IntPtr mmtime, int size);

        public const int BytesPerSec = 48000 * 4;
        const int HdrSize = 48;  // WAVEHDR auf x64
        const int Count = 8;
        public const int BufBytes = BytesPerSec / 33; // ~30 ms
        IntPtr h;
        readonly IntPtr[] hdrs = new IntPtr[Count];
        readonly IntPtr[] datas = new IntPtr[Count];
        readonly bool[] queued = new bool[Count];
        readonly IntPtr mmtime;
        readonly object lck = new object();
        long lastPlayed;

        public WaveOut()
        {
            var f = new WAVEFORMATEX { wFormatTag = 1, nChannels = 2, nSamplesPerSec = 48000, nAvgBytesPerSec = BytesPerSec, nBlockAlign = 4, wBitsPerSample = 16, cbSize = 0 };
            int r = waveOutOpen(out h, -1, ref f, IntPtr.Zero, IntPtr.Zero, 0);
            if (r != 0) throw new Exception("Audioausgabe konnte nicht geöffnet werden (waveOut " + r + ").");
            for (int i = 0; i < Count; i++)
            {
                hdrs[i] = Marshal.AllocHGlobal(HdrSize);
                datas[i] = Marshal.AllocHGlobal(BufBytes);
                for (int k = 0; k < HdrSize; k += 4) Marshal.WriteInt32(hdrs[i], k, 0);
            }
            mmtime = Marshal.AllocHGlobal(16);
        }

        bool Done(int i) { return (Marshal.ReadInt32(hdrs[i], 24) & 1) != 0; }

        public int FreeSlot()
        {
            lock (lck)
            {
                if (h == IntPtr.Zero) return -1;
                for (int i = 0; i < Count; i++)
                {
                    if (!queued[i]) return i;
                    if (Done(i))
                    {
                        waveOutUnprepareHeader(h, hdrs[i], HdrSize);
                        queued[i] = false;
                        return i;
                    }
                }
                return -1;
            }
        }

        public bool AllDone
        {
            get
            {
                lock (lck)
                {
                    for (int i = 0; i < Count; i++) if (queued[i] && !Done(i)) return false;
                    return true;
                }
            }
        }

        public void Queue(int slot, byte[] data, int len)
        {
            lock (lck)
            {
                if (h == IntPtr.Zero) return;
                Marshal.Copy(data, 0, datas[slot], len);
                IntPtr hd = hdrs[slot];
                for (int k = 0; k < HdrSize; k += 4) Marshal.WriteInt32(hd, k, 0);
                Marshal.WriteIntPtr(hd, 0, datas[slot]);
                Marshal.WriteInt32(hd, 8, len);
                waveOutPrepareHeader(h, hd, HdrSize);
                waveOutWrite(h, hd, HdrSize);
                queued[slot] = true;
            }
        }

        public long PlayedBytes
        {
            get
            {
                lock (lck)
                {
                    if (h == IntPtr.Zero) return lastPlayed;
                    Marshal.WriteInt32(mmtime, 0, 4); // TIME_BYTES
                    Marshal.WriteInt32(mmtime, 4, 0);
                    if (waveOutGetPosition(h, mmtime, 12) != 0) return lastPlayed;
                    lastPlayed = (uint)Marshal.ReadInt32(mmtime, 4);
                    return lastPlayed;
                }
            }
        }

        public void Dispose()
        {
            lock (lck)
            {
                if (h == IntPtr.Zero) return;
                waveOutReset(h);
                for (int i = 0; i < Count; i++)
                {
                    if (queued[i]) waveOutUnprepareHeader(h, hdrs[i], HdrSize);
                    Marshal.FreeHGlobal(hdrs[i]);
                    Marshal.FreeHGlobal(datas[i]);
                }
                waveOutClose(h);
                Marshal.FreeHGlobal(mmtime);
                h = IntPtr.Zero;
            }
        }
    }

    // ======================= Vorschau-Wiedergabe =======================

    /// <summary>
    /// Spielt den Clip ab Position X: ein FFmpeg liefert Bilder in genau der Anzeigegröße (BGRA),
    /// ein zweites den gemischten Ton. Der Ton ist die Uhr; ein eigener Render-Thread zeichnet
    /// jedes Bild pünktlich per GDI direkt ins Fenster – ohne Umweg über den UI-Thread.
    /// </summary>
    public sealed class PreviewPlayback
    {
        public readonly int W, H;
        public readonly double Fps;
        readonly int frameBytes;
        public readonly double StartPos;
        readonly Process vproc, aproc;
        readonly Queue<KeyValuePair<double, byte[]>> frames = new Queue<KeyValuePair<double, byte[]>>();
        readonly Stack<byte[]> pool = new Stack<byte[]>();
        readonly object fl = new object();
        volatile bool stop, videoEnded, audioEnded;
        WaveOut wo;
        readonly Stopwatch sw = new Stopwatch();
        readonly bool hasAudio;
        public volatile string Error;
        readonly IntPtr hwnd;
        readonly Func<Rectangle> dest;
        byte[] shown;
        readonly object shownLock = new object();

        public PreviewPlayback(string ffmpeg, string file, double pos, double duration, string audioGraph, int w, int h, double fps, IntPtr target, Func<Rectangle> destRect)
        {
            StartPos = pos;
            W = w; H = h; Fps = fps;
            frameBytes = w * h * 4;
            hwnd = target;
            dest = destRect;
            string ss = "-ss " + EditorFilters.T(pos) + " -t " + EditorFilters.T(Math.Max(0.05, duration)) + " ";
            string vf = "fps=" + fps.ToString("0.###", CultureInfo.InvariantCulture) + "," + EditorFilters.Scale(w, h);
            vproc = Ffmpeg.Launch(ffmpeg, "-hide_banner -loglevel error -nostdin -hwaccel auto -threads 0 " + ss + "-i " + Ffmpeg.Q(file) +
                                  " -an -sn -vf \"" + vf + "\" -f rawvideo -pix_fmt bgra pipe:1", false, null, true, true);
            hasAudio = !string.IsNullOrEmpty(audioGraph);
            if (hasAudio)
            {
                aproc = Ffmpeg.Launch(ffmpeg, "-hide_banner -loglevel error -nostdin " + ss + "-i " + Ffmpeg.Q(file) +
                                      " -vn -filter_complex \"" + audioGraph + "\" -map \"[a]\" -f s16le -ar 48000 -ac 2 pipe:1", false, null, true, true);
            }
            new Thread(VideoLoop) { IsBackground = true, Name = "Vorschau-Decoder" }.Start();
            new Thread(AudioLoop) { IsBackground = true, Name = "Vorschau-Audio", Priority = ThreadPriority.AboveNormal }.Start();
            new Thread(RenderLoop) { IsBackground = true, Name = "Vorschau-Render", Priority = ThreadPriority.AboveNormal }.Start();
        }

        static int ReadFully(Stream s, byte[] buf, int len)
        {
            int got = 0;
            while (got < len)
            {
                int r = s.Read(buf, got, len - got);
                if (r <= 0) break;
                got += r;
            }
            return got;
        }

        void VideoLoop()
        {
            try
            {
                var st = vproc.StandardOutput.BaseStream;
                long k = 0;
                int max = Math.Max(4, Math.Min(24, (int)(120L * 1024 * 1024 / Math.Max(1, frameBytes))));
                while (!stop)
                {
                    byte[] buf;
                    lock (fl)
                    {
                        while (frames.Count >= max && !stop) Monitor.Wait(fl, 50);
                        if (stop) break;
                        buf = pool.Count > 0 ? pool.Pop() : new byte[frameBytes];
                    }
                    if (ReadFully(st, buf, frameBytes) < frameBytes) break;
                    lock (fl) { frames.Enqueue(new KeyValuePair<double, byte[]>(k / Fps, buf)); Monitor.PulseAll(fl); }
                    k++;
                }
            }
            catch (Exception ex) { if (!stop) Error = ex.Message; }
            videoEnded = true;
        }

        void AudioLoop()
        {
            if (!hasAudio)
            {
                WaitFirstFrame();
                sw.Start();
                return;
            }
            try
            {
                wo = new WaveOut();
                WaitFirstFrame();
                var st = aproc.StandardOutput.BaseStream;
                var buf = new byte[WaveOut.BufBytes];
                while (!stop)
                {
                    int slot = wo.FreeSlot();
                    if (slot < 0) { Thread.Sleep(3); continue; }
                    int n = ReadFully(st, buf, buf.Length);
                    n -= n % 4;
                    if (n <= 0) break;
                    wo.Queue(slot, buf, n);
                }
                while (!stop && !wo.AllDone) Thread.Sleep(5);
            }
            catch (Exception ex)
            {
                if (!stop) Error = ex.Message;
                if (!sw.IsRunning) sw.Start();
            }
            finally
            {
                var w = wo;
                if (w != null) { try { w.Dispose(); } catch { } }
            }
            audioEnded = true;
        }

        void WaitFirstFrame()
        {
            var t = Stopwatch.StartNew();
            while (!stop && t.ElapsedMilliseconds < 4000)
            {
                lock (fl) if (frames.Count > 1 || (frames.Count > 0 && videoEnded)) return;
                if (videoEnded) return;
                Thread.Sleep(2);
            }
        }

        /// <summary>Sekunden seit StartPos (Ton ist die Uhr).</summary>
        public double Clock
        {
            get
            {
                var w = wo;
                if (hasAudio && w != null && !audioEnded) return w.PlayedBytes / (double)WaveOut.BytesPerSec;
                if (hasAudio && audioEnded) return lastClock;
                return sw.Elapsed.TotalSeconds;
            }
        }

        double lastClock;

        public bool Ended
        {
            get
            {
                if (hasAudio) return audioEnded;
                bool empty;
                lock (fl) empty = frames.Count == 0;
                return videoEnded && empty;
            }
        }

        void RenderLoop()
        {
            try
            {
                while (!stop)
                {
                    double now = Clock;
                    lastClock = now;
                    byte[] latest = null;
                    double nextDue = -1;
                    lock (fl)
                    {
                        while (frames.Count > 0 && frames.Peek().Key <= now + 0.004)
                        {
                            if (latest != null) pool.Push(latest);
                            latest = frames.Dequeue().Value;
                        }
                        if (frames.Count > 0) nextDue = frames.Peek().Key;
                        if (latest != null) Monitor.PulseAll(fl);
                    }
                    if (latest != null)
                    {
                        Blitter.DrawToWindow(hwnd, dest(), latest, W, H);
                        lock (shownLock)
                        {
                            if (shown != null) lock (fl) pool.Push(shown);
                            shown = latest;
                        }
                    }
                    if (videoEnded && nextDue < 0 && (Ended || !hasAudio)) break;
                    int wait = nextDue < 0 ? 4 : (int)Math.Max(1, Math.Min(10, (nextDue - now) * 1000 - 1));
                    Thread.Sleep(wait);
                }
            }
            catch (Exception ex) { if (!stop) Error = ex.Message; }
        }

        /// <summary>Zuletzt gezeigtes Bild (für Neuzeichnen im UI-Thread).</summary>
        public byte[] Current { get { lock (shownLock) return shown; } }

        public void Stop()
        {
            stop = true;
            lock (fl) Monitor.PulseAll(fl);
            foreach (var p in new[] { vproc, aproc })
            {
                try { if (p != null && !p.HasExited) p.Kill(); } catch { }
            }
        }
    }

    /// <summary>Holt einzelne Standbilder (beim Scrubben) – immer nur die neueste Anfrage.</summary>
    public sealed class FrameGrabber
    {
        readonly string ffmpeg, file;
        readonly AutoResetEvent ev = new AutoResetEvent(false);
        double requested = -1;
        int reqW, reqH;
        readonly object l = new object();
        volatile bool stop;
        public event Action<double, byte[], int, int> FrameReady;
        public volatile bool Busy;

        public FrameGrabber(string ffmpeg, string file)
        {
            this.ffmpeg = ffmpeg; this.file = file;
            new Thread(Loop) { IsBackground = true, Name = "Standbild" }.Start();
        }

        public void Request(double pos, int w, int h)
        {
            lock (l) { requested = pos; reqW = Math.Max(16, w & ~1); reqH = Math.Max(16, h & ~1); }
            ev.Set();
        }

        public void Stop() { stop = true; ev.Set(); }

        void Loop()
        {
            while (!stop)
            {
                ev.WaitOne();
                if (stop) break;
                double pos; int w, h;
                lock (l) { pos = requested; requested = -1; w = reqW; h = reqH; }
                if (pos < 0) continue;
                Busy = true;
                try
                {
                    var p = Ffmpeg.Launch(ffmpeg, "-hide_banner -loglevel error -nostdin -ss " + EditorFilters.T(pos) + " -i " + Ffmpeg.Q(file) +
                                          " -frames:v 1 -an -sn -vf \"" + EditorFilters.Scale(w, h) + "\" -f rawvideo -pix_fmt bgra pipe:1", false, null, true, true);
                    var buf = new byte[w * h * 4];
                    var st = p.StandardOutput.BaseStream;
                    int got = 0;
                    while (got < buf.Length)
                    {
                        int r = st.Read(buf, got, buf.Length - got);
                        if (r <= 0) break;
                        got += r;
                    }
                    p.WaitForExit(5000);
                    var hnd = FrameReady;
                    if (got == buf.Length && hnd != null && !stop) hnd(pos, buf, w, h);
                }
                catch { }
                Busy = false;
            }
        }
    }

    /// <summary>Lautstärke-Hüllkurven aller Tonspuren in einem Durchlauf (100 Werte pro Sekunde).</summary>
    public static class WaveformLoader
    {
        public const int PerSecond = 100;

        public static float[][] LoadAll(string ffmpeg, string file, int tracks)
        {
            if (tracks <= 0) return new float[0][];
            if (tracks == 1) return new[] { Run(ffmpeg, "-map 0:a:0 -ac 1 -ar 8000", file, 1)[0] };
            var g = new StringBuilder();
            for (int i = 0; i < tracks; i++) g.AppendFormat("[0:a:{0}]aresample=8000,aformat=sample_fmts=s16:channel_layouts=mono[a{0}];", i);
            for (int i = 0; i < tracks; i++) g.Append("[a" + i + "]");
            g.Append("amerge=inputs=" + tracks + "[a]");
            var r = Run(ffmpeg, "-filter_complex \"" + g + "\" -map \"[a]\"", file, tracks);
            if (r[0].Length > 0) return r;
            // Fallback: Spur für Spur
            var res = new float[tracks][];
            for (int i = 0; i < tracks; i++) res[i] = Run(ffmpeg, "-map 0:a:" + i + " -ac 1 -ar 8000", file, 1)[0];
            return res;
        }

        static float[][] Run(string ffmpeg, string mapArgs, string file, int ch)
        {
            var env = new List<float>[ch];
            for (int i = 0; i < ch; i++) env[i] = new List<float>();
            try
            {
                var p = Ffmpeg.Launch(ffmpeg, "-hide_banner -loglevel error -nostdin -i " + Ffmpeg.Q(file) + " -vn " + mapArgs + " -f s16le pipe:1", false, null, false, true);
                var st = p.StandardOutput.BaseStream;
                int frameBytes = 2 * ch;
                var buf = new byte[frameBytes * 4096];
                int perBucket = 8000 / PerSecond;
                int inBucket = 0;
                var peak = new int[ch];
                int carry = 0;
                int r;
                while ((r = st.Read(buf, carry, buf.Length - carry)) > 0)
                {
                    int total = carry + r;
                    int whole = total - total % frameBytes;
                    for (int o = 0; o < whole; o += frameBytes)
                    {
                        for (int c = 0; c < ch; c++)
                        {
                            int s = (short)(buf[o + 2 * c] | (buf[o + 2 * c + 1] << 8));
                            if (s < 0) s = -s;
                            if (s > peak[c]) peak[c] = s;
                        }
                        if (++inBucket >= perBucket)
                        {
                            for (int c = 0; c < ch; c++) { env[c].Add(peak[c] / 32768f); peak[c] = 0; }
                            inBucket = 0;
                        }
                    }
                    carry = total - whole;
                    if (carry > 0) Buffer.BlockCopy(buf, whole, buf, 0, carry);
                }
                p.WaitForExit(5000);
                if (inBucket > 0) for (int c = 0; c < ch; c++) env[c].Add(peak[c] / 32768f);
            }
            catch { }
            var res = new float[ch][];
            for (int c = 0; c < ch; c++) res[c] = env[c].ToArray();
            return res;
        }
    }

    /// <summary>Filmstreifen: kleine Vorschaubilder an den Keyframes (sehr schnell, kein volles Dekodieren).</summary>
    public static class FilmstripLoader
    {
        public const int TW = 160, TH = 90;

        public static List<KeyValuePair<double, Bitmap>> Load(string ffmpeg, string file, Func<bool> cancelled)
        {
            var list = new List<KeyValuePair<double, Bitmap>>();
            var times = new List<double>();
            var psi = new ProcessStartInfo(ffmpeg,
                "-hide_banner -loglevel info -nostdin -skip_frame nokey -i " + Ffmpeg.Q(file) +
                " -an -sn -vf \"select='isnan(prev_selected_t)+gte(t-prev_selected_t\\,1.5)'," + EditorFilters.Scale(TW, TH) + ",showinfo\" -fps_mode passthrough -f rawvideo -pix_fmt bgra pipe:1")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true, StandardErrorEncoding = Encoding.UTF8
            };
            var rx = new Regex(@"pts_time:\s*(-?\d+(?:\.\d+)?)");
            try
            {
                using (var p = new Process { StartInfo = psi })
                {
                    p.ErrorDataReceived += (s, e) =>
                    {
                        if (e.Data == null || e.Data.IndexOf("Parsed_showinfo", StringComparison.Ordinal) < 0) return;
                        var m = rx.Match(e.Data);
                        if (m.Success) lock (times) times.Add(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
                    };
                    p.Start();
                    ChildJob.Add(p);
                    p.BeginErrorReadLine();
                    try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
                    var st = p.StandardOutput.BaseStream;
                    int fb = TW * TH * 4;
                    var buf = new byte[fb];
                    int idx = 0;
                    while (!cancelled())
                    {
                        int got = 0;
                        while (got < fb)
                        {
                            int r = st.Read(buf, got, fb - got);
                            if (r <= 0) break;
                            got += r;
                        }
                        if (got < fb) break;
                        var bmp = new Bitmap(TW, TH, PixelFormat.Format32bppRgb);
                        var bd = bmp.LockBits(new Rectangle(0, 0, TW, TH), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
                        try
                        {
                            if (bd.Stride == TW * 4) Marshal.Copy(buf, 0, bd.Scan0, fb);
                            else for (int y = 0; y < TH; y++) Marshal.Copy(buf, y * TW * 4, new IntPtr(bd.Scan0.ToInt64() + (long)y * bd.Stride), TW * 4);
                        }
                        finally { bmp.UnlockBits(bd); }
                        list.Add(new KeyValuePair<double, Bitmap>(-1, bmp));
                        idx++;
                    }
                    if (cancelled()) { try { p.Kill(); } catch { } }
                    if (p.WaitForExit(5000)) p.WaitForExit();
                }
            }
            catch { }
            // Zeitstempel zuordnen (showinfo-Zeilen kommen in derselben Reihenfolge)
            lock (times)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    double t = i < times.Count ? times[i] : (i > 0 ? list[i - 1].Key + 2 : 0);
                    list[i] = new KeyValuePair<double, Bitmap>(Math.Max(0, t), list[i].Value);
                }
            }
            return list;
        }
    }
}
