using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Engrave.Core
{
    /// <summary>An iProperty in front of the part name, with text to put before and after its value.</summary>
    internal sealed class PropertySpec
    {
        public string Name { get; set; } = "";
        public string Before { get; set; } = "";
        public string After { get; set; } = "";
    }

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

        /// <summary>
        /// The iProperties whose values go in front of the part name, in this order (for example an order number);
        /// read from the open assembly or part.
        /// </summary>
        public List<PropertySpec> Properties { get; set; } = new List<PropertySpec>();

        /// <summary>Older settings files held only names, or one iProperty; they are moved into <see cref="Properties"/> on loading.</summary>
        public List<string> PropertyNames { get; set; } = new List<string>();
        public bool IncludeProperty { get; set; }
        public string PropertyName { get; set; } = "";

        /// <summary>A value that means the iProperty is not filled in yet (for example 160100); refused when found. Empty: none.</summary>
        public string PlaceholderValue { get; set; } = "";

        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { WriteIndented = true, PropertyNameCaseInsensitive = true };

        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Engrave", "settings.json");

        public static EngraveSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    EngraveSettings s = JsonSerializer.Deserialize<EngraveSettings>(File.ReadAllText(FilePath), Json) ?? new EngraveSettings();
                    if (s.Properties == null) s.Properties = new List<PropertySpec>();
                    if (s.PropertyNames == null) s.PropertyNames = new List<string>();
                    if (s.PropertyNames.Count == 0 && s.IncludeProperty && !string.IsNullOrWhiteSpace(s.PropertyName))
                        s.PropertyNames.Add(s.PropertyName.Trim());
                    if (s.Properties.Count == 0)
                        foreach (string n in s.PropertyNames) s.Properties.Add(new PropertySpec { Name = n });
                    s.PropertyNames = new List<string>();
                    s.IncludeProperty = false;
                    s.PropertyName = "";
                    return s;
                }
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
