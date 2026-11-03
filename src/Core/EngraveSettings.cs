using System;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Engrave.Core
{
    /// <summary>The panel's settings, remembered between sessions in %APPDATA%\Engrave\settings.json.</summary>
    internal sealed class EngraveSettings
    {
        /// <summary>Maximum engraving depth in mm; never more than a quarter of the sheet thickness.</summary>
        public double DepthMm { get; set; } = 0.5;

        /// <summary>Text height in mm; 0 keeps Inventor's default sketch text size.</summary>
        public double TextHeightMm { get; set; }

        /// <summary>The part number is cut off after this many characters (the same limit as the STEP export).</summary>
        public int MaxPartChars { get; set; } = 20;

        /// <summary>
        /// True: the top side is the side with the inside of most bends ("bends up"). Set to false if the CAM software
        /// sees the other side as the top.
        /// </summary>
        public bool TopIsInsideOfBends { get; set; } = true;

        public bool IncludeOrderNumber { get; set; } = true;

        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { WriteIndented = true, PropertyNameCaseInsensitive = true };

        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Engrave", "settings.json");

        public static EngraveSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<EngraveSettings>(File.ReadAllText(FilePath), Json) ?? new EngraveSettings();
            }
            catch (Exception ex) { Log.Write("Reading " + FilePath + " failed: " + ex.Message); }
            return new EngraveSettings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
            }
            catch (Exception ex) { Log.Write("Writing " + FilePath + " failed: " + ex.Message); }
        }

        /// <summary>Formats a number for a field: no trailing zeros, the decimal separator of the user's culture.</summary>
        public static string Format(double v) => v.ToString("0.###", CultureInfo.CurrentCulture);

        /// <summary>Reads a number typed with either a comma or a point as the decimal separator.</summary>
        public static bool TryParse(string text, out double v)
        {
            string t = (text ?? "").Trim().Replace(',', '.');
            return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        }
    }
}
