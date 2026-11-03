using System;
using System.Drawing;
using System.Windows.Forms;

namespace Engrave.UI
{
    /// <summary>
    /// Colours taken from Inventor's own theme (ThemeManager.GetComponentThemeColor), so the panel follows the
    /// light / dark / highlight theme the user has chosen. The names are those of Inventor's Themes\*.xbel files.
    /// Built-in values (sampled from Inventor's dark theme) are used only if a colour cannot be read.
    /// </summary>
    internal sealed class Theme
    {
        public bool Dark;
        public Color Back, Header, Field, FieldInvalid, Text, Dim, Accent, AccentFill, Hover, Outline, OkBorder, Link;

        public static Theme Detect(Inventor.Application app)
        {
            Inventor.ThemeManager tm = null;
            bool dark = true;
            try
            {
                tm = app.ThemeManager;
                dark = tm.ActiveTheme.Name.IndexOf("light", StringComparison.OrdinalIgnoreCase) < 0;
            }
            catch { /* use the built-in dark values */ }

            InventorIcons.Dark = dark;   // use the matching set of Inventor's own interface icons
            Theme fb = dark ? DarkFallback() : LightFallback();
            if (tm == null) return fb;

            try
            {
                Color back = Read(tm, "WidgetContainer1", fb.Back, fb.Back);
                // If the base colour does not look like the fallback, the names are not what we expect: keep the fallback.
                if (Distance(back, fb.Back) > 90) return fb;

                var t = new Theme { Dark = dark, Back = back };
                t.Header = Read(tm, "RolloutContainerTitleBackgroundColor", fb.Header, back);
                t.Field = Read(tm, "HeaderBorderTop", fb.Field, back);
                t.Text = Read(tm, "FontNormal1", fb.Text, back);
                t.Dim = Read(tm, "Triangle2", fb.Dim, back);
                t.Accent = Read(tm, "ControlActive1", fb.Accent, back);
                t.AccentFill = Read(tm, "MBViewItemSelectedGroupBackground", fb.AccentFill, back);
                t.Hover = Read(tm, "HoverContainer1", fb.Hover, back);
                t.Outline = Read(tm, "WidgetOutline3", fb.Outline, back);
                t.Link = Read(tm, "WidgetOutline6", fb.Link, back);
                t.OkBorder = Lerp(back, t.Accent, 0.4);
                t.FieldInvalid = dark ? Color.FromArgb(105, 45, 48) : Color.FromArgb(255, 205, 205);
                return t;
            }
            catch
            {
                return fb;
            }
        }

        private static Color Read(Inventor.ThemeManager tm, string name, Color fallback, Color under)
        {
            try
            {
                Inventor.Color c = tm.GetComponentThemeColor(name);
                double a = c.Opacity;
                if (a > 1.0) a /= 255.0;           // some builds report 0..255
                if (a <= 0.0 && c.Red == 0 && c.Green == 0 && c.Blue == 0) return fallback; // unknown name
                return Lerp(under, Color.FromArgb(c.Red, c.Green, c.Blue), Math.Min(1.0, a <= 0 ? 1.0 : a));
            }
            catch
            {
                return fallback;
            }
        }

        private static Color Lerp(Color a, Color b, double t) => Color.FromArgb(
            (int)Math.Round(a.R + (b.R - a.R) * t),
            (int)Math.Round(a.G + (b.G - a.G) * t),
            (int)Math.Round(a.B + (b.B - a.B) * t));

        private static int Distance(Color a, Color b) => Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);

        private static Theme DarkFallback() => new Theme
        {
            Dark = true,
            Back = Color.FromArgb(59, 68, 83),
            Header = Color.FromArgb(75, 84, 99),
            Field = Color.FromArgb(46, 52, 64),
            FieldInvalid = Color.FromArgb(105, 45, 48),
            Text = Color.FromArgb(217, 217, 217),
            Dim = Color.FromArgb(134, 145, 161),
            Accent = Color.FromArgb(6, 150, 215),
            AccentFill = Color.FromArgb(58, 98, 124),
            Hover = Color.FromArgb(74, 83, 98),
            Outline = Color.FromArgb(79, 88, 103),
            OkBorder = Color.FromArgb(37, 101, 136),
            Link = Color.FromArgb(56, 171, 223)
        };

        private static Theme LightFallback() => new Theme
        {
            Dark = false,
            Back = Color.FromArgb(240, 241, 243),
            Header = Color.FromArgb(222, 225, 230),
            Field = Color.White,
            FieldInvalid = Color.FromArgb(255, 205, 205),
            Text = Color.FromArgb(30, 32, 36),
            Dim = Color.FromArgb(120, 125, 135),
            Accent = Color.FromArgb(0, 120, 200),
            AccentFill = Color.FromArgb(200, 224, 244),
            Hover = Color.FromArgb(228, 231, 236),
            Outline = Color.FromArgb(190, 195, 203),
            OkBorder = Color.FromArgb(0, 120, 200),
            Link = Color.FromArgb(0, 120, 200)
        };

        /// <summary>Recursively colours the standard controls the panel still uses.</summary>
        public void Apply(Control c)
        {
            switch (c)
            {
                case Button b:
                    b.FlatStyle = FlatStyle.Flat; b.BackColor = Back; b.ForeColor = Text;
                    b.FlatAppearance.BorderColor = Dim;
                    b.FlatAppearance.MouseOverBackColor = Hover;
                    break;
                case ListView lv:
                    lv.BackColor = Field; lv.ForeColor = Text; lv.BorderStyle = BorderStyle.None;
                    break;
                case CheckBox cb:
                    cb.ForeColor = Text;
                    cb.BackColor = Color.Transparent;
                    break;
                case Label l:
                    if (l.Tag as string == "keep") break;
                    l.ForeColor = Text;
                    l.BackColor = Color.Transparent;
                    break;
                default:
                    break;
            }
            foreach (Control child in c.Controls) Apply(child);
        }
    }
}
