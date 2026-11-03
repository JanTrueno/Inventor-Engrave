using System.Collections.Generic;

namespace Engrave.Core
{
    /// <summary>The standard text iProperties, grouped like the tabs of Inventor's iProperties dialog, to pick from.</summary>
    internal static class PropertyCatalog
    {
        public static readonly IReadOnlyList<(string Tab, string[] Names)> Tabs = new[]
        {
            ("Summary", new[] { "Title", "Subject", "Author", "Manager", "Company", "Category", "Keywords", "Comments" }),
            ("Project", new[] { "Part Number", "Stock Number", "Description", "Revision Number", "Project", "Designer",
                                "Engineer", "Authority", "Cost Center", "Vendor" }),
            ("Status", new[] { "Checked By", "Engr Approved By", "Mfg Approved By", "User Status" }),
        };
    }
}
