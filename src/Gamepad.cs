using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace ReplayClip
{
    /// <summary>
    /// Clip speichern per Controller (XInput: Xbox-Controller, PlayStation über Steam/DS4Windows).
    /// Fragt nur verbundene Controller ab (30x pro Sekunde), nicht verbundene Anschlüsse nur alle 2 Sekunden – kaum CPU.
    /// </summary>
    public static class Gamepad
    {
        [StructLayout(LayoutKind.Sequential)]
        struct State { public uint Packet; public ushort Buttons; public byte LT, RT; public short LX, LY, RX, RY; }

        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")] static extern int GetState14(int idx, out State s);
        [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState")] static extern int GetState910(int idx, out State s);

        public static readonly string[] Names =
        {
            "Aus",
            "View/Back + RB",
            "View/Back + LB",
            "Beide Sticks drücken (L3 + R3)",
            "View/Back + Start/Menü",
            "LB + RB + A"
        };
        static readonly ushort[] Masks = { 0, 0x0020 | 0x0200, 0x0020 | 0x0100, 0x0040 | 0x0080, 0x0020 | 0x0010, 0x0100 | 0x0200 | 0x1000 };

        static Thread thread;
        static volatile int combo;
        static int api; // 0 = xinput1_4, 1 = xinput9_1_0, 2 = keins
        /// <summary>Wird auf einem Hintergrund-Thread ausgelöst, wenn die Kombination gedrückt wurde.</summary>
        public static event Action Pressed;

        public static void SetCombo(int c)
        {
            combo = Math.Max(0, Math.Min(Masks.Length - 1, c));
            if (combo > 0 && thread == null && api < 2)
            {
                thread = new Thread(Loop) { IsBackground = true, Name = "Controller", Priority = ThreadPriority.BelowNormal };
                thread.Start();
            }
        }

        static bool Get(int i, out State s)
        {
            s = new State();
            try { return (api == 0 ? GetState14(i, out s) : GetState910(i, out s)) == 0; }
            catch (DllNotFoundException) { if (api == 0) { api = 1; return Get(i, out s); } api = 2; return false; }
            catch (EntryPointNotFoundException) { api = 2; return false; }
        }

        static void Loop()
        {
            var connected = new bool[4];
            var held = new bool[4];
            long lastScan = 0;
            while (api < 2)
            {
                int c = combo;
                if (c == 0) { Thread.Sleep(500); continue; }
                long now = Environment.TickCount;
                bool scan = now - lastScan > 2000;
                if (scan) lastScan = now;
                for (int i = 0; i < 4; i++)
                {
                    if (!connected[i] && !scan) continue;
                    State s;
                    connected[i] = Get(i, out s);
                    bool down = connected[i] && (s.Buttons & Masks[c]) == Masks[c];
                    if (down && !held[i]) { var h = Pressed; if (h != null) try { h(); } catch { } }
                    held[i] = down;
                }
                Thread.Sleep(33);
            }
        }
    }
}
