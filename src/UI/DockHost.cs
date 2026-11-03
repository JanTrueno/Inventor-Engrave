using System;
using System.Windows.Forms;
using Inventor;

namespace Engrave.UI
{
    /// <summary>
    /// Owns the one dockable window (in Inventor's left-hand panel area, next to Model / Properties) that hosts the
    /// Engrave panel while the command runs, the way Inventor's own feature commands do.
    /// </summary>
    internal static class DockHost
    {
        private const string WindowName = "Engrave.DockableWindow";

        private sealed class HostControl : UserControl
        {
            public HostControl() { Dock = DockStyle.Fill; }
        }

        private static DockableWindow _window;
        private static HostControl _host;
        private static DockableWindowsEvents _events;
        private static bool _hiding;   // true while the add-in hides the window itself

        /// <summary>
        /// The user closed the window (the cross on its tab) while a panel was in it. Inventor does not offer to keep
        /// that cross away when the window is docked as a tab, so the command is told instead.
        /// </summary>
        public static event Action Closed;

        public static bool Visible
        {
            get { try { return _window?.Visible == true; } catch { return false; } }
        }

        public static void Init(Inventor.Application app, string clientGuid)
        {
            try { app.UserInterfaceManager.DockableWindows[WindowName].Delete(); } catch { /* none left from before */ }

            _host = new HostControl();
            _ = _host.Handle; // make sure the native window exists before Inventor adopts it
            _window = app.UserInterfaceManager.DockableWindows.Add(clientGuid, WindowName, "Engrave");
            _window.ShowVisibilityCheckBox = false;
            _window.DisableCloseButton = true;
            _window.SetMinimumSize(250, 300);
            _window.AddChild(_host.Handle.ToInt32());
            _window.DockingState = DockingStateEnum.kDockLeft;
            _window.Visible = false;

            _events = app.UserInterfaceManager.DockableWindows.Events;
            _events.OnHide += OnHide;
        }

        private static void OnHide(DockableWindow window, EventTimingEnum timing, NameValueMap context, out HandlingCodeEnum handling)
        {
            handling = HandlingCodeEnum.kEventNotHandled;
            if (timing != EventTimingEnum.kAfter || _hiding || _host == null || _host.Controls.Count == 0) return;
            bool ours;
            try { ours = window?.InternalName == WindowName; } catch { ours = false; }
            if (ours) Closed?.Invoke();
        }

        /// <summary>Brings the window back, for example after its cross was clicked but the command goes on.</summary>
        public static void Reshow()
        {
            try { if (_window != null && !_window.Visible) _window.Visible = true; } catch { }
        }

        public static void Show(Control panel, string title)
        {
            if (_window == null) throw new InvalidOperationException("The docked panel is not available.");
            _host.Controls.Clear();
            panel.Dock = DockStyle.Fill;
            _host.Controls.Add(panel);
            try { _window.Title = title; } catch { /* cosmetic */ }
            _window.Visible = true;
        }

        public static void Hide(Control panel)
        {
            _hiding = true;
            try { if (_window != null) _window.Visible = false; } catch { }
            finally { _hiding = false; }
            try { _host?.Controls.Remove(panel); } catch { }
            try { panel?.Dispose(); } catch { }
        }

        public static void Shutdown()
        {
            try { if (_events != null) _events.OnHide -= OnHide; } catch { }
            _events = null;
            try { _window?.Delete(); } catch { }
            try { _host?.Dispose(); } catch { }
            _window = null;
            _host = null;
        }
    }
}
