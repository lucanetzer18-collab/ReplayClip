using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace ReplayClip
{
    public sealed class ProcInfo
    {
        public int Pid;
        public int Parent;
        public string Name; // lower case, without ".exe"
    }

    public sealed class MonitorInfo
    {
        public int Index;
        public string DeviceName;
        public int Width, Height;
        public bool Primary;
        public override string ToString()
        {
            return string.Format("Monitor {0}: {1} x {2}{3}", Index + 1, Width, Height, Primary ? "  (Hauptbildschirm)" : "");
        }
    }

    public static class Native
    {
        public const int WM_HOTKEY = 0x0312;
        public const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;

        [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int RegisterWindowMessage(string msg);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr w, IntPtr l);
        public static readonly IntPtr HWND_BROADCAST = new IntPtr(0xffff);

        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder sb, ref int size);
        [DllImport("kernel32.dll")] static extern void GetSystemTimePreciseAsFileTime(out long fileTime);
        [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint ms);
        [DllImport("kernel32.dll")] public static extern bool SetProcessWorkingSetSize(IntPtr proc, IntPtr min, IntPtr max);
        [DllImport("winmm.dll")] public static extern uint timeEndPeriod(uint ms);

        /// <summary>UTC-Zeit in Mikrosekunden seit 1970 (gleiche Basis wie av_gettime() in FFmpeg).</summary>
        public static long UtcNowMicros()
        {
            long ft;
            try { GetSystemTimePreciseAsFileTime(out ft); }
            catch (EntryPointNotFoundException) { ft = DateTime.UtcNow.ToFileTimeUtc(); }
            return ft / 10 - 11644473600000000L;
        }

        // ---------------- Prozesse (Toolhelp) ----------------
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct PROCESSENTRY32W
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
        }
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool Process32FirstW(IntPtr snap, ref PROCESSENTRY32W pe);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool Process32NextW(IntPtr snap, ref PROCESSENTRY32W pe);

        public static string ExeKey(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            string s = name.Trim().ToLowerInvariant();
            if (s.EndsWith(".exe")) s = s.Substring(0, s.Length - 4);
            return s;
        }

        public static Dictionary<int, ProcInfo> SnapshotProcesses()
        {
            var result = new Dictionary<int, ProcInfo>();
            IntPtr snap = CreateToolhelp32Snapshot(2 /*TH32CS_SNAPPROCESS*/, 0);
            if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return result;
            try
            {
                var pe = new PROCESSENTRY32W();
                pe.dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32W));
                if (Process32FirstW(snap, ref pe))
                {
                    do
                    {
                        var p = new ProcInfo();
                        p.Pid = (int)pe.th32ProcessID;
                        p.Parent = (int)pe.th32ParentProcessID;
                        p.Name = ExeKey(pe.szExeFile);
                        result[p.Pid] = p;
                    } while (Process32NextW(snap, ref pe));
                }
            }
            finally { CloseHandle(snap); }
            return result;
        }

        /// <summary>Wurzelprozesse eines Programms (z.B. der Haupt-Discord.exe, nicht die Kindprozesse).</summary>
        public static List<int> FindRoots(Dictionary<int, ProcInfo> procs, string name)
        {
            var roots = new List<int>();
            foreach (var p in procs.Values)
            {
                if (p.Name != name) continue;
                ProcInfo parent;
                bool parentSame = p.Parent != p.Pid && procs.TryGetValue(p.Parent, out parent) && parent.Name == name;
                if (!parentSame) roots.Add(p.Pid);
            }
            return roots;
        }

        /// <summary>Gehört der Prozess (oder einer seiner Elternprozesse) zu einer der angegebenen Apps?</summary>
        public static bool IsInTree(Dictionary<int, ProcInfo> procs, int pid, HashSet<string> names)
        {
            int cur = pid;
            for (int depth = 0; depth < 32; depth++)
            {
                ProcInfo p;
                if (!procs.TryGetValue(cur, out p)) return false;
                if (names.Contains(p.Name)) return true;
                if (p.Parent == cur || p.Parent <= 0) return false;
                cur = p.Parent;
            }
            return false;
        }

        public static bool HasAncestorIn(Dictionary<int, ProcInfo> procs, int pid, HashSet<int> set)
        {
            ProcInfo p;
            if (!procs.TryGetValue(pid, out p)) return false;
            int cur = p.Parent;
            for (int depth = 0; depth < 32 && cur > 0; depth++)
            {
                if (set.Contains(cur)) return true;
                ProcInfo q;
                if (!procs.TryGetValue(cur, out q) || q.Parent == cur) return false;
                cur = q.Parent;
            }
            return false;
        }

        public static string GetProcessPath(int pid)
        {
            IntPtr h = OpenProcess(0x1000 /*PROCESS_QUERY_LIMITED_INFORMATION*/, false, (uint)pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                if (QueryFullProcessImageName(h, 0, sb, ref size)) return sb.ToString();
                return null;
            }
            finally { CloseHandle(h); }
        }

        /// <summary>Anzeigename der App im Vordergrund – bestimmt den Unterordner (wie bei NVIDIA).</summary>
        public static string ForegroundAppName()
        {
            try
            {
                IntPtr hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero) return "Desktop";
                uint pid;
                GetWindowThreadProcessId(hwnd, out pid);
                if (pid == 0 || pid == (uint)Process.GetCurrentProcess().Id) return "Desktop";
                string path = GetProcessPath((int)pid);
                if (path != null)
                {
                    string exe = ExeKey(Path.GetFileName(path));
                    if (exe == "explorer" || exe == "shellexperiencehost" || exe == "searchhost" || exe == "startmenuexperiencehost" || exe == "lockapp")
                        return "Desktop";
                    try
                    {
                        var vi = FileVersionInfo.GetVersionInfo(path);
                        string n = (vi.FileDescription ?? "").Trim();
                        if (n.Length < 2 || n.Length > 60) n = (vi.ProductName ?? "").Trim();
                        if (n.Length >= 2 && n.Length <= 60) return n;
                    }
                    catch { }
                    return Path.GetFileNameWithoutExtension(path);
                }
                var sb = new StringBuilder(256);
                GetWindowText(hwnd, sb, sb.Capacity);
                string title = sb.ToString().Trim();
                return title.Length > 0 ? title : "Desktop";
            }
            catch { return "Desktop"; }
        }

        /// <summary>Prüft die Windows-Datenschutzeinstellung "Mikrofonzugriff" (global / für Desktop-Apps).</summary>
        public static bool MicPrivacyBlocked()
        {
            const string basePath = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";
            try
            {
                foreach (var hive in new[] { Microsoft.Win32.Registry.CurrentUser, Microsoft.Win32.Registry.LocalMachine })
                {
                    foreach (var sub in new[] { basePath, basePath + @"\NonPackaged" })
                    {
                        if (hive == Microsoft.Win32.Registry.LocalMachine && sub != basePath) continue;
                        using (var k = hive.OpenSubKey(sub))
                        {
                            if (k == null) continue;
                            var v = k.GetValue("Value") as string;
                            if (v != null && v.Equals("Deny", StringComparison.OrdinalIgnoreCase)) return true;
                        }
                    }
                }
            }
            catch { }
            return false;
        }

        public static string SafeFileName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "Unbekannt";
            var sb = new StringBuilder();
            foreach (char c in s)
            {
                if (Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 || c < 32) sb.Append('_');
                else sb.Append(c);
            }
            string r = sb.ToString().Trim().TrimEnd('.');
            if (r.Length > 60) r = r.Substring(0, 60).Trim();
            return r.Length == 0 ? "Unbekannt" : r;
        }

        /// <summary>Führt eine Funktion auf einem eigenen MTA-Thread aus (für Core-Audio-COM aus dem UI-Thread).</summary>
        public static T RunMta<T>(Func<T> f, T fallback)
        {
            T result = fallback;
            var th = new Thread(() => { try { result = f(); } catch { } });
            th.IsBackground = true;
            th.SetApartmentState(ApartmentState.MTA);
            th.Start();
            th.Join(10000);
            return result;
        }

        // ---------------- Monitore (DXGI, gleiche Reihenfolge wie FFmpeg ddagrab) ----------------
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DXGI_OUTPUT_DESC
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            public int Left, Top, Right, Bottom;
            public int AttachedToDesktop;
            public int Rotation;
            public IntPtr Monitor;
        }

        [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IDXGIFactory1
        {
            void _SetPrivateData(); void _SetPrivateDataInterface(); void _GetPrivateData(); void _GetParent();
            [PreserveSig] int EnumAdapters(uint index, out IDXGIAdapter adapter);
        }

        [ComImport, Guid("2411e7e1-12ac-4ccf-bd14-9798e8534dc0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IDXGIAdapter
        {
            void _SetPrivateData(); void _SetPrivateDataInterface(); void _GetPrivateData(); void _GetParent();
            [PreserveSig] int EnumOutputs(uint index, out IDXGIOutput output);
        }

        [ComImport, Guid("ae02eedb-c735-4690-8d52-5a8dc20213aa"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IDXGIOutput
        {
            void _SetPrivateData(); void _SetPrivateDataInterface(); void _GetPrivateData(); void _GetParent();
            [PreserveSig] int GetDesc(out DXGI_OUTPUT_DESC desc);
        }

        [DllImport("dxgi.dll")]
        static extern int CreateDXGIFactory1([In] ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IDXGIFactory1 factory);

        public static List<MonitorInfo> GetMonitors()
        {
            var list = new List<MonitorInfo>();
            try
            {
                Guid iid = typeof(IDXGIFactory1).GUID;
                IDXGIFactory1 factory;
                if (CreateDXGIFactory1(ref iid, out factory) >= 0 && factory != null)
                {
                    IDXGIAdapter adapter;
                    if (factory.EnumAdapters(0, out adapter) >= 0 && adapter != null)
                    {
                        for (uint i = 0; i < 16; i++)
                        {
                            IDXGIOutput output;
                            if (adapter.EnumOutputs(i, out output) < 0 || output == null) break;
                            DXGI_OUTPUT_DESC d;
                            if (output.GetDesc(out d) >= 0)
                            {
                                var m = new MonitorInfo();
                                m.Index = (int)i;
                                m.DeviceName = d.DeviceName;
                                m.Width = d.Right - d.Left;
                                m.Height = d.Bottom - d.Top;
                                foreach (var s in Screen.AllScreens)
                                    if (string.Equals(s.DeviceName, d.DeviceName, StringComparison.OrdinalIgnoreCase)) m.Primary = s.Primary;
                                list.Add(m);
                            }
                            Marshal.ReleaseComObject(output);
                        }
                        Marshal.ReleaseComObject(adapter);
                    }
                    Marshal.ReleaseComObject(factory);
                }
            }
            catch { }
            if (list.Count == 0)
            {
                int i = 0;
                foreach (var s in Screen.AllScreens)
                {
                    var m = new MonitorInfo();
                    m.Index = i++;
                    m.DeviceName = s.DeviceName;
                    m.Width = s.Bounds.Width;
                    m.Height = s.Bounds.Height;
                    m.Primary = s.Primary;
                    list.Add(m);
                }
            }
            return list;
        }
    }
}
