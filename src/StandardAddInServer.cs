using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Inventor;
using Engrave.UI;

namespace Engrave
{
    /// <summary>
    /// Inventor add-in entry point: one "Engrave" button on the Sheet Metal and 3D Model tabs of the part ribbon, the Assemble tab of the
    /// assembly ribbon and the Place Views tab of the drawing ribbon. It opens a panel docked in Inventor's left-hand area for engraving the part number (and
    /// order number) into sheet metal parts, and for editing or deleting existing engravings.
    /// </summary>
    [ComVisible(true)]
    [Guid(ClientId)]
    [ProgId("Engrave.StandardAddInServer")]
    public class StandardAddInServer : ApplicationAddInServer
    {
        public const string ClientId = "48c0d03d-510f-4800-a5a6-bb6e85513e11";
        private const string PanelName = "Engrave.Panel";

        private Inventor.Application _app;
        private ButtonDefinition _button;
        private UserInterfaceEvents _uiEvents;

        public object Automation => null;

        public void Activate(ApplicationAddInSite addInSiteObject, bool firstTime)
        {
            _app = addInSiteObject.Application;
            string clientGuid = "{" + ClientId + "}";
            UI.Theme.Detect(_app);   // picks the light or dark icon set before anything is drawn

            _button = _app.CommandManager.ControlDefinitions.AddButtonDefinition(
                "Engrave", "Engrave.Start", CommandTypesEnum.kShapeEditCmdType, clientGuid,
                "Engrave the part number into parts",
                "Engrave\nPuts the part number, optionally after the value of an iProperty such as an order number, as text on a face of each part (the top side of sheet metal) and engraves it with Emboss. Works in parts, assemblies and drawings, and lists existing engravings for editing or deleting.",
                Icons.CreateEngrave(16), Icons.CreateEngrave(32), ButtonDisplayEnum.kDisplayTextInLearningMode);
            _button.OnExecute += OnExecute;

            _uiEvents = _app.UserInterfaceManager.UserInterfaceEvents;
            _uiEvents.OnResetRibbonInterface += OnResetRibbon;

            DockHost.Init(_app, clientGuid);
            AddToRibbon();
        }

        public void Deactivate()
        {
            try { _uiEvents.OnResetRibbonInterface -= OnResetRibbon; } catch { }
            try { _button.OnExecute -= OnExecute; } catch { }
            try { EngraveCommand.CancelActive(); } catch { }
            try { DockHost.Shutdown(); } catch { }
            try { _button?.Delete(); } catch { }
            _button = null;
            _uiEvents = null;
            _app = null;
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        public void ExecuteCommand(int commandID) { }

        // ----- ribbon -------------------------------------------------------------------------------------------

        private void OnResetRibbon(NameValueMap context) => AddToRibbon();

        private void AddToRibbon()
        {
            AddTo("Part", "id_TabSheetMetal");
            AddTo("Part", "id_TabModel");
            AddTo("Assembly", "id_TabAssemble");
            AddTo("Drawing", "id_TabPlaceViews");
        }

        private void AddTo(string ribbonName, string tabName)
        {
            try
            {
                RibbonTab tab = _app.UserInterfaceManager.Ribbons[ribbonName].RibbonTabs[tabName];
                RibbonPanel panel;
                try { panel = tab.RibbonPanels[PanelName]; }
                catch { panel = tab.RibbonPanels.Add("Engrave", PanelName, "{" + ClientId + "}", "", false); }
                try { panel.CommandControls.AddButton(_button, true, true, "", false); } catch { /* already added */ }
            }
            catch (Exception ex) { Core.Log.Write("Adding the button to " + ribbonName + "/" + tabName + " failed: " + ex.Message); }
        }

        // ----- command ------------------------------------------------------------------------------------------

        private void OnExecute(NameValueMap context)
        {
            string error = EngraveCommand.Start(_app);
            if (error != null)
                MessageBox.Show(new InventorWindow(_app.MainFrameHWND), error, "Engrave", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
