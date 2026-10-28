using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace ReplayClip
{
    public sealed class UpdateInfo
    {
        public Version Version;
        public string Tag = "", Notes = "", ZipUrl = "", PageUrl = "";
        public string Display { get { return Program.FormatVersion(Version); } }
    }

    /// <summary>
    /// Updates über GitHub Releases: fragt das neueste Release ab, lädt die Zip, tauscht die EXE aus und startet neu.
    /// Die laufende EXE darf unter Windows umbenannt werden – sie wird zu "ReplayClip.old.exe" und beim nächsten Start gelöscht.
    /// </summary>
    public static class Updater
    {
        /// <summary>GitHub-Repository "Besitzer/Name". Platzhalter = Update-Suche ist aus.</summary>
        public const string DefaultRepo = "lucanetzer18-collab/ReplayClip";
        const string Placeholder = "DEIN-GITHUB-NAME/ReplayClip";

        public static bool RepoValid(string repo)
        {
            return !string.IsNullOrEmpty(repo) && repo != Placeholder && Regex.IsMatch(repo, @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$");
        }

        public static Version Current
        {
            get { return Norm(typeof(Updater).Assembly.GetName().Version); }
        }

        static Version Norm(Version v)
        {
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));
        }

        static WebClient Client()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var wc = new WebClient();
            wc.Headers.Add("User-Agent", "ReplayClip/" + Program.Version);
            wc.Headers.Add("Accept", "application/vnd.github+json");
            wc.Encoding = System.Text.Encoding.UTF8;
            return wc;
        }

        /// <summary>Neuestes Release abfragen (blockierend). Liefert null, wenn es nichts Neueres gibt.</summary>
        public static UpdateInfo Check(string repo)
        {
            if (!RepoValid(repo)) throw new Exception("Kein GitHub-Repository eingestellt.");
            string json;
            using (var wc = Client()) json = wc.DownloadString("https://api.github.com/repos/" + repo + "/releases/latest");
            var u = Parse(json);
            if (u.Version <= Current) return null;
            if (u.ZipUrl.Length == 0) throw new Exception("Im Release " + u.Tag + " ist keine Zip-Datei angehängt.");
            return u;
        }

        /// <summary>Antwort der GitHub-API (releases/latest) auswerten.</summary>
        public static UpdateInfo Parse(string json)
        {
            var u = new UpdateInfo();
            u.Tag = Str(json, "tag_name");
            u.PageUrl = Str(json, "html_url");
            u.Notes = Str(json, "body");
            foreach (Match m in Regex.Matches(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]+\\.zip)\""))
            {
                u.ZipUrl = m.Groups[1].Value;
                break;
            }
            var vm = Regex.Match(u.Tag, @"(\d+)\.(\d+)(?:\.(\d+))?(?:\.(\d+))?");
            if (!vm.Success) throw new Exception("Release-Name ohne Versionsnummer: " + u.Tag);
            u.Version = new Version(int.Parse(vm.Groups[1].Value), int.Parse(vm.Groups[2].Value),
                vm.Groups[3].Success ? int.Parse(vm.Groups[3].Value) : 0, vm.Groups[4].Success ? int.Parse(vm.Groups[4].Value) : 0);
            return u;
        }

        static string Str(string json, string key)
        {
            var m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            if (!m.Success) return "";
            string s = m.Groups[1].Value.Replace("\\/", "/");
            try { s = Regex.Unescape(s); } catch { }
            return s.Replace("\r\n", "\n");
        }

        /// <summary>Im Hintergrund prüfen; done(info, fehler) läuft auf dem UI-Thread von 'ui'.</summary>
        public static void CheckAsync(Control ui, string repo, Action<UpdateInfo, string> done)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                UpdateInfo info = null;
                string err = null;
                try { info = Check(repo); }
                catch (WebException ex)
                {
                    var r = ex.Response as HttpWebResponse;
                    err = r != null && r.StatusCode == HttpStatusCode.NotFound
                        ? "Repository oder Release nicht gefunden (" + repo + "). Ist das Repo öffentlich und gibt es schon ein Release?"
                        : "Keine Verbindung zu GitHub: " + ex.Message;
                }
                catch (Exception ex) { err = ex.Message; }
                try { if (!ui.IsDisposed) ui.BeginInvoke(new Action(() => done(info, err))); } catch { }
            });
        }

        /// <summary>Update herunterladen und Dateien austauschen. progress(prozent), done(fehler oder null) auf dem UI-Thread.</summary>
        public static void Install(Control ui, UpdateInfo u, Action<int> progress, Action<string> done)
        {
            string zip = Path.Combine(Path.GetTempPath(), "replayclip_update_" + u.Display + ".zip");
            var wc = Client();
            wc.DownloadProgressChanged += (s, e) => { try { progress(e.ProgressPercentage); } catch { } };
            wc.DownloadFileCompleted += (s, e) =>
            {
                wc.Dispose();
                if (e.Error != null) { done("Download fehlgeschlagen: " + e.Error.Message); return; }
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    string err = null;
                    try { Apply(zip); }
                    catch (Exception ex) { err = ex.Message; }
                    try { File.Delete(zip); } catch { }
                    try { ui.BeginInvoke(new Action(() => done(err))); } catch { }
                });
            };
            try { wc.DownloadFileAsync(new Uri(u.ZipUrl), zip); }
            catch (Exception ex) { wc.Dispose(); done("Download fehlgeschlagen: " + ex.Message); }
        }

        static void Apply(string zip)
        {
            string tmp = Path.Combine(Path.GetTempPath(), "replayclip_update_x");
            try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }
            Directory.CreateDirectory(tmp);
            try
            {
                // Windows 10/11 bringen tar.exe mit – entpackt auch ZIP.
                string tar = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "tar.exe");
                if (!File.Exists(tar)) tar = "tar.exe";
                var psi = new ProcessStartInfo(tar, "-xf \"" + zip + "\" -C \"" + tmp + "\"") { UseShellExecute = false, CreateNoWindow = true };
                using (var p = Process.Start(psi)) p.WaitForExit(120000);
                var exes = Directory.GetFiles(tmp, "ReplayClip.exe", SearchOption.AllDirectories);
                if (exes.Length == 0) throw new Exception("ReplayClip.exe nicht im Update gefunden.");
                string srcDir = Path.GetDirectoryName(exes[0]);
                string exe = Application.ExecutablePath;
                string appDir = Path.GetDirectoryName(exe);
                string old = Path.Combine(appDir, Path.GetFileNameWithoutExtension(exe) + ".old.exe");

                // Laufende EXE beiseite schieben (Umbenennen ist erlaubt, Überschreiben nicht)
                try { if (File.Exists(old)) File.Delete(old); } catch { }
                try { File.Move(exe, old); }
                catch (UnauthorizedAccessException) { throw new Exception("Keine Schreibrechte im Programmordner (" + appDir + "). Bitte die Zip manuell entpacken oder ReplayClip in einen eigenen Ordner legen."); }
                try
                {
                    File.Copy(exes[0], exe, true);
                }
                catch
                {
                    try { File.Move(old, exe); } catch { }
                    throw;
                }
                // Restliche Dateien (Liesmich, Skripte, Quellcode) ebenfalls aktualisieren
                foreach (var f in Directory.GetFiles(srcDir, "*", SearchOption.AllDirectories))
                {
                    if (string.Equals(f, exes[0], StringComparison.OrdinalIgnoreCase)) continue;
                    string rel = f.Substring(srcDir.Length).TrimStart('\\', '/');
                    string dest = Path.Combine(appDir, rel);
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(dest));
                        File.Copy(f, dest, true);
                    }
                    catch { }
                }
            }
            finally
            {
                try { Directory.Delete(tmp, true); } catch { }
            }
        }

        /// <summary>Alte EXE vom letzten Update aufräumen (die alte Instanz braucht evtl. noch einen Moment zum Beenden).</summary>
        public static void CleanupOld()
        {
            string exe = Application.ExecutablePath;
            string old = Path.Combine(Path.GetDirectoryName(exe), Path.GetFileNameWithoutExtension(exe) + ".old.exe");
            if (!File.Exists(old)) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                for (int i = 0; i < 30 && File.Exists(old); i++)
                {
                    try { File.Delete(old); } catch { Thread.Sleep(1000); }
                }
            });
        }
    }
}
