using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace ReplayClip
{
    // ======================= COM-Interop: Windows Core Audio (WASAPI) =======================

    [StructLayout(LayoutKind.Sequential)]
    public struct PROPERTYKEY
    {
        public Guid fmtid;
        public int pid;
        public PROPERTYKEY(Guid g, int p) { fmtid = g; pid = p; }
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    public struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr ptr;
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    public class MMDeviceEnumeratorCo { }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int Item(int index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDevice
    {
        [PreserveSig] int Activate([In] ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPropertyStore
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetAt(int index, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
        [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT pv);
        [PreserveSig] int Commit();
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint streamFlags, long hnsBufferDuration, long hnsPeriodicity, IntPtr pFormat, IntPtr audioSessionGuid);
        [PreserveSig] int GetBufferSize(out uint numBufferFrames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint padding);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr pFormat, out IntPtr closestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr deviceFormat);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService([In] ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out int numFramesToRead, out int flags, out long devicePosition, out long qpcPosition);
        [PreserveSig] int ReleaseBuffer(int numFramesRead);
        [PreserveSig] int GetNextPacketSize(out int numFramesInNextPacket);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, int flags, out IntPtr control);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, int flags, out IntPtr volume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object session);
    }

    [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(out IntPtr name);
        [PreserveSig] int SetDisplayName(IntPtr value, IntPtr eventContext);
        [PreserveSig] int GetIconPath(out IntPtr path);
        [PreserveSig] int SetIconPath(IntPtr value, IntPtr eventContext);
        [PreserveSig] int GetGroupingParam(out Guid groupingParam);
        [PreserveSig] int SetGroupingParam(IntPtr value, IntPtr eventContext);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
        [PreserveSig] int GetSessionIdentifier(out IntPtr id);
        [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);
        [PreserveSig] int GetProcessId(out int pid);
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference(bool optOut);
    }

    // ---- Process Loopback (Windows 10 2004+/Windows 11): ActivateAudioInterfaceAsync ----

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IActivateAudioInterfaceAsyncOperation
    {
        [PreserveSig] int GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IActivateAudioInterfaceCompletionHandler
    {
        [PreserveSig] int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
    }

    [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAgileObject { }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public readonly ManualResetEvent Done = new ManualResetEvent(false);
        public int Hr = -1;
        public object Client;

        public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
        {
            try
            {
                int hr;
                object o;
                int r = operation.GetActivateResult(out hr, out o);
                Hr = r < 0 ? r : hr;
                Client = o;
            }
            catch (Exception ex) { Hr = Marshal.GetHRForException(ex); }
            Done.Set();
            return 0;
        }
    }

    public sealed class AudioDeviceInfo
    {
        public string Id;
        public string Name;
        public override string ToString() { return Name; }
    }

    public static class CoreAudio
    {
        public const int eRender = 0, eCapture = 1;
        public const int eConsole = 0, eCommunications = 2;
        public const int CLSCTX_ALL = 23;
        public const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
        public const uint AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;
        public const int AUDCLNT_S_BUFFER_EMPTY = 0x08890001;

        public static readonly Guid IID_IAudioClient = new Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
        public static readonly Guid IID_IAudioCaptureClient = new Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
        public static readonly Guid IID_IAudioSessionManager2 = new Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
        static PROPERTYKEY PKEY_Device_FriendlyName = new PROPERTYKEY(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);

        [DllImport("ole32.dll")] static extern int PropVariantClear(ref PROPVARIANT pv);

        [DllImport("Mmdevapi.dll", ExactSpelling = true)]
        static extern int ActivateAudioInterfaceAsync([MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath, [In] ref Guid riid,
            IntPtr activationParams, IActivateAudioInterfaceCompletionHandler completionHandler, out IActivateAudioInterfaceAsyncOperation operation);

        public static void Check(int hr, string what)
        {
            if (hr < 0) throw new COMException(what + string.Format(" (0x{0:X8})", hr), hr);
        }

        static IMMDeviceEnumerator NewEnumerator()
        {
            return (IMMDeviceEnumerator)new MMDeviceEnumeratorCo();
        }

        static string FriendlyName(IMMDevice dev)
        {
            IPropertyStore store;
            if (dev.OpenPropertyStore(0, out store) < 0 || store == null) return null;
            try
            {
                PROPVARIANT pv;
                if (store.GetValue(ref PKEY_Device_FriendlyName, out pv) < 0) return null;
                string s = (pv.vt == 31 && pv.ptr != IntPtr.Zero) ? Marshal.PtrToStringUni(pv.ptr) : null;
                PropVariantClear(ref pv);
                return s;
            }
            finally { Marshal.ReleaseComObject(store); }
        }

        public static List<AudioDeviceInfo> ListDevices(int flow)
        {
            var list = new List<AudioDeviceInfo>();
            var en = NewEnumerator();
            try
            {
                IMMDeviceCollection col;
                if (en.EnumAudioEndpoints(flow, 1 /*ACTIVE*/, out col) < 0) return list;
                int n;
                col.GetCount(out n);
                for (int i = 0; i < n; i++)
                {
                    IMMDevice dev;
                    if (col.Item(i, out dev) < 0) continue;
                    var d = new AudioDeviceInfo();
                    dev.GetId(out d.Id);
                    d.Name = FriendlyName(dev) ?? d.Id;
                    list.Add(d);
                    Marshal.ReleaseComObject(dev);
                }
                Marshal.ReleaseComObject(col);
            }
            finally { Marshal.ReleaseComObject(en); }
            return list;
        }

        public static string GetDefaultDeviceId(int flow) { return GetDefaultDeviceId(flow, eConsole); }

        public static string GetDeviceName(string id)
        {
            var en = NewEnumerator();
            try
            {
                IMMDevice dev;
                if (en.GetDevice(id, out dev) < 0 || dev == null) return null;
                string n = FriendlyName(dev);
                Marshal.ReleaseComObject(dev);
                return n;
            }
            catch { return null; }
            finally { Marshal.ReleaseComObject(en); }
        }

        public static string GetDefaultDeviceId(int flow, int role)
        {
            var en = NewEnumerator();
            try
            {
                IMMDevice dev;
                if (en.GetDefaultAudioEndpoint(flow, role, out dev) < 0 || dev == null) return null;
                string id;
                dev.GetId(out id);
                Marshal.ReleaseComObject(dev);
                return id;
            }
            finally { Marshal.ReleaseComObject(en); }
        }

        /// <summary>Alle Prozess-IDs, die gerade eine Audio-Session auf einem Wiedergabegerät haben.</summary>
        public static List<int> ListSessionPids()
        {
            var pids = new List<int>();
            var en = NewEnumerator();
            try
            {
                IMMDeviceCollection col;
                if (en.EnumAudioEndpoints(eRender, 1, out col) < 0) return pids;
                int n;
                col.GetCount(out n);
                for (int i = 0; i < n; i++)
                {
                    IMMDevice dev;
                    if (col.Item(i, out dev) < 0) continue;
                    try
                    {
                        Guid iid = IID_IAudioSessionManager2;
                        object o;
                        if (dev.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out o) < 0 || o == null) continue;
                        var mgr = (IAudioSessionManager2)o;
                        IAudioSessionEnumerator se;
                        if (mgr.GetSessionEnumerator(out se) >= 0 && se != null)
                        {
                            int cnt;
                            se.GetCount(out cnt);
                            for (int k = 0; k < cnt; k++)
                            {
                                object so;
                                if (se.GetSession(k, out so) < 0 || so == null) continue;
                                var ctl = so as IAudioSessionControl2;
                                if (ctl != null)
                                {
                                    int state, pid;
                                    ctl.GetState(out state);
                                    if (state != 2 /*expired*/ && ctl.IsSystemSoundsSession() != 0 && ctl.GetProcessId(out pid) >= 0 && pid > 0 && !pids.Contains(pid))
                                        pids.Add(pid);
                                }
                                Marshal.ReleaseComObject(so);
                            }
                            Marshal.ReleaseComObject(se);
                        }
                        Marshal.ReleaseComObject(mgr);
                    }
                    catch { }
                    finally { Marshal.ReleaseComObject(dev); }
                }
                Marshal.ReleaseComObject(col);
            }
            finally { Marshal.ReleaseComObject(en); }
            return pids;
        }

        /// <summary>IAudioClient für ein Aufnahmegerät (id == null/leer: Standardgerät).</summary>
        public static IAudioClient ActivateDevice(string id, int flow)
        {
            var en = NewEnumerator();
            try
            {
                IMMDevice dev;
                int hr = string.IsNullOrEmpty(id) ? en.GetDefaultAudioEndpoint(flow, eConsole, out dev) : en.GetDevice(id, out dev);
                Check(hr, "Audiogerät nicht gefunden");
                try
                {
                    Guid iid = IID_IAudioClient;
                    object o;
                    Check(dev.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out o), "Audiogerät konnte nicht geöffnet werden");
                    return (IAudioClient)o;
                }
                finally { Marshal.ReleaseComObject(dev); }
            }
            finally { Marshal.ReleaseComObject(en); }
        }

        /// <summary>
        /// Process-Loopback: nimmt nur den Ton eines Prozessbaums auf (include=true)
        /// oder alles AUSSER diesem Prozessbaum (include=false).
        /// </summary>
        public static IAudioClient ActivateProcessLoopback(int pid, bool include, int timeoutMs)
        {
            IntPtr loopParams = Marshal.AllocHGlobal(12);
            IntPtr pv = Marshal.AllocHGlobal(24);
            try
            {
                Marshal.WriteInt32(loopParams, 0, 1);                 // AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK
                Marshal.WriteInt32(loopParams, 4, pid);               // TargetProcessId
                Marshal.WriteInt32(loopParams, 8, include ? 0 : 1);   // INCLUDE_/EXCLUDE_TARGET_PROCESS_TREE
                for (int i = 0; i < 24; i += 4) Marshal.WriteInt32(pv, i, 0);
                Marshal.WriteInt16(pv, 0, 65);                        // VT_BLOB
                Marshal.WriteInt32(pv, 8, 12);                        // blob.cbSize
                Marshal.WriteIntPtr(pv, 16, loopParams);              // blob.pBlobData (x64)

                var handler = new ActivationHandler();
                Guid iid = IID_IAudioClient;
                IActivateAudioInterfaceAsyncOperation op;
                Check(ActivateAudioInterfaceAsync("VAD\\Process_Loopback", ref iid, pv, handler, out op), "Process-Loopback nicht verfügbar");
                if (!handler.Done.WaitOne(timeoutMs)) throw new TimeoutException("Process-Loopback: Zeitüberschreitung");
                Check(handler.Hr, "Process-Loopback-Aktivierung fehlgeschlagen");
                if (op != null) Marshal.ReleaseComObject(op);
                return (IAudioClient)handler.Client;
            }
            finally
            {
                Marshal.FreeHGlobal(pv);
                Marshal.FreeHGlobal(loopParams);
            }
        }
    }

    // ======================= Format / Konvertierung =======================

    public sealed class WaveFmt
    {
        public int Channels, Rate, Bits, BlockAlign;
        public bool IsFloat;
        static readonly Guid SubtypeFloat = new Guid("00000003-0000-0010-8000-00aa00389b71");

        public static WaveFmt Parse(IntPtr p)
        {
            var f = new WaveFmt();
            int tag = (ushort)Marshal.ReadInt16(p, 0);
            f.Channels = Marshal.ReadInt16(p, 2);
            f.Rate = Marshal.ReadInt32(p, 4);
            f.BlockAlign = Marshal.ReadInt16(p, 12);
            f.Bits = Marshal.ReadInt16(p, 14);
            int cb = (ushort)Marshal.ReadInt16(p, 16);
            if (tag == 0xFFFE && cb >= 22)
            {
                var b = new byte[16];
                Marshal.Copy(new IntPtr(p.ToInt64() + 24), b, 0, 16);
                f.IsFloat = new Guid(b) == SubtypeFloat;
            }
            else f.IsFloat = tag == 3;
            return f;
        }

        public static IntPtr AllocPcm16Stereo48k()
        {
            IntPtr p = Marshal.AllocCoTaskMem(18);
            Marshal.WriteInt16(p, 0, 1);         // WAVE_FORMAT_PCM
            Marshal.WriteInt16(p, 2, 2);         // Kanäle
            Marshal.WriteInt32(p, 4, 48000);     // Abtastrate
            Marshal.WriteInt32(p, 8, 48000 * 4); // Bytes/s
            Marshal.WriteInt16(p, 12, 4);        // BlockAlign
            Marshal.WriteInt16(p, 14, 16);       // Bits
            Marshal.WriteInt16(p, 16, 0);        // cbSize
            return p;
        }
    }

    /// <summary>Wandelt beliebige WASAPI-Pakete in 48 kHz / Stereo / float um.</summary>
    public sealed class SampleConverter
    {
        readonly WaveFmt fmt;
        float[] tmp = new float[0];
        float[] outBuf = new float[0];
        // Zustand des linearen Resamplers
        double frac;
        float prevL, prevR;
        readonly double step;

        public SampleConverter(WaveFmt f)
        {
            fmt = f;
            step = (double)f.Rate / AudioEngine.Rate;
        }

        public unsafe void Process(IntPtr data, int frames, bool silent, AudioSource sink)
        {
            if (frames <= 0) return;
            if (tmp.Length < frames * 2) tmp = new float[frames * 2];
            if (silent || data == IntPtr.Zero)
            {
                Array.Clear(tmp, 0, frames * 2);
            }
            else
            {
                byte* b = (byte*)data.ToPointer();
                int bytes = fmt.Bits / 8;
                for (int i = 0; i < frames; i++)
                {
                    byte* f = b + (long)i * fmt.BlockAlign;
                    float l = ReadSample(f, bytes);
                    float r = fmt.Channels > 1 ? ReadSample(f + bytes, bytes) : l;
                    tmp[2 * i] = l;
                    tmp[2 * i + 1] = r;
                }
            }

            if (fmt.Rate == AudioEngine.Rate)
            {
                sink.Write(tmp, frames);
                return;
            }

            // Lineares Resampling (Position 0 = letzter Frame des vorherigen Pakets)
            int maxOut = (int)(frames / step) + 4;
            if (outBuf.Length < maxOut * 2) outBuf = new float[maxOut * 2];
            int o = 0;
            double t = frac;
            while (t < frames && o < maxOut)
            {
                int i = (int)t;
                double w = t - i;
                float aL = i == 0 ? prevL : tmp[2 * (i - 1)];
                float aR = i == 0 ? prevR : tmp[2 * (i - 1) + 1];
                float bL = tmp[2 * i], bR = tmp[2 * i + 1];
                outBuf[2 * o] = (float)(aL + (bL - aL) * w);
                outBuf[2 * o + 1] = (float)(aR + (bR - aR) * w);
                o++;
                t += step;
            }
            frac = t - frames;
            prevL = tmp[2 * (frames - 1)];
            prevR = tmp[2 * (frames - 1) + 1];
            sink.Write(outBuf, o);
        }

        unsafe float ReadSample(byte* p, int bytes)
        {
            if (fmt.IsFloat)
            {
                if (bytes == 4) return *(float*)p;
                if (bytes == 8) return (float)*(double*)p;
                return 0f;
            }
            switch (bytes)
            {
                case 2: return *(short*)p / 32768f;
                case 3: return ((p[0] << 8) | (p[1] << 16) | (p[2] << 24)) / 2147483648f;
                case 4: return *(int*)p / 2147483648f;
                case 1: return (p[0] - 128) / 128f;
            }
            return 0f;
        }
    }

    // ======================= Ringpuffer pro Quelle =======================

    public sealed class AudioSource
    {
        const int Cap = AudioEngine.Rate * 2;          // 2 s
        const int Prebuffer = AudioEngine.Rate * 30 / 1000;  // 30 ms Jitterpuffer
        const int High = Prebuffer + AudioEngine.Rate * 30 / 1000;
        const int Low = Prebuffer / 3;
        readonly float[] buf = new float[Cap * 2];
        readonly object l = new object();
        int rpos, count;
        bool primed;

        public static int LatencyMs { get { return 30 + 10; } }

        // Pegel (für die Anzeige)
        float peak;
        long peakTicks;
        public float MaxEver;
        public long LastDataTicks;

        static float Decay(float level, long fromTicks, long nowTicks)
        {
            double dt = (nowTicks - fromTicks) / (double)System.Diagnostics.Stopwatch.Frequency;
            return (float)(level * Math.Pow(0.5, dt / 0.3));
        }

        public float Level
        {
            get { lock (l) return Decay(peak, peakTicks, System.Diagnostics.Stopwatch.GetTimestamp()); }
        }

        public void Write(float[] src, int frames)
        {
            float pk = 0f;
            for (int i = 0; i < frames * 2; i++)
            {
                float a = src[i] < 0 ? -src[i] : src[i];
                if (a > pk) pk = a;
            }
            lock (l)
            {
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                peak = Math.Max(Decay(peak, peakTicks, now), pk);
                peakTicks = now;
                LastDataTicks = now;
                if (pk > MaxEver) MaxEver = pk;
                int offset = 0;
                if (frames > Cap) { offset = frames - Cap; frames = Cap; }
                int overflow = count + frames - Cap;
                if (overflow > 0) { rpos = (rpos + overflow) % Cap; count -= overflow; }
                int wpos = (rpos + count) % Cap;
                int first = Math.Min(frames, Cap - wpos);
                Array.Copy(src, offset * 2, buf, wpos * 2, first * 2);
                if (frames > first) Array.Copy(src, (offset + first) * 2, buf, 0, (frames - first) * 2);
                count += frames;
            }
        }

        void Skip(int frames)
        {
            frames = Math.Min(frames, count);
            rpos = (rpos + frames) % Cap;
            count -= frames;
        }

        /// <summary>Mischt 'frames' Frames (mit Verstärkung) in dst. Gleicht kleine Taktabweichungen aus.</summary>
        public void MixInto(float[] dst, int frames, float gain, bool catchUp)
        {
            lock (l)
            {
                if (catchUp)
                {
                    if (count > Prebuffer) Skip(count - Prebuffer);
                    return;
                }
                if (!primed)
                {
                    if (count < Prebuffer) return;
                    primed = true;
                }
                if (count > High * 4) Skip(count - Prebuffer);   // nach Hänger: Latenz zurücksetzen
                else if (count > High) Skip(1);                  // Quelle läuft minimal schneller
                bool stretch = count < Low && count > 1;          // Quelle läuft minimal langsamer

                int toRead = Math.Min(frames, count);
                if (stretch && toRead == frames) toRead = frames - 1;
                int pos = rpos;
                float lastL = 0f, lastR = 0f;
                for (int i = 0; i < toRead; i++)
                {
                    lastL = buf[2 * pos] * gain;
                    lastR = buf[2 * pos + 1] * gain;
                    dst[2 * i] += lastL;
                    dst[2 * i + 1] += lastR;
                    pos++;
                    if (pos == Cap) pos = 0;
                }
                Skip(toRead);
                if (toRead == frames - 1 && stretch && toRead > 0)
                {
                    dst[2 * toRead] += lastL;       // letzten Frame wiederholen
                    dst[2 * toRead + 1] += lastR;
                }
                else if (toRead < frames)
                {
                    primed = false; // Unterlauf -> Stille, neu puffern
                }
            }
        }
    }

    // ======================= Aufnahme-Thread pro Quelle =======================

    public sealed class WasapiCapture
    {
        public readonly AudioSource Sink;
        readonly bool isProcess;
        readonly string deviceId;
        readonly int pid;
        readonly bool include;
        Thread th;
        volatile bool stop;
        public volatile bool Failed;
        public volatile bool Running;
        public string Error;
        public string Description;     // z.B. "discord.exe (PID 1234, inkl. Unterprozesse)"
        public string FormatInfo = ""; // tatsächliches Aufnahmeformat
        public long Packets, SilentPackets;
        public readonly DateTime Created = DateTime.UtcNow;
        public bool IsDevice { get { return !isProcess; } }

        const uint AUTOCONVERTPCM = 0x80000000;
        const uint SRC_DEFAULT_QUALITY = 0x08000000;

        WasapiCapture(AudioSource sink, bool isProcess, string deviceId, int pid, bool include)
        {
            Sink = sink; this.isProcess = isProcess; this.deviceId = deviceId; this.pid = pid; this.include = include;
        }

        public static WasapiCapture ForDevice(string deviceId, AudioSource sink, string description)
        {
            var c = new WasapiCapture(sink, false, deviceId, 0, false);
            c.Description = description;
            return c;
        }

        public static WasapiCapture ForProcess(int pid, bool include, AudioSource sink, string description)
        {
            var c = new WasapiCapture(sink, true, null, pid, include);
            c.Description = description;
            return c;
        }

        public void Start()
        {
            th = new Thread(Run);
            th.IsBackground = true;
            th.Name = "Capture " + Description;
            th.SetApartmentState(ApartmentState.MTA);
            th.Priority = ThreadPriority.AboveNormal;
            th.Start();
        }

        public void Stop()
        {
            stop = true;
            if (th != null) th.Join(2000);
        }

        sealed class Variant
        {
            public uint Flags;
            public long Buffer;
            public bool OwnFormat;   // eigenes 48-kHz-PCM-Format statt Geräteformat
            public string Name;
        }

        void Run()
        {
            IAudioClient client = null;
            IAudioCaptureClient cap = null;
            IntPtr fmtPtr = IntPtr.Zero;
            var ev = new AutoResetEvent(false);
            try
            {
                const uint LB = CoreAudio.AUDCLNT_STREAMFLAGS_LOOPBACK, EV = CoreAudio.AUDCLNT_STREAMFLAGS_EVENTCALLBACK;
                Variant[] variants = isProcess
                    ? new[]
                    {
                        // wie Microsofts ApplicationLoopback-Beispiel (plus etwas größerer Puffer)
                        new Variant { Flags = LB | EV | AUTOCONVERTPCM | SRC_DEFAULT_QUALITY, Buffer = 2000000, OwnFormat = true, Name = "Event" },
                        new Variant { Flags = LB | EV | AUTOCONVERTPCM, Buffer = 0, OwnFormat = true, Name = "Event/Standardpuffer" },
                        new Variant { Flags = LB | AUTOCONVERTPCM, Buffer = 2000000, OwnFormat = true, Name = "Polling" },
                    }
                    : new[]
                    {
                        new Variant { Flags = EV, Buffer = 2000000, OwnFormat = false, Name = "Event" },
                        new Variant { Flags = 0, Buffer = 2000000, OwnFormat = false, Name = "Polling" },
                        new Variant { Flags = EV | AUTOCONVERTPCM | SRC_DEFAULT_QUALITY, Buffer = 2000000, OwnFormat = true, Name = "Event/Konvertierung" },
                    };

                int hr = -1;
                Variant used = null;
                string lastErr = "";
                foreach (var v in variants)
                {
                    if (stop) return;
                    try
                    {
                        client = isProcess ? CoreAudio.ActivateProcessLoopback(pid, include, 5000) : CoreAudio.ActivateDevice(deviceId, CoreAudio.eCapture);
                        if (v.OwnFormat) fmtPtr = WaveFmt.AllocPcm16Stereo48k();
                        else CoreAudio.Check(client.GetMixFormat(out fmtPtr), "GetMixFormat");
                        hr = client.Initialize(0, v.Flags, v.Buffer, 0, fmtPtr, IntPtr.Zero);
                        if (hr >= 0) { used = v; break; }
                        lastErr = string.Format("Initialize 0x{0:X8}", hr);
                    }
                    catch (Exception ex) { lastErr = ex.Message; }
                    try { if (client != null) Marshal.ReleaseComObject(client); } catch { }
                    client = null;
                    if (fmtPtr != IntPtr.Zero) { Marshal.FreeCoTaskMem(fmtPtr); fmtPtr = IntPtr.Zero; }
                }
                if (used == null) throw new Exception("Aufnahme konnte nicht gestartet werden: " + lastErr);

                var wf = WaveFmt.Parse(fmtPtr);
                FormatInfo = string.Format("{0} Hz, {1} Kanal/Kanäle, {2} Bit{3}, {4}", wf.Rate, wf.Channels, wf.Bits, wf.IsFloat ? " float" : "", used.Name);
                bool useEvent = (used.Flags & EV) != 0;
                if (useEvent) CoreAudio.Check(client.SetEventHandle(ev.SafeWaitHandle.DangerousGetHandle()), "SetEventHandle");
                Guid g = CoreAudio.IID_IAudioCaptureClient;
                object svc;
                CoreAudio.Check(client.GetService(ref g, out svc), "GetService");
                cap = (IAudioCaptureClient)svc;
                var conv = new SampleConverter(wf);
                CoreAudio.Check(client.Start(), "Start");
                Running = true;

                while (!stop)
                {
                    if (useEvent) ev.WaitOne(100); else Thread.Sleep(5);
                    while (!stop)
                    {
                        int packet;
                        CoreAudio.Check(cap.GetNextPacketSize(out packet), "GetNextPacketSize");
                        if (packet == 0) break;
                        IntPtr data; int frames, bflags; long pos, qpc;
                        hr = cap.GetBuffer(out data, out frames, out bflags, out pos, out qpc);
                        if (hr == CoreAudio.AUDCLNT_S_BUFFER_EMPTY) break;
                        CoreAudio.Check(hr, "GetBuffer");
                        bool silent = (bflags & 2) != 0;
                        Packets++;
                        if (silent) SilentPackets++;
                        conv.Process(data, frames, silent, Sink);
                        cap.ReleaseBuffer(frames);
                    }
                }
                client.Stop();
            }
            catch (Exception ex)
            {
                Error = ex.Message;
                Failed = true;
            }
            finally
            {
                Running = false;
                try { if (cap != null) Marshal.ReleaseComObject(cap); } catch { }
                try { if (client != null) Marshal.ReleaseComObject(client); } catch { }
                if (fmtPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(fmtPtr);
                ev.Close();
            }
        }
    }
}
