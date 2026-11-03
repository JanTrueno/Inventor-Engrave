using System;
using System.Text;
using Inventor;

namespace Engrave.Core
{
    /// <summary>The text that is engraved: "[iProperty value ]part number", the iProperty (for example an order number) being optional.</summary>
    internal static class EngraveText
    {
        private const string Separator = " ";

        /// <summary>
        /// True when the value is the placeholder that means "not filled in yet". When the placeholder has digits, only the
        /// digits are compared, so "160100" also matches "160100UR" and "160100 UR".
        /// </summary>
        public static bool IsPlaceholder(string value, string placeholder)
        {
            if (string.IsNullOrWhiteSpace(placeholder)) return false;
            string digits = Digits(placeholder);
            if (digits.Length > 0) return Digits(value) == digits;
            return string.Equals((value ?? "").Trim(), placeholder.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        public static string Compose(string prefix, PartDocument part, int maxPartChars)
        {
            string name = PartName(part);
            if (maxPartChars > 0 && name.Length > maxPartChars) name = name.Substring(0, maxPartChars);
            prefix = (prefix ?? "").Trim();
            return prefix.Length > 0 ? prefix + Separator + name : name;
        }

        /// <summary>The part number, or the file name, or the display name.</summary>
        public static string PartName(PartDocument part)
        {
            string name = ReadProperty((Document)part, "Part Number");
            if (name.Length == 0) { try { name = System.IO.Path.GetFileNameWithoutExtension(part.FullDocumentName) ?? ""; } catch { } }
            if (name.Trim().Length == 0) { try { name = part.DisplayName ?? ""; } catch { } }
            name = name.Trim();
            return name.Length > 0 ? name : "part";
        }

        /// <summary>
        /// The value of an iProperty by its name as typed (custom properties first, then every other set, by internal or
        /// display name); "" when there is no such property.
        /// </summary>
        public static string ReadProperty(Document doc, string name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0 || doc == null) return "";
            try { return Text(doc.PropertySets["User Defined Properties"][name].Value); } catch { }
            try
            {
                foreach (PropertySet set in doc.PropertySets)
                    foreach (Property p in set)
                    {
                        string display = "";
                        try { display = p.DisplayName; } catch { }
                        if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(display, name, StringComparison.OrdinalIgnoreCase)) return Text(p.Value);
                    }
            }
            catch { }
            return "";
        }

        private static string Text(object value) => (Convert.ToString(value) ?? "").Trim();

        private static string Digits(string s)
        {
            var b = new StringBuilder();
            foreach (char c in s ?? "") if (c >= '0' && c <= '9') b.Append(c);
            return b.ToString();
        }
    }
}
