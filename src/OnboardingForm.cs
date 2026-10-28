using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ReplayClip
{
    /// <summary>Kurze Einführung beim ersten Start (überspringbar, später unter Einstellungen › Über erneut aufrufbar).</summary>
    public sealed class OnboardingForm : Form
    {
        readonly string[][] steps;
        int step;
        readonly Tween fade;
        FxButton bNext, bBack, bSkip;

        static int Px(float v) { return Theme.S(v); }

        public OnboardingForm(Settings s)
        {
            Func<uint, int, string> hk = (m, k) => k != 0 ? HotkeyBox.Format(m, k) : "(kein Kürzel)";
            steps = new[]
            {
                new[] { "record", "Willkommen bei ReplayClip " + Program.Version, "Ein schlanker Begleiter fürs Zocken: Highlights sichern, ganze Sessions aufnehmen und Screenshots machen – mit getrennten Tonspuren und eigener Bibliothek.\n\nDie nächsten Seiten zeigen dir in einer Minute, was alles geht." },
                new[] { "record", "Rewind – nie wieder ein Highlight verpassen", "Oben links im Umschalter: „Rewind“. ReplayClip behält im Hintergrund immer die letzten Minuten.\n\nPassiert etwas Cooles, drück " + hk(s.SaveHotkeyMods, s.SaveHotkeyKey) + " – der Clip landet in der Bibliothek, sortiert nach Spiel.\nPausieren: Einstellungen › Video & Rewind." },
                new[] { "video", "Aufnahme – für Turniere und lange Sessions", "Umschalter auf „Aufnahme“, dann „Aufnahme starten“ oder " + hk(s.ToggleHotkeyMods, s.ToggleHotkeyKey) + ".\n\nOhne Zeitlimit, absturzsicher, mit eigenem Speicherort. Bevor die Platte voll ist, stoppt die Aufnahme von selbst." },
                new[] { "camera", "Screenshots – jederzeit", "Knopf „Screenshot“ oben oder " + hk(s.ShotHotkeyMods, s.ShotHotkeyKey) + " – funktioniert in jedem Modus. Das Bild landet im Screenshot-Ordner und in der Zwischenablage.\n\nDoppelklick in der Bibliothek öffnet den Bild-Editor: zuschneiden, Größe, Format." },
                new[] { "library", "Bibliothek & Editor", "Links zwischen Clips, Aufnahmen und Screenshots wechseln. Maus über eine Kachel halten und hin und her bewegen = Vorschau.\n\n•  Tags: 1–9 drücken oder links bei „Tags“ auf „+“. Klick auf den Farbpunkt = Farbe ändern.\n•  Doppelklick = Editor (kürzen, Abschnitte mit A, Spuren, Lautstärke).\n•  „Für Discord“ rechnet den Clip unter das Upload-Limit." },
                new[] { "settings", "Fast fertig", "Einstellungen (Zahnrad oben rechts) haben oben eine Suche – einfach eintippen, was du suchst.\n\nTipp: Unter „Audiospuren“ legst du fest, was auf welcher Spur landet (z.B. Discord, Spiel, Mikro). Viel Spaß!" },
            };
            Text = "Willkommen";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            BackColor = Theme.Panel;
            ForeColor = Theme.Text;
            Size = new Size(Px(620), Px(420));
            DoubleBuffered = true;
            KeyPreview = true;
            fade = new Tween(this, 1, 12);
            fade.Changed = Invalidate;

            bSkip = new FxButton { Text = "Überspringen", AutoSize = true };
            bSkip.Click += (o, e) => Close();
            bBack = new FxButton { Text = "Zurück", AutoSize = true };
            bBack.Click += (o, e) => Go(step - 1);
            bNext = new FxButton { Text = "Weiter", Kind = FxKind.Primary, AutoSize = false, Size = new Size(Px(150), Px(40)) };
            bNext.Click += (o, e) => { if (step >= steps.Length - 1) Close(); else Go(step + 1); };
            Controls.Add(bSkip);
            Controls.Add(bBack);
            Controls.Add(bNext);
            Resize += (o, e) => LayoutButtons();
            Go(0);
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Theme.RoundCorners(this); }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) Close();
            else if (e.KeyCode == Keys.Right || e.KeyCode == Keys.Enter) bNext.PerformClick();
            else if (e.KeyCode == Keys.Left && step > 0) Go(step - 1);
            base.OnKeyDown(e);
        }

        void Go(int i)
        {
            step = Math.Max(0, Math.Min(steps.Length - 1, i));
            bBack.Visible = step > 0;
            bSkip.Visible = step < steps.Length - 1;
            bNext.Text = step == steps.Length - 1 ? "Los geht's" : "Weiter";
            fade.Set(0);
            fade.To(1);
            LayoutButtons();
            Invalidate();
        }

        void LayoutButtons()
        {
            int y = Height - Px(64);
            foreach (var b in new[] { bSkip, bBack }) if (b.AutoSize) b.Size = b.GetPreferredSize(Size.Empty);
            bNext.Location = new Point(Width - Px(28) - bNext.Width, y);
            bBack.Location = new Point(bNext.Left - Px(10) - bBack.Width, y);
            bSkip.Location = new Point(Px(28), y);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var top = new Rectangle(0, 0, Width, Px(6));
            using (var b = Gfx.Gradient(top, Theme.Accent, Theme.Secondary, 0)) g.FillRectangle(b, top);
            using (var p = new Pen(Theme.Border)) g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
            float a = Anim.EaseOut(fade.Value);
            int dx = (int)((1 - a) * Px(24));
            var st = steps[step];
            // Symbol im Kreis
            var c = new RectangleF(Px(28) + dx, Px(36), Px(52), Px(52));
            using (var b = Gfx.Gradient(c, Gfx.Alpha(Theme.Accent, (int)(255 * a)), Gfx.Alpha(Theme.Secondary, (int)(255 * a)), 45)) g.FillEllipse(b, c);
            Gfx.Icon(g, st[0], 16f, Rectangle.Round(c), Gfx.Alpha(Color.White, (int)(255 * a)));
            TextRenderer.DrawText(g, st[1], Theme.Semi(15f), new Rectangle(Px(96) + dx, Px(40), Width - Px(124), Px(48)), Gfx.Lerp(Theme.Panel, Theme.Text, a), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, st[2], Theme.F(10f), new Rectangle(Px(32) + dx, Px(112), Width - Px(64), Height - Px(200)), Gfx.Lerp(Theme.Panel, Theme.TextDim, a), TextFormatFlags.Left | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            // Fortschrittspunkte
            float dot = Px(8), gap = Px(8), total = steps.Length * dot + (steps.Length - 1) * gap, x0 = (Width - total) / 2;
            for (int i = 0; i < steps.Length; i++)
            {
                var r = new RectangleF(x0 + i * (dot + gap), Height - Px(92), i == step ? dot * 2.2f : dot, dot);
                if (i > step) r.X += dot * 1.2f;
                Gfx.FillRound(g, i == step ? Theme.Accent : Gfx.Alpha(Theme.TextDim, 90), r, dot / 2);
            }
        }
    }
}
