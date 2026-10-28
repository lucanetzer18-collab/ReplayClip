using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("ReplayClip")]
[assembly: AssemblyDescription("Replay-Buffer mit NVENC und getrennten Tonspuren")]
[assembly: AssemblyProduct("ReplayClip")]
[assembly: AssemblyVersion("2.3.0.0")]
[assembly: AssemblyFileVersion("2.3.0.0")]
[assembly: ComVisible(false)]

namespace ReplayClip
{
    static class Program
    {
        /// <summary>Anzeige-Version, z.B. "1.3".</summary>
        public static string Version
        {
            get
            {
                return FormatVersion(typeof(Program).Assembly.GetName().Version) + (Channel.Length > 0 ? " " + Channel : "");
            }
        }

        /// <summary>Kanal-Zusatz in der Anzeige (z.B. "Beta"), leer = stabile Version.</summary>
        public const string Channel = "";

        public static string FormatVersion(Version v)
        {
            return v.Major + "." + v.Minor + (v.Build > 0 ? "." + v.Build : "");
        }

        /// <summary>Gerade per Update neu gestartet?</summary>
        public static bool JustUpdated;

        [STAThread]
        static void Main(string[] args)
        {
            bool created;
            bool restart = Array.IndexOf(args, "--restart") >= 0;
            bool replace = Array.IndexOf(args, "--replace") >= 0;
            JustUpdated = Array.IndexOf(args, "--updated") >= 0;
            if (JustUpdated) restart = true;
            using (var mutex = new Mutex(true, "ReplayClip_SingleInstance_v1", out created))
            {
                if (!created && !restart)
                {
                    // Start per Skript (--replace) oder eine andere Version läuft schon:
                    // alte Instanz(en) samt FFmpeg beenden und selbst übernehmen.
                    try
                    {
                        var others = Instances.Others();
                        if (others.Count > 0 && (replace || Instances.AnyFromOtherPath(others)))
                        {
                            Instances.KillAll(others);
                            try { created = mutex.WaitOne(5000); }
                            catch (AbandonedMutexException) { created = true; }
                        }
                    }
                    catch { }
                }
                if (!created && restart)
                {
                    // Neustart (z.B. nach Schriftwechsel): warten, bis die alte Instanz beendet ist
                    try { created = mutex.WaitOne(10000); }
                    catch (AbandonedMutexException) { created = true; }
                }
                if (!created)
                {
                    // Läuft schon -> vorhandenes Fenster nach vorne holen
                    Native.PostMessage(Native.HWND_BROADCAST, MainForm.WM_SHOWME, IntPtr.Zero, IntPtr.Zero);
                    return;
                }
                try { Native.SetProcessDPIAware(); } catch { }
                try { using (var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero)) Theme.Dpi = g.DpiX / 96f; } catch { }
                var cfg = Settings.Load();
                Theme.Configure(cfg);
                Theme.InitFonts(cfg.FontChoice);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += (s, e) => ShowError(e.Exception);
                AppDomain.CurrentDomain.UnhandledException += (s, e) => ShowError(e.ExceptionObject as Exception);

                bool minimized = false;
                foreach (var a in args) if (a == "--minimized" || a == "/minimized") minimized = true;
                if (replace) minimized = false; // per Skript gestartet -> Fenster direkt öffnen
                Application.Run(new MainForm(minimized));
                GC.KeepAlive(mutex);
            }
        }

        static void ShowError(Exception ex)
        {
            try
            {
                MessageBox.Show("Unerwarteter Fehler:\n\n" + (ex == null ? "unbekannt" : ex.ToString()), "ReplayClip", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { }
        }
    }
}
