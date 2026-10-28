using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace ReplayClip
{
    /// <summary>Screenshot-Editor: zuschneiden (Rahmen ziehen), Größe ändern, drehen, Format wählen, speichern/kopieren.</summary>
    public sealed class ImageEditorForm : Form
    {
        readonly string file;
        Bitmap img;
        Rectangle crop;                 // in Bildkoordinaten
        readonly Canvas canvas;
        FxNumber numW, numH;
        FxToggle chkRatio;
        FxCombo cmbFormat;
        FxSlider trkQuality;
        Label lblInfo, lblQuality;
        bool updating;
        public event Action<string> Saved;
        public event Action<string> DeleteRequested;
        FxButton bDelete;

        static int Px(float v) { return Theme.S(v); }

        public ImageEditorForm(string path)
        {
            file = path;
            Text = "Screenshot bearbeiten – " + Path.GetFileName(path) + "  ·  ReplayClip " + Program.Version;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.F(9f);
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(Px(1200), Px(780));
            MinimumSize = new Size(Px(900), Px(560));
            KeyPreview = true;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var src = Image.FromStream(fs)) img = new Bitmap(src);
            crop = new Rectangle(0, 0, img.Width, img.Height);

            canvas = new Canvas(this) { Dock = DockStyle.Fill };
            var side = new Panel { Dock = DockStyle.Right, Width = Px(320), BackColor = Theme.Panel, Padding = new Padding(Px(16)) };
            var stack = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = Theme.Panel };
            stack.HorizontalScroll.Maximum = 0;
            stack.HorizontalScroll.Visible = false;
            side.Controls.Add(stack);

            Func<string, Label> head = t => new Label { Text = t, AutoSize = true, Font = Theme.Semi(10f), ForeColor = Theme.Text, Margin = new Padding(0, Px(10), 0, Px(6)), Tag = "keep" };
            stack.Controls.Add(head("Zuschneiden"));
            stack.Controls.Add(new Label { Text = "Im Bild einen Rahmen ziehen. Doppelklick = alles.", AutoSize = true, MaximumSize = new Size(Px(260), 0), ForeColor = Theme.TextDim, Tag = "keep" });
            var cropBtns = Ui.Flow(Ui.Button("crop", "Alles", (s, e) => { crop = new Rectangle(0, 0, img.Width, img.Height); CropChanged(); }),
                Ui.Button("rotate", "Drehen", (s, e) => Rotate()));
            stack.Controls.Add(cropBtns);

            stack.Controls.Add(head("Größe"));
            numW = new FxNumber { Minimum = 16, Maximum = 16384, Width = Px(110) };
            numH = new FxNumber { Minimum = 16, Maximum = 16384, Width = Px(110) };
            stack.Controls.Add(Ui.Flow(numW, new Label { Text = "×", AutoSize = true, Margin = new Padding(Px(4), Px(8), Px(4), 0), Tag = "keep", ForeColor = Theme.TextDim }, numH));
            chkRatio = new FxToggle { Text = "Seitenverhältnis beibehalten", Checked = true };
            stack.Controls.Add(chkRatio);
            var presets = new FlowLayoutPanel { AutoSize = true, MaximumSize = new Size(Px(260), 0), WrapContents = true };
            foreach (var p in new[] { "100 %", "75 %", "50 %", "1920 px", "1280 px" })
            {
                string pp = p;
                presets.Controls.Add(Ui.Button(p, (s, e) => Preset(pp)));
            }
            stack.Controls.Add(presets);
            numW.ValueChanged += (s, e) => SizeEdited(true);
            numH.ValueChanged += (s, e) => SizeEdited(false);

            stack.Controls.Add(head("Format"));
            cmbFormat = Ui.Combo(Px(200));
            cmbFormat.Items.Add("PNG (verlustfrei)");
            cmbFormat.Items.Add("JPG (kleiner)");
            cmbFormat.SelectedIndex = Path.GetExtension(path).ToLowerInvariant() == ".png" ? 0 : 1;
            stack.Controls.Add(cmbFormat);
            trkQuality = new FxSlider { Minimum = 50, Maximum = 100, Value = 92, DefaultValue = 92, Width = Px(200) };
            lblQuality = new Label { AutoSize = true, Tag = "keep", ForeColor = Theme.TextDim, Margin = new Padding(Px(6), Px(6), 0, 0) };
            stack.Controls.Add(Ui.Flow(trkQuality, lblQuality));
            trkQuality.ValueChanged += (s, e) => lblQuality.Text = "Qualität " + trkQuality.Value + " %";
            cmbFormat.SelectedIndexChanged += (s, e) => trkQuality.Parent.Visible = cmbFormat.SelectedIndex == 1;
            lblQuality.Text = "Qualität 92 %";

            lblInfo = new Label { AutoSize = true, ForeColor = Theme.TextDim, Margin = new Padding(0, Px(14), 0, Px(6)), Tag = "keep" };
            stack.Controls.Add(lblInfo);
            var bNew = new FxButton { Text = "Als neue Datei speichern", IconName = "save", Kind = FxKind.Primary, Size = new Size(Px(262), Px(40)), Margin = new Padding(0, Px(4), 0, Px(6)) };
            bNew.Click += (s, e) => Save(false);
            var bOver = new FxButton { Text = "Original überschreiben", IconName = "edit", Size = new Size(Px(262), Px(36)), Margin = new Padding(0, 0, 0, Px(6)) };
            bOver.Click += (s, e) => Save(true);
            var bCopy = new FxButton { Text = "In Zwischenablage kopieren", IconName = "copy", Size = new Size(Px(262), Px(36)) };
            bCopy.Click += (s, e) => { using (var b = Render()) Clipboard.SetImage(b); Toast.Show(this, "Kopiert", "Bild in der Zwischenablage (Strg+V).", "copy", Theme.Secondary); };
            stack.Controls.Add(bNew);
            stack.Controls.Add(bOver);
            stack.Controls.Add(bCopy);
            var bDel = bDelete = new FxButton { Text = "Screenshot löschen", IconName = "delete", Kind = FxKind.Danger, Size = new Size(Px(262), Px(36)), Margin = new Padding(0, Px(14), 0, 0) };
            bDel.Click += (s, e) =>
            {
                if (!FxDialog.Ask(this, "Screenshot löschen?", Path.GetFileName(file) + " wird in den Papierkorb verschoben.", "Löschen", "Abbrechen", true)) return;
                Close();
                var h = DeleteRequested;
                if (h != null) h(file);
            };
            stack.Controls.Add(bDel);

            Controls.Add(canvas);
            Controls.Add(side);
            Theme.Apply(this);
            CropChanged();
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Theme.DarkTitleBar(this); }

        readonly Settings keyCfg = Settings.Load();

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyData == keyCfg.EditorKey("close")) Close();
            else if (e.KeyData == keyCfg.EditorKey("save")) Save(false);
            else if (e.KeyData == keyCfg.EditorKey("delete")) bDelete.PerformClick();
            base.OnKeyDown(e);
        }

        void Rotate()
        {
            img.RotateFlip(RotateFlipType.Rotate90FlipNone);
            crop = new Rectangle(img.Width - crop.Bottom, crop.X, crop.Height, crop.Width); // img ist schon gedreht: neue Breite = alte Höhe
            crop.Intersect(new Rectangle(0, 0, img.Width, img.Height));
            CropChanged();
        }

        internal void SetCrop(Rectangle r)
        {
            r.Intersect(new Rectangle(0, 0, img.Width, img.Height));
            if (r.Width < 8 || r.Height < 8) return;
            crop = r;
            CropChanged();
        }

        void CropChanged()
        {
            updating = true;
            numW.Value = crop.Width;
            numH.Value = crop.Height;
            updating = false;
            UpdateInfo();
            canvas.Invalidate();
        }

        void SizeEdited(bool width)
        {
            if (updating) return;
            if (chkRatio.Checked)
            {
                updating = true;
                if (width) numH.Value = Math.Max(16, Math.Round(numW.Value * crop.Height / crop.Width));
                else numW.Value = Math.Max(16, Math.Round(numH.Value * crop.Width / crop.Height));
                updating = false;
            }
            UpdateInfo();
        }

        void Preset(string p)
        {
            updating = true;
            if (p.EndsWith("%")) { int pct = int.Parse(p.Split(' ')[0]); numW.Value = Math.Max(16, crop.Width * pct / 100); numH.Value = Math.Max(16, crop.Height * pct / 100); }
            else { int w = int.Parse(p.Split(' ')[0]); numW.Value = w; numH.Value = Math.Max(16, (decimal)Math.Round(w * (double)crop.Height / crop.Width)); }
            updating = false;
            UpdateInfo();
        }

        void UpdateInfo()
        {
            lblInfo.Text = "Original " + img.Width + " × " + img.Height + "\nAusschnitt " + crop.Width + " × " + crop.Height + "\nErgebnis " + (int)numW.Value + " × " + (int)numH.Value;
        }

        Bitmap Render()
        {
            int w = (int)numW.Value, h = (int)numH.Value;
            var b = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(b))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(img, new Rectangle(0, 0, w, h), crop, GraphicsUnit.Pixel);
            }
            return b;
        }

        void Save(bool overwrite)
        {
            bool jpg = cmbFormat.SelectedIndex == 1;
            string ext = jpg ? ".jpg" : ".png";
            string target;
            if (overwrite)
            {
                if (!FxDialog.Ask(this, "Original überschreiben?", "Der Screenshot wird durch die bearbeitete Version ersetzt.", "Überschreiben", "Abbrechen", true)) return;
                target = Path.ChangeExtension(file, ext);
            }
            else
            {
                string dir = Path.GetDirectoryName(file), bn = Path.GetFileNameWithoutExtension(file) + " (bearbeitet)";
                target = Path.Combine(dir, bn + ext);
                for (int n = 2; File.Exists(target); n++) target = Path.Combine(dir, bn + " " + n + ext);
            }
            try
            {
                using (var b = Render()) SaveImage(b, target, jpg, trkQuality.Value);
                if (overwrite && !string.Equals(target, file, StringComparison.OrdinalIgnoreCase)) { try { File.Delete(file); } catch { } }
            }
            catch (Exception ex) { FxDialog.Info(this, "Speichern fehlgeschlagen", ex.Message, "warning"); return; }
            var h = Saved;
            if (h != null) h(target);
            Close();
        }

        public static void SaveImage(Bitmap b, string path, bool jpg, int quality)
        {
            if (!jpg) { b.Save(path, ImageFormat.Png); return; }
            ImageCodecInfo codec = null;
            foreach (var c in ImageCodecInfo.GetImageEncoders()) if (c.FormatID == ImageFormat.Jpeg.Guid) codec = c;
            using (var ep = new EncoderParameters(1))
            {
                ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);
                b.Save(path, codec, ep);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && img != null) { img.Dispose(); img = null; }
            base.Dispose(disposing);
        }

        /// <summary>Bildfläche: zeigt das Bild eingepasst, Ausschnitt per Ziehen.</summary>
        sealed class Canvas : Control
        {
            readonly ImageEditorForm f;
            Point? dragStart;
            Rectangle dragRect;
            public Canvas(ImageEditorForm owner)
            {
                f = owner;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                Cursor = Cursors.Cross;
            }

            RectangleF ImgRect()
            {
                float pad = Theme.S(20);
                float sc = Math.Min((Width - 2 * pad) / f.img.Width, (Height - 2 * pad) / f.img.Height);
                sc = Math.Min(sc, 4f);
                float w = f.img.Width * sc, h = f.img.Height * sc;
                return new RectangleF((Width - w) / 2, (Height - h) / 2, w, h);
            }

            Point ToImg(Point p)
            {
                var r = ImgRect();
                return new Point((int)Math.Round((p.X - r.X) * f.img.Width / r.Width), (int)Math.Round((p.Y - r.Y) * f.img.Height / r.Height));
            }

            RectangleF ToScreen(Rectangle c)
            {
                var r = ImgRect();
                float sx = r.Width / f.img.Width, sy = r.Height / f.img.Height;
                return new RectangleF(r.X + c.X * sx, r.Y + c.Y * sy, c.Width * sx, c.Height * sy);
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                if (e.Button != MouseButtons.Left) return;
                if (e.Clicks == 2) { f.SetCrop(new Rectangle(0, 0, f.img.Width, f.img.Height)); return; }
                dragStart = ToImg(e.Location);
                dragRect = Rectangle.Empty;
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                if (dragStart == null) return;
                var a = dragStart.Value;
                var b = ToImg(e.Location);
                dragRect = Rectangle.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
                Invalidate();
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e);
                if (dragStart != null && dragRect.Width > 8 && dragRect.Height > 8) f.SetCrop(dragRect);
                dragStart = null;
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.Clear(Theme.Bg);
                if (f.img == null) return;
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                var r = ImgRect();
                g.DrawImage(f.img, r);
                var c = ToScreen(dragStart != null && dragRect.Width > 0 ? dragRect : f.crop);
                using (var reg = new Region(r))
                {
                    reg.Exclude(c);
                    using (var b = new SolidBrush(Color.FromArgb(150, 0, 0, 0))) g.FillRegion(b, reg);
                }
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var p = new Pen(Theme.Accent, 2f * Theme.Dpi)) g.DrawRectangle(p, c.X, c.Y, c.Width, c.Height);
                using (var p = new Pen(Color.FromArgb(70, 255, 255, 255)) { DashStyle = DashStyle.Dash })
                    for (int i = 1; i < 3; i++)
                    {
                        g.DrawLine(p, c.X + c.Width * i / 3, c.Y, c.X + c.Width * i / 3, c.Bottom);
                        g.DrawLine(p, c.X, c.Y + c.Height * i / 3, c.Right, c.Y + c.Height * i / 3);
                    }
            }
        }
    }
}
