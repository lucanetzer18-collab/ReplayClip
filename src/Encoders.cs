using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace ReplayClip
{
    public enum EncKind { Nvidia, Amd, Intel, Cpu }

    /// <summary>
    /// Video-Encoder je nach Hardware: NVIDIA (NVENC), AMD (AMF), Intel/Onboard-Grafik (Quick Sync) oder Prozessor (x264).
    /// "Automatisch" probiert der Reihe nach, was auf diesem PC wirklich funktioniert (kurzer Test-Encode mit FFmpeg).
    /// </summary>
    public static class Encoders
    {
        static readonly Dictionary<string, bool> cache = new Dictionary<string, bool>();
        static readonly object l = new object();

        public static readonly string[] Prefs = { "auto", "nvidia", "amd", "intel", "cpu" };
        public static readonly string[] PrefNames =
        {
            "Automatisch erkennen",
            "NVIDIA (NVENC)",
            "AMD Radeon (AMF)",
            "Intel / Onboard-Grafik (Quick Sync)",
            "Prozessor (ohne Grafikkarte – mehr CPU-Last)"
        };

        public static string Label(EncKind k)
        {
            switch (k)
            {
                case EncKind.Nvidia: return "NVIDIA NVENC";
                case EncKind.Amd: return "AMD AMF";
                case EncKind.Intel: return "Intel Quick Sync";
                default: return "Prozessor (x264)";
            }
        }

        public static string Codec(EncKind k, bool hevc)
        {
            switch (k)
            {
                case EncKind.Nvidia: return hevc ? "hevc_nvenc" : "h264_nvenc";
                case EncKind.Amd: return hevc ? "hevc_amf" : "h264_amf";
                case EncKind.Intel: return hevc ? "hevc_qsv" : "h264_qsv";
                default: return hevc ? "libx265" : "libx264";
            }
        }

        static EncKind? FromPref(string p)
        {
            switch (p)
            {
                case "nvidia": return EncKind.Nvidia;
                case "amd": return EncKind.Amd;
                case "intel": return EncKind.Intel;
                case "cpu": return EncKind.Cpu;
            }
            return null;
        }

        /// <summary>Funktioniert dieser Encoder auf diesem PC? (Test-Encode von ein paar schwarzen Bildern, Ergebnis wird gemerkt)</summary>
        public static bool Works(string ffmpeg, string encoder)
        {
            string key = ffmpeg + "|" + encoder;
            lock (l)
            {
                bool ok;
                if (cache.TryGetValue(key, out ok)) return ok;
            }
            bool result = false;
            try
            {
                var p = Ffmpeg.Launch(ffmpeg, "-hide_banner -loglevel error -nostdin -f lavfi -i color=c=black:s=1280x720:r=30 -frames:v 5 -pix_fmt " +
                    (encoder.StartsWith("lib") ? "yuv420p" : "nv12") + " -c:v " + encoder + " -f null -", false, null);
                if (!p.WaitForExit(15000)) { try { p.Kill(); } catch { } }
                else result = p.ExitCode == 0;
            }
            catch { }
            lock (l) cache[key] = result;
            return result;
        }

        /// <summary>
        /// Encoder bestimmen. 'hevc' wird ggf. auf H.264 zurückgesetzt (Prozessor: HEVC wäre viel zu langsam;
        /// manche Grafikkarten können kein HEVC kodieren).
        /// </summary>
        public static EncKind Resolve(string ffmpeg, string pref, ref bool hevc)
        {
            var order = new List<EncKind>();
            var p = FromPref(pref);
            if (p.HasValue) order.Add(p.Value);
            foreach (EncKind k in new[] { EncKind.Nvidia, EncKind.Amd, EncKind.Intel })
                if (!order.Contains(k)) order.Add(k);
            foreach (var k in order)
            {
                if (k == EncKind.Cpu) break;
                if (hevc && Works(ffmpeg, Codec(k, true))) return Remember(k);
                if (Works(ffmpeg, Codec(k, false))) { hevc = false; return Remember(k); }
            }
            hevc = false;
            return Remember(EncKind.Cpu);
        }

        static EncKind Remember(EncKind k) { Last = k; return k; }

        /// <summary>Zuletzt erkannter/benutzter Encoder (für die Anzeige in den Einstellungen).</summary>
        public static EncKind? Last;

        /// <summary>Filter-Ende hinter ddagrab: NVENC nimmt die GPU-Bilder direkt, alle anderen bekommen normale Bilder.</summary>
        public static string CaptureFilter(EncKind k)
        {
            switch (k)
            {
                case EncKind.Nvidia: return "";
                case EncKind.Cpu: return ",hwdownload,format=bgra,format=yuv420p";
                default: return ",hwdownload,format=bgra,format=nv12";
            }
        }

        /// <summary>Encoder-Optionen für den Replay-Buffer (konstante Bitrate mit Obergrenze, feste Keyframe-Abstände).</summary>
        public static string BufferArgs(EncKind k, bool hevc, int mbps, int gop)
        {
            var ci = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.Append("-c:v ").Append(Codec(k, hevc)).Append(' ');
            switch (k)
            {
                case EncKind.Nvidia: sb.Append("-preset p4 -tune hq -rc vbr "); break;
                case EncKind.Amd: sb.Append("-usage transcoding -quality balanced -rc vbr_peak "); break;
                case EncKind.Intel: sb.Append("-preset faster "); break;
                default: sb.Append("-preset superfast "); break;
            }
            sb.AppendFormat(ci, "-b:v {0}M -maxrate {1}M -bufsize {2}M ", mbps, mbps * 3 / 2, mbps * 2);
            sb.Append("-g ").Append(gop).Append(' ');
            if (!hevc) sb.Append("-profile:v high ");
            if (k == EncKind.Cpu) sb.Append("-pix_fmt yuv420p ");
            return sb.ToString();
        }

        /// <summary>Encoder-Optionen für den Editor-Export (Qualität statt fester Bitrate).</summary>
        public static string ExportArgs(EncKind k, bool hevc)
        {
            var sb = new StringBuilder();
            sb.Append("-c:v ").Append(Codec(k, hevc)).Append(' ');
            if (hevc) sb.Append("-tag:v hvc1 "); else sb.Append("-profile:v high ");
            switch (k)
            {
                case EncKind.Nvidia: sb.Append("-preset p5 -tune hq -rc vbr -cq 19 -b:v 0 "); break;
                case EncKind.Amd: sb.Append("-usage transcoding -quality quality -rc cqp -qp_i 18 -qp_p 20 "); break;
                case EncKind.Intel: sb.Append("-preset medium -global_quality 20 "); break;
                default: sb.Append(hevc ? "-crf 22 -preset veryfast " : "-crf 18 -preset veryfast "); break;
            }
            sb.Append(k == EncKind.Nvidia || k == EncKind.Cpu ? "-pix_fmt yuv420p " : "-pix_fmt nv12 ");
            return sb.ToString();
        }
    }
}
