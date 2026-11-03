
using System.Text;
using Inventor;

namespace Engrave.Core
{
    /// <summary>The text that is engraved: "[last 4 digits of the order number ]part number".</summary>
    internal static class EngraveText
    {
        public const string OrderProperty = "ordernummer";
        private const string Separator = " ";

        /// <summary>
        /// 160100 is the template's default order number and means "not filled in yet" (also "160100UR" or
        /// "160100 UR": only the digits count).
        /// </summary>
        public static bool IsPlaceholder(string order) => Digits(order) == "160100";

        /// <summary>The digits of the order number, of which the last four ("160261496UR" gives "1496"); "" if there are none.</summary>
        public static string ShortOrder(string order)
        {
            string s = Digits(order);
            return s.Length > 4 ? s.Substring(s.Length - 4) : s;
        }

        public static string Compose(string order, PartDocument part, int maxPartChars)
        {
            string name = PartName(part);
            if (maxPartChars > 0 && name.Length > maxPartChars) name = name.Substring(0, maxPartChars);
            string shortOrder = ShortOrder(order);
            return shortOrder.Length > 0 ? shortOrder + Separator + name : name;
        }

        /// <summary>The part number, or the file name, or the display name.</summary>
        public static string PartName(PartDocument part)
        {
            string name = ReadProperty((Document)part, "Design Tracking Properties", "Part Number");
            if (name.Length == 0) { try { name = System.IO.Path.GetFileNameWithoutExtension(part.FullDocumentName) ?? ""; } catch { } }
            if (name.Trim().Length == 0) { try { name = part.DisplayName ?? ""; } catch { } }
            name = name.Trim();
            return name.Length > 0 ? name : "part";
        }

        public static string ReadOrder(Document doc) => ReadProperty(doc, "User Defined Properties", OrderProperty);

        private static string ReadProperty(Document doc, string set, string name)
        {
            try { return (System.Convert.ToString(doc.PropertySets[set][name].Value) ?? "").Trim(); }
            catch { return ""; }
        }

        private static string Digits(string s)
        {
            var b = new StringBuilder();
            foreach (char c in s ?? "") if (c >= '0' && c <= '9') b.Append(c);
            return b.ToString();
        }
    }
}
