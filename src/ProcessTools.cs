using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace ReplayClip
{
    /// <summary>
    /// Hält alle FFmpeg-Prozesse an ReplayClip gebunden (Windows-Job-Objekt): Wird ReplayClip beendet – auch per
    /// Task-Manager "Task beenden" – schließt Windows den Job und beendet FFmpeg automatisch mit.
    /// </summary>
    public static class ChildJob
    {
        [StructLayout(LayoutKind.Sequential)]
        struct BasicLimit
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct IoCounters { public ulong a, b, c, d, e, f; }

        [StructLayout(LayoutKind.Sequential)]
        struct ExtendedLimit
        {
            public BasicLimit Basic;
            public IoCounters Io;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr attr, string name);
        [DllImport("kernel32.dll")] static extern bool SetInformationJobObject(IntPtr job, int cls, ref ExtendedLimit info, int size);
        [DllImport("kernel32.dll")] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        const uint KillOnJobClose = 0x2000;
        static IntPtr job;
        static bool failed;
        static readonly object l = new object();

        /// <summary>Prozess an ReplayClip koppeln (stirbt mit, wenn ReplayClip endet).</summary>
        public static void Add(Process p)
        {
            if (p == null) return;
            lock (l)
            {
                if (failed) return;
                try
                {
                    if (job == IntPtr.Zero)
                    {
                        job = CreateJobObject(IntPtr.Zero, null);
                        if (job == IntPtr.Zero) { failed = true; return; }
                        var info = new ExtendedLimit();
                        info.Basic.LimitFlags = KillOnJobClose;
                        if (!SetInformationJobObject(job, 9 /*ExtendedLimitInformation*/, ref info, Marshal.SizeOf(typeof(ExtendedLimit)))) { failed = true; return; }
                    }
                    AssignProcessToJobObject(job, p.Handle);
                }
                catch { failed = true; } // z.B. nicht unter Windows
            }
        }
    }

    /// <summary>Andere laufende ReplayClip-Instanzen finden und samt ihren FFmpeg-Prozessen beenden.</summary>
    public static class Instances
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct ProcessEntry
        {
            public uint dwSize, cntUsage, th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID, cntThreads, th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32FirstW(IntPtr snap, ref ProcessEntry e);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32NextW(IntPtr snap, ref ProcessEntry e);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

        /// <summary>(PID, Eltern-PID, Name) aller Prozesse.</summary>
        static List<KeyValuePair<uint, KeyValuePair<uint, string>>> Snapshot()
        {
            var list = new List<KeyValuePair<uint, KeyValuePair<uint, string>>>();
            IntPtr snap = CreateToolhelp32Snapshot(0x2, 0);
            if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return list;
            try
            {
                var e = new ProcessEntry { dwSize = (uint)Marshal.SizeOf(typeof(ProcessEntry)) };
                if (!Process32FirstW(snap, ref e)) return list;
                do list.Add(new KeyValuePair<uint, KeyValuePair<uint, string>>(e.th32ProcessID, new KeyValuePair<uint, string>(e.th32ParentProcessID, e.szExeFile ?? "")));
                while (Process32NextW(snap, ref e));
            }
            finally { CloseHandle(snap); }
            return list;
        }

        /// <summary>PIDs anderer ReplayClip-Prozesse (egal aus welchem Ordner / welche Version).</summary>
        public static List<int> Others()
        {
            var r = new List<int>();
            int me = Process.GetCurrentProcess().Id;
            string myName = Path.GetFileNameWithoutExtension(Process.GetCurrentProcess().MainModule.FileName);
            foreach (var name in new[] { "ReplayClip", myName })
                foreach (var p in Process.GetProcessesByName(name))
                    if (p.Id != me && !r.Contains(p.Id)) r.Add(p.Id);
            return r;
        }

        /// <summary>Läuft eine andere Instanz aus einem anderen Ordner (= andere Version)?</summary>
        public static bool AnyFromOtherPath(List<int> pids)
        {
            string mine = Process.GetCurrentProcess().MainModule.FileName;
            foreach (int pid in pids)
            {
                string p = null;
                try { p = Native.GetProcessPath(pid); } catch { }
                if (p != null && !string.Equals(Path.GetFullPath(p), Path.GetFullPath(mine), StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>Instanzen samt allen Unterprozessen (FFmpeg) beenden und kurz auf das Ende warten.</summary>
        public static void KillAll(List<int> pids)
        {
            var victims = new List<int>(pids);
            try
            {
                var snap = Snapshot();
                // Kinder (und deren Kinder) einsammeln – auch FFmpeg-Prozesse alter Versionen ohne Job-Kopplung
                for (int i = 0; i < victims.Count; i++)
                    foreach (var e in snap)
                        if (e.Value.Key == (uint)victims[i] && !victims.Contains((int)e.Key)) victims.Add((int)e.Key);
            }
            catch { }
            var procs = new List<Process>();
            foreach (int pid in victims)
            {
                try { var p = Process.GetProcessById(pid); p.Kill(); procs.Add(p); } catch { }
            }
            foreach (var p in procs) { try { p.WaitForExit(3000); } catch { } }
        }
    }
}
