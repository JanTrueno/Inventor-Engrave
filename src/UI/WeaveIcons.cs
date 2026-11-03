using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;

namespace Engrave.UI
{
    /// <summary>
    /// Icons from Autodesk's Weave design system (@weave-design/icons, Apache-2.0), pre-rendered to PNG in a light-on-dark
    /// and a dark-on-light colour at 16, 24 and 32 px and embedded in this assembly. See THIRD-PARTY-NOTICES.txt.
    /// </summary>
    internal static class WeaveIcons
    {
        private static readonly Dictionary<string, Image> Cache = new Dictionary<string, Image>();

        /// <summary>The icon for the active theme at a size suited to the display scale, or null if it is not embedded.</summary>
        public static Image Get(string name)
        {
            string resource = "Engrave.Weave." + name + "_" + SourceSize() + "_" + (InventorIcons.Dark ? "d" : "l") + ".png";
            lock (Cache)
            {
                if (Cache.TryGetValue(resource, out Image cached)) return cached;
                Image img = null;
                try
                {
                    using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
                    {
                        if (stream != null)
                        {
                            using (var png = Image.FromStream(stream)) img = new Bitmap(png);   // detach from the stream
                        }
                    }
                }
                catch { /* draw the fallback instead */ }
                Cache[resource] = img;
                return img;
            }
        }

        /// <summary>Draws the icon scaled into <paramref name="target"/>; false if it is not available.</summary>
        public static bool Draw(Graphics g, Rectangle target, string name)
        {
            Image icon = Get(name);
            if (icon == null) return false;
            InterpolationMode before = g.InterpolationMode;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(icon, target);
            g.InterpolationMode = before;
            return true;
        }

        private static int SourceSize()
        {
            float dpi;
            using (Graphics g = Graphics.FromHwnd(System.IntPtr.Zero)) dpi = g.DpiX;
            return dpi <= 108 ? 16 : dpi <= 162 ? 24 : 32;
        }
    }
}
