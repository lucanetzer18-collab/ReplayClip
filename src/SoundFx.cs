using System;
using System.Collections.Generic;
using System.IO;
using System.Media;
using System.Text;

namespace ReplayClip
{
    /// <summary>Kurze Sounds für "Clip gespeichert" / Fehler – synthetisch erzeugt oder eigene Datei.</summary>
    public static class SoundFx
    {
        public static readonly KeyValuePair<string, string>[] Choices =
        {
            new KeyValuePair<string, string>("off", "Aus"),
            new KeyValuePair<string, string>("pling", "Pling"),
            new KeyValuePair<string, string>("double", "Doppel-Ping"),
            new KeyValuePair<string, string>("chime", "Glockenspiel (3 Töne)"),
            new KeyValuePair<string, string>("camera", "Kamera-Auslöser"),
            new KeyValuePair<string, string>("coin", "Retro-Coin"),
            new KeyValuePair<string, string>("bell", "Sanfte Glocke"),
            new KeyValuePair<string, string>("pop", "Pop"),
            new KeyValuePair<string, string>("success", "Erfolg (zwei Töne)"),
            new KeyValuePair<string, string>("sparkle", "Glitzer"),
            new KeyValuePair<string, string>("levelup", "Level-Up (Retro)"),
            new KeyValuePair<string, string>("blip", "Blip"),
            new KeyValuePair<string, string>("drop", "Wassertropfen"),
            new KeyValuePair<string, string>("whoosh", "Whoosh"),
            new KeyValuePair<string, string>("laser", "Laser"),
            new KeyValuePair<string, string>("shutter", "Kamera (mechanisch)"),
            new KeyValuePair<string, string>("press", "Tiefer Ton"),
            new KeyValuePair<string, string>("windows", "Windows-Standard"),
            new KeyValuePair<string, string>("file", "Eigene Datei …"),
        };

        const int Rate = 44100;
        static readonly Dictionary<string, byte[]> cache = new Dictionary<string, byte[]>();
        static SoundPlayer player;

        public static void PlaySaved(Settings s) { Play(s.SaveSound, s.SaveSoundFile, s.SoundVolume, s); }

        public static void PlayError(Settings s)
        {
            if (!s.ErrorSound) return;
            Play("error", null, s.SoundVolume, s);
        }

        public static void Play(string id, string file, int volume, Settings s)
        {
            try
            {
                if (id == null || id == "off" || volume <= 0) return;
                if (id == "windows") { SystemSounds.Asterisk.Play(); return; }
                byte[] wav;
                if (id == "file")
                {
                    wav = LoadFile(file, volume, s);
                    if (wav == null) { SystemSounds.Asterisk.Play(); return; }
                }
                else
                {
                    string key = id + "|" + volume;
                    if (!cache.TryGetValue(key, out wav))
                    {
                        wav = Synth(id, volume / 100f);
                        if (wav == null) return;
                        cache[key] = wav;
                    }
                }
                if (player != null) { try { player.Stop(); player.Dispose(); } catch { } }
                player = new SoundPlayer(new MemoryStream(wav));
                player.Play();
            }
            catch { }
        }

        // ---------------- eigene Datei (beliebiges Format über FFmpeg) ----------------

