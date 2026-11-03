using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;

namespace Engrave.UI
{
    /// <summary>
    /// Inventor's own interface icons, read at run time from the resource DLLs of the running installation
    /// (InvImages_*.dll for light themes, InvImagesDark_*.dll for dark ones, one per display scale). Nothing is copied or
    /// redistributed. Every lookup can return null, in which case the caller draws its own fallback.
    /// </summary>
    internal static class InventorIcons
    {
        // Resource ids of the icons used by the panel.
        public const int SelectArrows = 2816;   // 32x16: the select arrow (left half) and its red "selected" variant (right half)

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibraryEx(string file, IntPtr reserved, uint flags);
        [DllImport("kernel32.dll")]
        private static extern bool FreeLibrary(IntPtr module);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern IntPtr FindResource(IntPtr module, IntPtr name, string type);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LoadResource(IntPtr module, IntPtr resource);
        [DllImport("kernel32.dll")]
        private static extern IntPtr LockResource(IntPtr resource);
        [DllImport("kernel32.dll")]
        private static extern uint SizeofResource(IntPtr module, IntPtr resource);

        private const uint LoadLibraryAsDatafile = 0x2;
        private static readonly Dictionary<string, Image> Cache = new Dictionary<string, Image>();
        private static string _binDir;

        /// <summary>True when the dark icon set should be used; set from the active Inventor theme.</summary>
        public static bool Dark = true;

        public static Image Get(int id)
        {
            string key = (Dark ? "d" : "l") + id;
            lock (Cache)
            {
                if (Cache.TryGetValue(key, out Image cached)) return cached;
                Image img = null;
                try { img = Load(id); } catch { /* fall back to the drawn icon */ }
                Cache[key] = img;
                return img;
            }
        }

        /// <summary>The left (normal) 16-px half of the 2816 select-arrows strip.</summary>
        public static Image SelectArrow()
        {
            Image strip = Get(SelectArrows);
            if (strip == null) return null;
            var half = new Bitmap(strip.Width / 2, strip.Height);
            using (Graphics g = Graphics.FromImage(half))
                g.DrawImage(strip, new Rectangle(0, 0, half.Width, half.Height), new Rectangle(0, 0, half.Width, half.Height), GraphicsUnit.Pixel);
            return half;
        }

        private static Image Load(int id)
        {
            string dir = BinDirectory();
            if (dir == null) return null;

            string file = Path.Combine(dir, (Dark ? "InvImagesDark_" : "InvImages_") + ScaleSuffix() + ".dll");
            if (!File.Exists(file)) return null;

            IntPtr module = LoadLibraryEx(file, IntPtr.Zero, LoadLibraryAsDatafile);
            if (module == IntPtr.Zero) return null;
            try
            {
                IntPtr res = FindResource(module, (IntPtr)id, "PNG");
                if (res == IntPtr.Zero) return null;
                IntPtr data = LockResource(LoadResource(module, res));
                var bytes = new byte[SizeofResource(module, res)];
                Marshal.Copy(data, bytes, 0, bytes.Length);
                using (var ms = new MemoryStream(bytes))
                using (var png = Image.FromStream(ms))
                    return new Bitmap(png);   // detach from the stream
            }
            finally { FreeLibrary(module); }
        }

        private static string BinDirectory()
        {
            if (_binDir != null) return _binDir;
            try { _binDir = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName); }
            catch { _binDir = null; }
            return _binDir;
        }

        private static string ScaleSuffix()
        {
            float dpi;
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) dpi = g.DpiX;
            return dpi <= 108 ? "100" : dpi <= 162 ? "150" : "200";
        }
    }
}
