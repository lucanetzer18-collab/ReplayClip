using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace ReplayClip
{
    /// <summary>Clip so verkleinern, dass er unter Discords Upload-Grenze passt (Bitrate aus Länge berechnet, ggf. kleinere Auflösung).</summary>
    public static class DiscordExport
    {
        public static readonly int[] Limits = { 10, 50, 500 };
        public static readonly string[] LimitNames = { "10 MB (ohne Nitro)", "50 MB (Nitro Basic)", "500 MB (Nitro)" };

        public static string OutDir { get { return Path.Combine(Path.GetTempPath(), "ReplayClip Discord"); } }

        /// <summary>Läuft im Hintergrund. progress(prozent) und done(datei, fehler) kommen auf einem Hintergrund-Thread.</summary>
        public static void Run(string ffmpeg, string file, int limitMB, string encPref, Action<int> progress, Action<string, string> done)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string outFile = null, error = null;
                try { outFile = Make(ffmpeg, file, limitMB, encPref, progress); }
                catch (Exception ex) { error = ex.Message; }
                done(outFile, error);
            });
        }

        static string Make(string ffmpeg, string file, int limitMB, string encPref, Action<int> progress)
        {
            var info = ClipInfo.Probe(ffmpeg, file);
            double dur = info.Duration;
            if (dur <= 0.1) throw new Exception("Länge des Clips unbekannt.");
            double limitBits = limitMB * 1024.0 * 1024.0 * 8 * 0.94;   // Puffer für Container-Overhead
            int abps = info.AudioNames.Count > 0 ? 96000 : 0;
            double vbps = limitBits / dur - abps;
            if (vbps < 180000)
                throw new Exception(string.Format(CultureInfo.CurrentCulture, "Clip zu lang für {0} MB – vorher im Editor auf max. ca. {1:0} Sekunden kürzen.", limitMB, limitBits / (180000 + abps)));
            vbps = Math.Min(vbps, 12000000);

            int srcH = info.Height > 0 ? info.Height : 1080;
            int h = vbps >= 5e6 ? 1080 : vbps >= 2e6 ? 720 : vbps >= 0.8e6 ? 540 : 360;
            h = Math.Min(h, srcH);
            int fps = vbps < 3e6 ? 30 : (int)Math.Min(60, Math.Round(info.Fps));

            Directory.CreateDirectory(OutDir);
            string outFile = Path.Combine(OutDir, Path.GetFileNameWithoutExtension(file) + " (Discord).mp4");
            bool hevc = false;
            var kind = Encoders.Resolve(ffmpeg, encPref, ref hevc);

            for (int attempt = 0; attempt < 3; attempt++)
            {
                string err;
                int code = Encode(ffmpeg, file, outFile, info, kind, (int)vbps, abps, h, fps, dur, progress, out err);
                if (code != 0 && kind != EncKind.Cpu) { kind = EncKind.Cpu; continue; }   // Grafikkarte streikt -> Prozessor
                if (code != 0 || !File.Exists(outFile)) throw new Exception(LastLine(err));
                long size = new FileInfo(outFile).Length;
                if (size <= limitMB * 1024L * 1024L) return outFile;
                vbps = vbps * 0.9 * (limitMB * 1024.0 * 1024.0 / size);   // zu groß -> knapper nochmal
            }
            throw new Exception("Datei wurde zu groß – bitte den Clip kürzen.");
        }

        static int Encode(string ffmpeg, string file, string outFile, ClipInfo info, EncKind kind, int vbps, int abps, int h, int fps, double dur, Action<int> progress, out string err)
        {
            var ci = CultureInfo.InvariantCulture;
            var sb = new StringBuilder("-hide_banner -loglevel error -nostdin -y -i ").Append(Ffmpeg.Q(file)).Append(' ');
            // Ton: vorhandene Mix-Spur nehmen, sonst alle Spuren zusammenmischen
            int n = info.AudioNames.Count, mix = -1;
            for (int i = 0; i < n; i++) if ((info.AudioNames[i] ?? "").IndexOf("mix", StringComparison.OrdinalIgnoreCase) >= 0) { mix = i; break; }
            if (n > 1 && mix < 0)
            {
                sb.Append("-filter_complex \"");
                for (int i = 0; i < n; i++) sb.Append("[0:a:").Append(i).Append(']');
                sb.Append("amix=inputs=").Append(n).Append(":normalize=0:dropout_transition=0,alimiter=limit=0.97:level=false[a]\" -map \"[a]\" ");
            }
            else if (n > 0) sb.Append("-map 0:a:").Append(Math.Max(0, mix)).Append(' ');
            sb.Append("-map 0:v:0 -vf \"scale=-2:").Append(h).Append(":flags=bicubic,fps=").Append(fps).Append("\" ");
            sb.Append("-c:v ").Append(Encoders.Codec(kind, false)).Append(' ');
            switch (kind)
            {
                case EncKind.Nvidia: sb.Append("-preset p6 -tune hq -rc vbr -multipass fullres "); break;
                case EncKind.Amd: sb.Append("-usage transcoding -quality quality -rc vbr_peak "); break;
                case EncKind.Intel: sb.Append("-preset slower "); break;
                default: sb.Append("-preset medium "); break;
            }
            sb.AppendFormat(ci, "-b:v {0} -maxrate {0} -bufsize {1} -profile:v high -pix_fmt {2} ", vbps, vbps * 2, kind == EncKind.Amd || kind == EncKind.Intel ? "nv12" : "yuv420p");
            if (abps > 0) sb.Append("-c:a aac -b:a ").Append(abps / 1000).Append("k -ac 2 "); else sb.Append("-an ");
            sb.Append("-movflags +faststart -progress pipe:1 -nostats ").Append(Ffmpeg.Q(outFile));

            var e = new StringBuilder();
            var p = Ffmpeg.Launch(ffmpeg, sb.ToString(), false, e, false, true);
            string line;
            while ((line = p.StandardOutput.ReadLine()) != null)
            {
                if (!line.StartsWith("out_time_us=") && !line.StartsWith("out_time_ms=")) continue;
                long us;
                if (long.TryParse(line.Substring(line.IndexOf('=') + 1), out us))
                    try { progress((int)Math.Max(0, Math.Min(99, us / 1e6 / dur * 100))); } catch { }
            }
            p.WaitForExit();
            lock (e) err = e.ToString();
            return p.ExitCode;
        }

        static string LastLine(string s)
        {
            var lines = (s ?? "").Replace("\r", "").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            return lines.Length == 0 ? "FFmpeg-Fehler" : lines[lines.Length - 1];
        }
    }
}