        static byte[] LoadFile(string file, int volume, Settings s)
        {
            if (string.IsNullOrEmpty(file) || !File.Exists(file)) return null;
            var fi = new FileInfo(file);
            string key = "f|" + file + "|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks + "|" + volume;
            byte[] cached;
            if (cache.TryGetValue(key, out cached)) return cached;
            string ff = Ffmpeg.Find(s);
            byte[] result = null;
            if (ff != null)
            {
                string tmp = Path.Combine(Path.GetTempPath(), "replayclip_sound_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".wav");
                try
                {
                    string args = "-hide_banner -loglevel error -nostdin -y -i " + Ffmpeg.Q(file) + " -t 6 -vn -af \"volume=" +
                                  (volume / 100.0).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + "\" -ac 2 -ar 44100 -c:a pcm_s16le " + Ffmpeg.Q(tmp);
                    var p = Ffmpeg.Launch(ff, args, false, null);
                    p.WaitForExit(15000);
                    if (File.Exists(tmp) && new FileInfo(tmp).Length > 100) result = File.ReadAllBytes(tmp);
                }
                catch { }
                finally { try { File.Delete(tmp); } catch { } }
            }
            else if (file.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) result = File.ReadAllBytes(file);
            if (result != null) cache[key] = result;
            return result;
        }

        // ---------------- Synthese ----------------

        static float[] Buf(double seconds) { return new float[(int)(Rate * seconds)]; }

        /// <summary>Ton mit Hüllkurve (Attack, exponentielles Abklingen) und Obertönen hinzufügen.</summary>
        static void Tone(float[] b, double start, double freq, double dur, double amp, double decay, double[] harmonics, double attack = 0.004)
        {
            int s0 = (int)(start * Rate), n = (int)(dur * Rate);
            for (int i = 0; i < n && s0 + i < b.Length; i++)
            {
                double t = i / (double)Rate;
                double env = Math.Min(1, t / attack) * Math.Exp(-t * decay);
                double v = 0;
                for (int h = 0; h < harmonics.Length; h++) v += harmonics[h] * Math.Sin(2 * Math.PI * freq * (h + 1) * t);
                b[s0 + i] += (float)(v * env * amp);
            }
        }

        /// <summary>Ton, dessen Höhe exponentiell von f0 nach f1 gleitet (Blip, Tropfen, Laser).</summary>
        static void Sweep(float[] b, double start, double f0, double f1, double dur, double amp, double decay)
        {
            int s0 = (int)(start * Rate), n = (int)(dur * Rate);
            double ph = 0;
            for (int i = 0; i < n && s0 + i < b.Length; i++)
            {
                double t = i / (double)Rate;
                double f = f0 * Math.Pow(f1 / f0, t / dur);
                ph += 2 * Math.PI * f / Rate;
                b[s0 + i] += (float)(amp * Math.Min(1, t / 0.003) * Math.Exp(-t * decay) * Math.Sin(ph));
            }
        }

        static void Square(float[] b, double start, double freq, double dur, double amp)
        {
            int s0 = (int)(start * Rate), n = (int)(dur * Rate);
            for (int i = 0; i < n && s0 + i < b.Length; i++)
            {
                double t = i / (double)Rate;
                double env = Math.Min(1, t / 0.002) * (t > dur - 0.02 ? Math.Max(0, (dur - t) / 0.02) : 1);
                double v = Math.Sin(2 * Math.PI * freq * t) >= 0 ? 1 : -1;
                b[s0 + i] += (float)(v * env * amp * 0.5);
            }
        }

        static void Noise(float[] b, double start, double dur, double amp, double decay, Random rnd)
        {
            int s0 = (int)(start * Rate), n = (int)(dur * Rate);
            double lp = 0;
            for (int i = 0; i < n && s0 + i < b.Length; i++)
            {
                double t = i / (double)Rate;
                double w = rnd.NextDouble() * 2 - 1;
                lp += (w - lp) * 0.35;
                b[s0 + i] += (float)(lp * amp * Math.Exp(-t * decay));
            }
        }

        static byte[] Synth(string id, float vol)
        {
            float[] b;
            var rnd = new Random(7);
            switch (id)
            {
                case "press":
                    // kurzer, tiefer "Bestätigt"-Ton beim Drücken des Hotkeys
                    b = Buf(0.22);
                    Tone(b, 0, 523.25, 0.22, 0.42, 16, new[] { 1, 0.35, 0.1 });
                    break;
                case "pling":
                    b = Buf(0.5);
                    Tone(b, 0, 1318.5, 0.5, 0.45, 9, new[] { 1, 0.25, 0.08 });
                    Tone(b, 0, 2637, 0.25, 0.08, 18, new[] { 1.0 });
                    break;
                case "double":
                    b = Buf(0.55);
                    Tone(b, 0, 987.8, 0.3, 0.4, 14, new[] { 1, 0.2 });
                    Tone(b, 0.11, 1318.5, 0.44, 0.42, 9, new[] { 1, 0.2, 0.05 });
                    break;
                case "chime":
                    b = Buf(0.9);
                    Tone(b, 0, 1046.5, 0.6, 0.32, 7, new[] { 1, 0.3, 0.1 });
                    Tone(b, 0.09, 1318.5, 0.6, 0.32, 7, new[] { 1, 0.3, 0.1 });
                    Tone(b, 0.18, 1568, 0.72, 0.36, 5.5, new[] { 1, 0.3, 0.1 });
                    break;
                case "camera":
                    b = Buf(0.32);
                    Noise(b, 0, 0.05, 0.9, 70, rnd);
                    Tone(b, 0, 2200, 0.02, 0.25, 160, new[] { 1.0 });
                    Noise(b, 0.11, 0.12, 0.7, 40, rnd);
                    Tone(b, 0.11, 1600, 0.03, 0.2, 140, new[] { 1.0 });
                    break;
                case "coin":
                    b = Buf(0.42);
                    Square(b, 0, 987.8, 0.07, 0.35);
                    Square(b, 0.07, 1318.5, 0.32, 0.32);
                    for (int i = (int)(0.07 * Rate); i < b.Length; i++) b[i] *= (float)Math.Exp(-(i / (double)Rate - 0.07) * 7);
                    break;
                case "bell":
                    {
                        b = Buf(1.2);
                        for (int i = 0; i < b.Length; i++)
                        {
                            double t = i / (double)Rate;
                            double mod = 2.4 * Math.Exp(-t * 4) * Math.Sin(2 * Math.PI * 880 * 1.4 * t);
                            b[i] = (float)(0.38 * Math.Min(1, t / 0.003) * Math.Exp(-t * 3.6) * Math.Sin(2 * Math.PI * 880 * t + mod));
                        }
                        break;
                    }
                case "pop":
                    {
                        b = Buf(0.18);
                        double ph = 0;
                        for (int i = 0; i < b.Length; i++)
                        {
                            double t = i / (double)Rate;
                            double f = 300 + 900 * Math.Exp(-t * 40);
                            ph += 2 * Math.PI * f / Rate;
                            b[i] = (float)(0.6 * Math.Min(1, t / 0.002) * Math.Exp(-t * 26) * Math.Sin(ph));
                        }
                        break;
                    }
                case "success":
                    b = Buf(0.6);
                    Tone(b, 0, 783.99, 0.25, 0.38, 12, new[] { 1, 0.3, 0.1 });
                    Tone(b, 0.12, 1174.66, 0.48, 0.42, 7, new[] { 1, 0.3, 0.1 });
                    break;
                case "sparkle":
                    b = Buf(0.8);
                    { double[] f = { 1568, 2093, 2637, 3136, 4186 }; for (int i = 0; i < f.Length; i++) Tone(b, i * 0.045, f[i], 0.6, 0.22, 9, new[] { 1, 0.15 }); }
                    break;
                case "levelup":
                    b = Buf(0.55);
                    { double[] f = { 523.25, 659.25, 783.99, 1046.5 }; for (int i = 0; i < f.Length; i++) Square(b, i * 0.075, f[i], i == 3 ? 0.25 : 0.075, 0.28); }
                    for (int i = (int)(0.225 * Rate); i < b.Length; i++) b[i] *= (float)Math.Exp(-(i / (double)Rate - 0.225) * 8);
                    break;
                case "blip":
                    b = Buf(0.14);
                    Sweep(b, 0, 900, 1500, 0.14, 0.5, 30);
                    break;
                case "drop":
                    b = Buf(0.3);
                    Sweep(b, 0, 1800, 500, 0.3, 0.6, 16);
                    break;
                case "whoosh":
                    {
                        b = Buf(0.45);
                        double lp = 0;
                        for (int i = 0; i < b.Length; i++)
                        {
                            double t = i / (double)Rate, k = 0.04 + 0.5 * Math.Sin(Math.PI * t / 0.45);
                            lp += (rnd.NextDouble() * 2 - 1 - lp) * k * 0.6;
                            b[i] = (float)(lp * Math.Sin(Math.PI * t / 0.45));
                        }
                        break;
                    }
                case "laser":
                    b = Buf(0.3);
                    Sweep(b, 0, 2600, 300, 0.3, 0.45, 9);
                    Sweep(b, 0, 2610, 305, 0.3, 0.25, 9);
                    break;
                case "shutter":
                    b = Buf(0.28);
                    Noise(b, 0, 0.03, 1.0, 120, rnd);
                    Tone(b, 0, 180, 0.05, 0.5, 60, new[] { 1, 0.5 });
                    Noise(b, 0.09, 0.05, 0.8, 90, rnd);
                    Tone(b, 0.09, 140, 0.06, 0.4, 50, new[] { 1, 0.5 });
                    break;
                case "error":
                    b = Buf(0.5);
                    Tone(b, 0, 392, 0.22, 0.38, 10, new[] { 1, 0.4, 0.2 });
                    Tone(b, 0.16, 311.1, 0.34, 0.38, 8, new[] { 1, 0.4, 0.2 });
                    break;
                default:
                    return null;
            }
            return Wav(b, vol);
        }

        static byte[] Wav(float[] samples, float vol)
        {
            // Normalisieren auf ~ -3 dB, dann Lautstärke
            float peak = 0.0001f;
            foreach (var v in samples) peak = Math.Max(peak, Math.Abs(v));
            float g = 0.7f / peak * vol;
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                int dataLen = samples.Length * 2;
                w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + dataLen); w.Write(Encoding.ASCII.GetBytes("WAVE"));
                w.Write(Encoding.ASCII.GetBytes("fmt ")); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
                w.Write(Encoding.ASCII.GetBytes("data")); w.Write(dataLen);
                int fadeN = Math.Min(samples.Length / 4, Rate / 100);
                for (int i = 0; i < samples.Length; i++)
                {
                    float v = samples[i] * g;
                    if (i > samples.Length - fadeN) v *= (samples.Length - i) / (float)fadeN;
                    w.Write((short)Math.Max(-32767, Math.Min(32767, v * 32767)));
                }
                w.Flush();
                return ms.ToArray();
            }
        }
    }
}
