using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace Engrave.UI
{
    /// <summary>Ribbon icons drawn at run time, converted to the IPictureDisp Inventor wants.</summary>
    internal static class Icons
    {
        private sealed class PictureConverter : AxHost
        {
            private PictureConverter() : base(string.Empty) { }
            public static object ToPictureDisp(Image image) => GetIPictureDispFromPicture(image);
        }

        public static object CreateEngrave(int size) => PictureConverter.ToPictureDisp(DrawEngrave(size));

        /// <summary>A grey plate in the style of Inventor's feature icons with "A1" cut into its top face in blue.</summary>
        private static Bitmap DrawEngrave(int size)
        {
            var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            float s = size / 32f;
            using (Graphics g = Graphics.FromImage(bmp))
            using (var outline = new Pen(Color.FromArgb(60, 68, 82), Math.Max(1f, s * 0.8f)))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.Transparent);
                PointF P(float x, float y) => new PointF(x * s, y * s);
                void Face(Color a, Color b, params PointF[] pts)
                {
                    float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
                    foreach (PointF p in pts) { minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y); maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y); }
                    var bounds = new RectangleF(minX, minY, Math.Max(1, maxX - minX), Math.Max(1, maxY - minY));
                    using (var brush = new LinearGradientBrush(bounds, a, b, 90f)) g.FillPolygon(brush, pts);
                    g.DrawPolygon(outline, pts);
                }

                // plate seen from the front and a little above: top face, then the front edge
                Face(Color.FromArgb(214, 221, 232), Color.FromArgb(176, 186, 201), P(5, 6), P(31, 6), P(27, 24), P(1, 24));
                Face(Color.FromArgb(120, 130, 146), Color.FromArgb(84, 93, 108), P(1, 24), P(27, 24), P(27, 28), P(1, 28));

                // the engraved text, slanted with the plate
                using (var font = new Font("Segoe UI", 11f * s, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var shade = new SolidBrush(Color.FromArgb(150, 255, 255, 255)))
                using (var ink = new SolidBrush(Color.FromArgb(16, 112, 196)))
                {
                    var state = g.Save();
                    g.TranslateTransform(16 * s, 15 * s);
                    g.MultiplyTransform(new Matrix(1, 0, -0.22f, 1, 0, 0));
                    var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    g.DrawString("A1", font, shade, 0.6f * s, 0.6f * s, fmt);
                    g.DrawString("A1", font, ink, 0, 0, fmt);
                    g.Restore(state);
                }
            }
            return bmp;
        }
    }
}
