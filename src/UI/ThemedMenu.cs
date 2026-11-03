using System.Drawing;
using System.Windows.Forms;

namespace Engrave.UI
{
    /// <summary>Context menu painted in the panel's theme colours instead of the Windows grey.</summary>
    internal sealed class ThemedMenu : ContextMenuStrip
    {
        public ThemedMenu(Theme theme)
        {
            Renderer = new ToolStripProfessionalRenderer(new Colors(theme)) { RoundedEdges = false };
            BackColor = theme.Field;
            ForeColor = theme.Text;
            ShowImageMargin = false;
            Font = new Font("Segoe UI", 9f);
        }

        /// <summary>Applies the theme to a submenu's drop-down, which does not inherit it.</summary>
        public static void Style(ToolStripMenuItem item, Theme theme)
        {
            item.ForeColor = theme.Text;
            if (item.DropDown is ToolStripDropDownMenu dd)
            {
                dd.ShowImageMargin = false;
                dd.BackColor = theme.Field;
                dd.ForeColor = theme.Text;
            }
        }

        private sealed class Colors : ProfessionalColorTable
        {
            private readonly Theme _t;
            public Colors(Theme t) { _t = t; UseSystemColors = false; }

            public override Color ToolStripDropDownBackground => _t.Field;
            public override Color MenuBorder => _t.Outline;
            public override Color MenuItemBorder => _t.AccentFill;
            public override Color MenuItemSelected => _t.AccentFill;
            public override Color MenuItemSelectedGradientBegin => _t.AccentFill;
            public override Color MenuItemSelectedGradientEnd => _t.AccentFill;
            public override Color MenuItemPressedGradientBegin => _t.AccentFill;
            public override Color MenuItemPressedGradientEnd => _t.AccentFill;
            public override Color ImageMarginGradientBegin => _t.Field;
            public override Color ImageMarginGradientMiddle => _t.Field;
            public override Color ImageMarginGradientEnd => _t.Field;
            public override Color SeparatorDark => _t.Outline;
            public override Color SeparatorLight => _t.Field;
        }
    }
}
