using System;
using Inventor;
using Engrave.Core;

namespace Engrave.UI
{
    /// <summary>
    /// The icon of one of Inventor's own commands, read at run time through its ControlDefinition. Nothing is copied:
    /// the picture comes from the running Inventor. Every lookup can fail (null); the caller then draws its own icon.
    /// </summary>
    internal static class InventorCommandIcons
    {
        /// <summary>
        /// The IPictureDisp of the first command found by one of <paramref name="names"/>, else of a command on the
        /// part ribbon whose internal name contains <paramref name="partOfName"/>; null if none has an icon.
        /// </summary>
        public static object Picture(Inventor.Application app, bool large, string partOfName, params string[] names)
        {
            string property = large ? "LargeIcon" : "StandardIcon";
            foreach (string name in names)
            {
                ControlDefinition def = null;
                try { def = app.CommandManager.ControlDefinitions[name]; } catch { /* not by this name */ }
                object pic = Read(def, property);
                if (pic != null) return pic;
            }

            // some definitions only expose their icon once a ribbon has loaded them
            try
            {
                foreach (RibbonTab tab in app.UserInterfaceManager.Ribbons["Part"].RibbonTabs)
                    foreach (RibbonPanel panel in tab.RibbonPanels)
                        foreach (CommandControl cc in panel.CommandControls)
                        {
                            ControlDefinition def = null;
                            try { def = cc.ControlDefinition; } catch { continue; }
                            if (def == null || def.InternalName.IndexOf(partOfName, StringComparison.OrdinalIgnoreCase) < 0) continue;
                            object pic = Read(def, property);
                            if (pic != null) return pic;
                        }
            }
            catch (Exception ex) { Log.Write("Looking for the " + partOfName + " icon failed: " + ex.Message); }
            return null;
        }

        private static object Read(ControlDefinition def, string property)
        {
            if (def == null) return null;
            try { return def.GetType().InvokeMember(property, System.Reflection.BindingFlags.GetProperty, null, def, null); }
            catch { return null; }
        }
    }
}
