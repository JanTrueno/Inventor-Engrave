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
            _front.Tick += OnFront;
        }

        private static void OnHide(DockableWindow window, EventTimingEnum timing, NameValueMap context, out HandlingCodeEnum handling)
        {
            handling = HandlingCodeEnum.kEventNotHandled;
            if (timing != EventTimingEnum.kAfter || _hiding || _host == null || _host.Controls.Count == 0) return;
            bool ours;
            try { ours = window?.InternalName == WindowName; } catch { ours = false; }
            if (ours) Closed?.Invoke();
        }

        private static readonly Timer _front = new Timer { Interval = 250 };

        /// <summary>
        /// Brings the Engrave tab to the front a moment from now. Inventor switches its left-hand area to the Model
        /// browser when a part is opened or an edit starts or ends, and has no call to pick a tab, so the window is
        /// hidden and shown again (which Inventor does put in front); <see cref="Closed"/> is not raised for it.
        /// </summary>
        public static void BringToFront()
        {
            if (_window == null || _host == null || _host.Controls.Count == 0) return;
            _front.Stop();
            _front.Start();
        }

        private static void OnFront(object sender, EventArgs e)
        {
            _front.Stop();
            if (_window == null || _host == null || _host.Controls.Count == 0) return;
            _hiding = true;
            try
            {
                _window.Visible = false;
                _window.Visible = true;
            }
            catch { }
            finally { _hiding = false; }
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
            _front.Stop();
            _hiding = true;
            try { if (_window != null) _window.Visible = false; } catch { }
            finally { _hiding = false; }
            try { _host?.Controls.Remove(panel); } catch { }
            try { panel?.Dispose(); } catch { }
        }

        public static void Shutdown()
        {
            try { if (_events != null) _events.OnHide -= OnHide; } catch { }
            _front.Stop();
            _front.Tick -= OnFront;
            _events = null;
            try { _window?.Delete(); } catch { }
            try { _host?.Dispose(); } catch { }
            _window = null;
            _host = null;
        }
    }
}
