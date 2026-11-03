using System;
using System.Collections.Generic;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;
using Inventor;
using Engrave.Core;

namespace Engrave.UI
{
    /// <summary>
    /// The interactive "Engrave" command. In an assembly it walks the visible parts that can be changed, sheet metal
    /// first (each part file once):
    /// the part is highlighted, its top side green, and a click on a face puts the text there as a sketch. The user then
    /// drags or turns the text in Inventor's sketch environment and clicks Finish Sketch, after which the shallow cut is
    /// made and the next part is up. In a part the same is done for that part. In a drawing every sheet is one row: the
    /// part of its first view is opened, and the text is "part number - sheet number". The panel also lists the existing
    /// engravings for editing or deleting. Cancel undoes everything done since the command started. Nothing is saved.
    /// </summary>
    internal sealed class EngraveCommand
    {
        private const int SketchPollMs = 200;

        /// <summary>A part file; <see cref="Occ"/> is null when the part itself is open.</summary>
        private sealed class Item
        {
            public ComponentOccurrence Occ;
            public PartDocument Part;
            public string Name;
            public bool Visible = true;
            public bool SheetMetal;
            public int Sheet;   // drawing mode: the sheet number, 0 otherwise
            public string Label => Sheet > 0 ? "Sheet " + Sheet + " \u00B7 " + Name : Name;
            public TopSide Side;
            public string Text;
            public bool TextEdited, Done, Skipped;
        }

        private static EngraveCommand _active;

        private readonly Inventor.Application _app;
        private readonly Document _top;
        private readonly string _topName;
        private readonly AssemblyDocument _asm;
        private readonly DrawingDocument _drawing;
        private readonly List<Document> _opened = new List<Document>();   // parts this command opened a window for
        private readonly InventorWindow _owner;
        private readonly List<Item> _items = new List<Item>();
        private readonly List<int> _walk = new List<int>();   // the items the panel's part list shows (the visible ones)
        private readonly List<string> _warnings = new List<string>();
        private readonly Timer _wait = new Timer { Interval = SketchPollMs };
        private readonly EngraveSettings _settings = EngraveSettings.Load();

        private EngravePanel _panel;
        private ApplicationEvents _appEvents;
        private InteractionEvents _events;
        private SelectEvents _select;
        private HighlightSet _hiPart, _hiFaces;
        private int _current = -1;
        private PanelMode _mode = PanelMode.Picking;
        private PlanarSketch _sketch;
        private Item _sketchItem;
        private bool _cancelSketch;
        private ComponentOccurrence _editedOcc;   // the last occurrence opened for in-place editing
        private int _engraved;
        private int _changes;                     // engravings, edits and deletions in this run
        private CheckPoint _start;                // Cancel goes back to here
        private bool _finished;

        private EngraveCommand(Inventor.Application app, Document top)
        {
            _app = app;
            _top = top;
            _asm = top as AssemblyDocument;
            _drawing = top as DrawingDocument;
            _owner = new InventorWindow(app.MainFrameHWND);
            try { _topName = top.FullDocumentName; } catch { _topName = ""; }
        }

        public static bool IsRunning => _active != null;

        /// <summary>Ends the running command, if any (used when the add-in unloads).</summary>
        public static void CancelActive() => _active?.Finish(false, false);

        /// <summary>Starts the command on the active document; returns an error message, or null.</summary>
        public static string Start(Inventor.Application app)
        {
            if (_active != null) return "Engrave is already running. Click Done in its panel first.";
            Document doc = app.ActiveDocument;
            if (!(doc is AssemblyDocument) && !(doc is PartDocument) && !(doc is DrawingDocument))
                return "Open an assembly, a part or a drawing first.";

            var cmd = new EngraveCommand(app, doc);
            string error = cmd.Collect();
            if (error != null) return error;

            _active = cmd;
            try { cmd.Run(); }
            catch (Exception ex)
            {
                Log.Write("Starting Engrave failed: " + ex);
                cmd.Finish(false, false);
                return "Engrave could not start: " + ex.Message;
            }
            return null;
        }

        // ----- start up -----------------------------------------------------------------------------------------

        /// <summary>
        /// The part files that can be changed (not read-only, so no library or Content Center parts): in an assembly every
        /// leaf occurrence that is not suppressed, each file once and preferably by a visible occurrence. Hidden parts are
        /// left out of the walk but their engravings are listed. Sheet metal parts come first in the walk.
        /// </summary>
        private string Collect()
        {
            if (_drawing != null) return CollectSheets();
            if (_top is PartDocument part)
            {
                if (!Modifiable(part)) return "This part is read-only (a library or Content Center part) and cannot be engraved.";
                _items.Add(new Item { Part = part, Name = EngraveText.PartName(part), SheetMetal = part.ComponentDefinition is SheetMetalComponentDefinition });
            }
            else
            {
                var byFile = new Dictionary<string, Item>(StringComparer.OrdinalIgnoreCase);
                foreach (ComponentOccurrence occ in _asm.ComponentDefinition.Occurrences.AllLeafOccurrences)
                {
                    try
                    {
                        if (occ.Suppressed) continue;
                        if (!(occ.Definition.Document is PartDocument p)) continue;
                        if (!Modifiable(p)) continue;
                        bool visible = occ.Visible;
                        if (byFile.TryGetValue(p.FullDocumentName, out Item seen))
                        {
                            if (visible && !seen.Visible) { seen.Occ = occ; seen.Visible = true; }
                            continue;
                        }
                        var item = new Item { Occ = occ, Part = p, Name = EngraveText.PartName(p), Visible = visible, SheetMetal = p.ComponentDefinition is SheetMetalComponentDefinition };
                        byFile[p.FullDocumentName] = item;
                        _items.Add(item);
                    }
                    catch { /* e.g. a virtual component without a document */ }
                }
                if (_items.Count == 0) return "No parts found in this assembly that can be engraved.";
            }
            for (int i = 0; i < _items.Count; i++) if (_items[i].Visible && _items[i].SheetMetal) _walk.Add(i);
            for (int i = 0; i < _items.Count; i++) if (_items[i].Visible && !_items[i].SheetMetal) _walk.Add(i);
            return null;
        }

        /// <summary>Drawing mode: one item per sheet, for the part shown in its first view (other sheets are left out).</summary>
        private string CollectSheets()
        {
            int number = 0;
            foreach (Sheet sheet in _drawing.Sheets)
            {
                number++;
                try
                {
                    if (sheet.DrawingViews.Count == 0) continue;
                    if (!(sheet.DrawingViews[1].ReferencedDocumentDescriptor.ReferencedDocument is PartDocument p)) continue;
                    if (!Modifiable(p)) continue;
                    _items.Add(new Item { Part = p, Name = EngraveText.PartName(p), Sheet = number, SheetMetal = p.ComponentDefinition is SheetMetalComponentDefinition });
                }
                catch { /* view without a readable part */ }
            }
            if (_items.Count == 0) return "No sheet in this drawing shows a part that can be engraved.";
            for (int i = 0; i < _items.Count; i++) _walk.Add(i);
            return null;
        }

        private static bool Modifiable(PartDocument part)
        {
            try { return ((Document)part).IsModifiable; } catch { return true; }
        }

        private void Run()
        {
            _panel = new EngravePanel(Theme.Detect(_app), _asm != null || _drawing != null);
            if (_drawing != null) _panel.ShowTextOptions(false);   // the drawing text is fixed: part number - sheet
            _panel.LoadSettings(_settings);
            _panel.SetProperty(_settings.IncludeProperty, _settings.PropertyName, EngraveText.ReadProperty(_top, _settings.PropertyName));

            _panel.OrderChanged += OnOrderChanged;
            _panel.PropertyNameChanged += OnPropertyNameChanged;
            _panel.CustomProperties = CustomProperties;
            _panel.TextEdited += OnTextEdited;
            _panel.PartChosen += k => Choose(_walk[k]);
            _panel.PickClicked += () => { if (_events == null) StartPicking(); else StopPicking(); };
            _panel.PrimaryClicked += OnPrimary;
            _panel.SecondaryClicked += OnSecondary;
            _panel.EditRequested += OnEdit;
            _panel.DeleteRequested += OnDelete;
            _panel.DeleteAllRequested += OnDeleteAll;
            _panel.SettingsChanged += OnSettingsChanged;

            if (_drawing == null) CreateHighlightSets(_top);
            _wait.Tick += (s, e) => { if (!InSketch()) SketchClosed(); };
            _panel.TertiaryClicked += OnSkip;
            try { _start = _app.TransactionManager.SetCheckPoint(); }
            catch (Exception ex) { Log.Write("Setting the undo check point failed: " + ex.Message); }

            _appEvents = _app.ApplicationEvents;
            _appEvents.OnCloseDocument += OnCloseDocument;

            DockHost.Show(_panel, "Engrave");
            ShowOrderHint();
            RefreshExisting();
            if (_walk.Count > 0) Choose(_walk[0]);
            else _panel.SetStatus("All parts are hidden; their engravings are listed below.", false);
        }

        /// <summary>The orange (part) and green (top side) highlights, in the document the user picks in.</summary>
        private void CreateHighlightSets(Document doc)
        {
            try { _hiPart?.Delete(); } catch { }
            try { _hiFaces?.Delete(); } catch { }
            _hiPart = Create(255, 140, 0);
            _hiFaces = Create(0, 200, 0);

            HighlightSet Create(int r, int g, int b)
            {
                HighlightSet set = doc is AssemblyDocument a ? a.CreateHighlightSet() : ((PartDocument)doc).CreateHighlightSet();
                set.Color = _app.TransientObjects.CreateColor((byte)r, (byte)g, (byte)b);
                return set;
            }
        }

        /// <summary>
        /// Drawing mode: opens the part of the item in its own window (or brings it to the front), so its faces can be
        /// picked. The windows this command opened are closed again at the end; the parts stay loaded through the drawing.
        /// </summary>
        private void ShowPart(Item item)
        {
            if (_drawing == null) return;
            var doc = (Document)item.Part;
            bool hadWindow = false;
            try { hadWindow = doc.Views.Count > 0; } catch { }
            var shown = (Document)_app.Documents.Open(doc.FullFileName, true);
            try { shown.Activate(); } catch { }
            if (!hadWindow && !_opened.Contains(shown)) _opened.Add(shown);
            CreateHighlightSets(shown);
        }

        // ----- the part being engraved --------------------------------------------------------------------------

        private void Choose(int index)
        {
            if (_mode != PanelMode.Picking || index < 0 || index >= _items.Count) return;
            StopPicking();
            _current = index;
            Item item = _items[index];
            try { ShowPart(item); }
            catch (Exception ex)
            {
                _panel.SetStatus("Could not open " + item.Name + ": " + ex.Message, true);
                RefreshParts();
                return;
            }
            if (item.Side == null) item.Side = TopSide.Find(item.Part, _settings.TopIsInsideOfBends);
            if (!item.TextEdited) item.Text = ComposeText(item);
            _panel.SetText(item.Text);
            string info = item.Side.Info;
            if (Engraver.Find(item.Part) != null) info += " This part is engraved already; a new engraving replaces it.";
            _panel.SetInfo(info);
            _panel.SetStatus("", false);
            Highlight(item);
            RefreshParts();
            StartPicking();
        }

        private string ComposeText(Item item) => item.Sheet > 0
            ? EngraveText.PartName(item.Part) + " - " + item.Sheet
            : EngraveText.Compose(_panel.IncludeProperty ? _panel.PropertyValue : "", item.Part, _settings.MaxPartChars);

        /// <summary>The part in orange; its top side in green on top of that, so it is clear where to click.</summary>
        private void Highlight(Item item)
        {
            if (_hiPart == null || _hiFaces == null) return;
            try
            {
                _hiPart.Clear();
                _hiFaces.Clear();
                if (item.Occ != null) _hiPart.AddItem(item.Occ);
                if (item.Side.Faces.Count == 0) { _app.ActiveView.Update(); return; }   // every flat or round face can be picked
                foreach (Face f in item.Part.ComponentDefinition.SurfaceBodies[1].Faces)
                {
                    try
                    {
                        if (!item.Side.Faces.Contains(f.TransientKey)) continue;
                        if (item.Occ == null) { _hiFaces.AddItem(f); continue; }
                        item.Occ.CreateGeometryProxy(f, out object proxy);
                        _hiFaces.AddItem(proxy);
                    }
                    catch { }
                }
                _app.ActiveView.Update();
            }
            catch (Exception ex) { Log.Write("Highlighting failed: " + ex.Message); }
        }

        /// <summary>Next part that is neither engraved nor skipped in this run, after the current one.</summary>
        private void Advance()
        {
            int start = Math.Max(0, _walk.IndexOf(_current));
            for (int n = 1; n <= _walk.Count; n++)
            {
                Item item = _items[_walk[(start + n) % _walk.Count]];
                if (!item.Done && !item.Skipped)
                {
                    Choose(_walk[(start + n) % _walk.Count]);
                    return;
                }
            }
            StopPicking();
            RefreshParts();
            _panel.SetStatus(_engraved + (_engraved == 1 ? " part" : " parts") + " engraved. Click Done to finish, or pick a part to do it again.", false);
        }

        private void RefreshParts()
        {
            var rows = new List<PartRow>();
            foreach (int i in _walk)
            {
                Item item = _items[i];
                bool engraved = Engraver.Find(item.Part) != null;
                string status = item.Done ? "Engraved" : item.Skipped ? "Skipped" : engraved ? "Engraved before" : item.SheetMetal ? "" : "Not sheet metal";
                rows.Add(new PartRow { Name = item.Label, Status = status, Done = item.Done && engraved, Engraved = engraved });
            }
            _panel.SetParts(rows, _walk.IndexOf(_current));
        }

        private void RefreshExisting()
        {
            var rows = new List<EngravingRow>();
            var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // a part on several sheets is listed once
            for (int i = 0; i < _items.Count; i++)
            {
                if (!listed.Add(((Document)_items[i].Part).FullFileName)) continue;
                PartFeature f = Engraver.Find(_items[i].Part);
                if (f == null) continue;
                bool suppressed = false;
                try { suppressed = f.Suppressed; } catch { }
                rows.Add(new EngravingRow
                {
                    Part = i, Text = Engraver.TextOf(f), Suppressed = suppressed,
                    Name = _items[i].Label + (_items[i].Visible ? "" : " (hidden)")
                });
            }
            _panel.SetExisting(rows);
        }

        // ----- iProperty in front, text and settings ------------------------------------------------------------

        /// <summary>The iProperty value may be used: it is off, or it is not the placeholder for "not filled in".</summary>
        private bool OrderValid()
        {
            if (_drawing != null || !_panel.IncludeProperty) return true;
            return !EngraveText.IsPlaceholder(_panel.PropertyValue, _settings.PlaceholderValue);
        }

        private void ShowOrderHint()
        {
            if (!_panel.IncludeProperty) _panel.SetOrderHint("", false);
            else if (_panel.PropertyName.Length == 0)
                _panel.SetOrderHint("Type the name of the iProperty, for example an order number property.", false);
            else if (EngraveText.IsPlaceholder(_panel.PropertyValue, _settings.PlaceholderValue))
                _panel.SetOrderHint(_settings.PlaceholderValue + " means “" + _panel.PropertyName + "” is not filled in yet. Type the right value.", true);
            else if (_panel.PropertyValue.Length == 0)
                _panel.SetOrderHint("“" + _panel.PropertyName + "” is empty here: the text is the part number only.", false);
            else _panel.SetOrderHint("The value is put in front of the part number.", false);
        }

        /// <summary>The custom iProperties of the open assembly or part with their values, sorted by name.</summary>
        private IList<KeyValuePair<string, string>> CustomProperties()
        {
            var list = new List<KeyValuePair<string, string>>();
            try
            {
                foreach (Property p in _top.PropertySets["User Defined Properties"])
                {
                    string value = "";
                    try { value = (Convert.ToString(p.Value) ?? "").Trim(); } catch { }
                    list.Add(new KeyValuePair<string, string>(p.Name, value));
                }
            }
            catch { }
            list.Sort((x, y) => string.Compare(x.Key, y.Key, StringComparison.CurrentCultureIgnoreCase));
            return list;
        }

        /// <summary>A new iProperty name: read its value from the open assembly or part.</summary>
        private void OnPropertyNameChanged()
        {
            _panel.SetPropertyValue(EngraveText.ReadProperty(_top, _panel.PropertyName));
            OnOrderChanged();
        }

        private void OnOrderChanged()
        {
            ShowOrderHint();
            RecomposeTexts();
            if (_mode != PanelMode.Picking || _current < 0) return;
            if (OrderValid()) { if (_events == null) StartPicking(); }
            else StopPicking();
        }

        private void RecomposeTexts()
        {
            foreach (Item item in _items) if (!item.TextEdited) item.Text = null;
            if (_current < 0) return;
            Item cur = _items[_current];
            if (!cur.TextEdited)
            {
                cur.Text = ComposeText(cur);
                _panel.SetText(cur.Text);
            }
        }

        private void OnTextEdited()
        {
            if (_current < 0) return;
            Item item = _items[_current];
            item.Text = _panel.EngravingText;
            item.TextEdited = item.Text.Length > 0;   // clearing the field goes back to the composed text
        }

        private void OnSettingsChanged()
        {
            bool topInside = _settings.TopIsInsideOfBends;
            if (_panel.ReadSettings(_settings) != null) return;
            OnOrderChanged();   // the placeholder or the name length may have changed
            if (topInside != _settings.TopIsInsideOfBends)
            {
                foreach (Item item in _items) item.Side = null;
                if (_current >= 0) Choose(_current);
            }
        }

        // ----- picking a face -----------------------------------------------------------------------------------

        private void StartPicking()
        {
            if (_finished || _mode != PanelMode.Picking || _current < 0 || _events != null) return;
            if (!OrderValid())
            {
                _panel.SetPick(false, "");
                _panel.SetStatus("Fill in the iProperty value, or untick the iProperty.", true);
                return;
            }
            _events = _app.CommandManager.CreateInteractionEvents();
            _events.InteractionDisabled = false;
            _select = _events.SelectEvents;
            _select.AddSelectionFilter(SelectionFilterEnum.kPartFacePlanarFilter);
            _select.AddSelectionFilter(SelectionFilterEnum.kPartFaceCylindricalFilter);
            _select.SingleSelectEnabled = true;
            _select.WindowSelectEnabled = false;
            _select.OnPreSelect += OnPreSelect;
            _select.OnSelect += OnSelect;
            _events.OnTerminate += OnTerminate;
            _events.StatusBarText = "Select the face for the text on the highlighted part (green faces only, where shown)";
            _events.Start();
            _panel.SetPick(true, "");
        }

        private void StopPicking()
        {
            InteractionEvents events = _events;
            _events = null;
            if (events != null)
            {
                try { _select.OnPreSelect -= OnPreSelect; } catch { }
                try { _select.OnSelect -= OnSelect; } catch { }
                try { events.OnTerminate -= OnTerminate; } catch { }
                try { events.Stop(); } catch { }
            }
            _select = null;
            _panel?.SetPick(false, "");
        }

        /// <summary>Another command or Esc ended the picking; the command itself stays open.</summary>
        private void OnTerminate()
        {
            StopPicking();
            if (!_finished) _panel.SetStatus("Picking stopped. Click the Face field to pick again.", false);
        }

        private void OnPreSelect(ref object entity, out bool doHighlight, ref ObjectCollection more, SelectionDeviceEnum device,
                                 Point modelPosition, Point2d viewPosition, Inventor.View view)
        {
            doHighlight = true;
            try { doHighlight = Accepts(entity, out _, out _); } catch { }
        }

        private void OnSelect(ObjectsEnumerator justSelected, SelectionDeviceEnum device, Point modelPosition, Point2d viewPosition, Inventor.View view)
        {
            if (_finished || _events == null || justSelected.Count == 0) return;
            object picked = justSelected[1];
            if (!Accepts(picked, out Face face, out ComponentOccurrence occ))
            {
                _panel.SetStatus("Pick a flat or round face of the highlighted part (a green one, where shown).", true);
                return;
            }
            Item item = _items[_current];
            StopPicking();
            // Leave Inventor's event first: entering the sketch environment from inside it is not reliable.
            _panel.BeginInvoke(new Action(() => BeginSketch(item, occ, face, modelPosition, view)));
        }

        /// <summary>A face of the current part (any occurrence of it) on its top side.</summary>
        private bool Accepts(object entity, out Face face, out ComponentOccurrence occ)
        {
            face = null;
            occ = null;
            if (_current < 0) return false;
            Item item = _items[_current];
            if (entity is FaceProxy proxy)
            {
                occ = proxy.ContainingOccurrence;
                if (!SameFile(occ.Definition.Document as Document, item.Part)) return false;
                face = proxy.NativeObject;
            }
            else if (entity is Face native)
            {
                // a part opened by itself, or the part being edited in place in the assembly
                var def = native.SurfaceBody.ComponentDefinition as PartComponentDefinition;
                if (def == null || !SameFile(def.Document as Document, item.Part)) return false;
                face = native;
                occ = _asm == null ? null : (_editedOcc != null && SameFile(_editedOcc.Definition.Document as Document, item.Part) ? _editedOcc : item.Occ);
            }
            else return false;
            return Engraver.CanEngrave(face) && (item.Side == null || item.Side.Allows(face));
        }

        private static bool SameFile(Document doc, PartDocument part)
        {
            try { return doc != null && string.Equals(doc.FullDocumentName, part.FullDocumentName, StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        // ----- the text sketch ----------------------------------------------------------------------------------

        private void BeginSketch(Item item, ComponentOccurrence occ, Face face, Point point, Inventor.View view)
        {
            if (_finished) return;
            string error = _panel.ReadSettings(_settings);
            if (error != null) { _panel.SetStatus(error, true); StartPicking(); return; }
            string text = item.Text ?? ComposeText(item);
            if (text.Trim().Length == 0)
            {
                _panel.MarkTextInvalid();
                _panel.SetStatus("Type the text to engrave.", true);
                StartPicking();
                return;
            }

            var warnings = new List<string>();
            PlanarSketch sketch = null;
            try
            {
                sketch = Engraver.Start(_app, item.Part, occ, face, point, view, text.Trim(), _settings, warnings);
                if (occ != null) { occ.Edit(); _editedOcc = occ; }
                sketch.Edit();
            }
            catch (Exception ex)
            {
                Log.Write("Placing the text on " + item.Part.FullDocumentName + " failed: " + ex);
                if (sketch != null) Engraver.Abandon(item.Part, sketch);
                _panel.SetStatus("Could not place the text: " + ex.Message, true);
                StartPicking();
                return;
            }
            Report(item, warnings);
            EnterSketchMode(PanelMode.Sketching, sketch, item,
                "Drag the text into place (turn it with Sketch > Rotate if needed), then click Finish Sketch.");
        }

        private void EnterSketchMode(PanelMode mode, PlanarSketch sketch, Item item, string hint)
        {
            _mode = mode;
            _sketch = sketch;
            _sketchItem = item;
            _cancelSketch = false;
            _panel.SetMode(mode, false);
            _panel.SetStatus(hint, false);
            try { _app.StatusBarText = hint; } catch { }
            _wait.Start();
        }

        private bool InSketch()
        {
            try { return _app.ActiveEditObject is PlanarSketch; } catch { return false; }
        }

        private void ExitSketch()
        {
            try { if (InSketch()) _sketch?.ExitEdit(); } catch (Exception ex) { Log.Write("Leaving the sketch failed: " + ex.Message); }
        }

        /// <summary>The user left the sketch (Finish Sketch in Inventor or in the panel, or Cancel): cut, or roll back.</summary>
        private void SketchClosed()
        {
            _wait.Stop();
            PanelMode mode = _mode;
            Item item = _sketchItem;
            PlanarSketch sketch = _sketch;
            _sketch = null;
            _sketchItem = null;
            _mode = PanelMode.Picking;
            try { _app.StatusBarText = ""; } catch { }
            if (_finished || item == null) return;

            var warnings = new List<string>();
            bool engraved = false;
            string status = "";
            bool isError = false;
            if (mode == PanelMode.Sketching && _cancelSketch)
            {
                Engraver.Abandon(item.Part, sketch);
                status = "Cancelled; the part is as it was.";
            }
            else
            {
                Transaction tx = _app.TransactionManager.StartTransaction(UndoDocument(item), mode == PanelMode.Sketching ? "Engrave" : "Edit Engraving");
                try
                {
                    if (mode == PanelMode.Sketching)
                    {
                        engraved = Engraver.Complete(item.Part, sketch, _settings, warnings);
                        status = engraved ? "Engraved “" + Engraver.TextOf(Engraver.Find(item.Part)) + "” in " + item.Name + "."
                                          : "The sketch has no text left: nothing engraved.";
                    }
                    else
                    {
                        Engraver.AfterEdit(item.Part, warnings);
                        status = Engraver.Find(item.Part) != null ? "Engraving of " + item.Name + " updated." : "The sketch had no text left: the engraving is deleted.";
                    }
                    tx.End();
                    _changes++;
                }
                catch (Exception ex)
                {
                    try { tx.Abort(); } catch { }
                    if (mode == PanelMode.Sketching) Engraver.Abandon(item.Part, sketch);
                    Log.Write("Finishing the engraving of " + item.Part.FullDocumentName + " failed: " + ex);
                    status = "Engraving " + item.Name + " failed: " + ex.Message;
                    isError = true;
                }
            }
            Report(item, warnings);

            _panel.SetMode(PanelMode.Picking, _walk.Count > 1);
            RefreshExisting();
            if (engraved)
            {
                item.Done = true;
                item.Skipped = false;
                _engraved++;
                _panel.SetStatus(status, false);
                if (_walk.Count > 1) Advance();
                else { RefreshParts(); StopPicking(); _panel.SetStatus(status + " Click Done to finish.", false); }
                return;
            }
            RefreshParts();
            if (warnings.Count > 0 && !isError) { status += " " + string.Join(" ", warnings); isError = true; }
            _panel.SetStatus(status, isError);
            if (_current >= 0) { Highlight(_items[_current]); StartPicking(); }
        }

        /// <summary>The document whose undo list gets the step: the part itself in drawing mode, else the open document.</summary>
        private _Document UndoDocument(Item item) => _drawing != null ? (_Document)item.Part : (_Document)_top;

        private void Report(Item item, List<string> warnings)
        {
            foreach (string w in warnings) _warnings.Add(item.Name + ": " + w);
        }

        // ----- buttons ------------------------------------------------------------------------------------------

        private void OnPrimary()
        {
            if (_mode == PanelMode.Picking) { Finish(true, false); return; }
            ExitSketch();
            if (!InSketch()) SketchClosed();
        }

        /// <summary>While picking: close and undo everything done in this run. While sketching: drop this text only.</summary>
        private void OnSecondary()
        {
            if (_mode == PanelMode.Picking)
            {
                if (_changes > 0 && MessageBox.Show(_owner,
                        "Undo everything Engrave did since it started (" + _changes + (_changes == 1 ? " change" : " changes") + ") and close?",
                        "Engrave", MessageBoxButtons.OKCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
                Finish(false, true);
                return;
            }
            if (_mode != PanelMode.Sketching) return;
            _cancelSketch = true;
            ExitSketch();
            if (!InSketch()) SketchClosed();
        }

        private void OnSkip()
        {
            if (_mode != PanelMode.Picking || _current < 0) return;
            _items[_current].Skipped = true;
            Advance();
        }

        // ----- existing engravings ------------------------------------------------------------------------------

        private void OnEdit(int index)
        {
            if (_mode != PanelMode.Picking) return;
            Item item = _items[index];
            PlanarSketch sketch = Engraver.SketchOf(Engraver.Find(item.Part));
            if (sketch == null) { _panel.SetStatus("The text sketch of this engraving cannot be found.", true); return; }
            StopPicking();
            _current = index;
            try { ShowPart(item); }
            catch (Exception ex) { _panel.SetStatus("Could not open " + item.Name + ": " + ex.Message, true); return; }
            if (item.Side == null) item.Side = TopSide.Find(item.Part, _settings.TopIsInsideOfBends);
            Highlight(item);
            RefreshParts();
            try
            {
                if (item.Occ != null) { item.Occ.Edit(); _editedOcc = item.Occ; }
                sketch.Edit();
            }
            catch (Exception ex)
            {
                _panel.SetStatus("Could not open the sketch: " + ex.Message, true);
                StartPicking();
                return;
            }
            EnterSketchMode(PanelMode.EditingExisting, sketch, item,
                "Move the text, or double-click it to change it, then click Finish Sketch. An empty sketch deletes the engraving.");
        }

        private void OnDelete(int index)
        {
            if (_mode != PanelMode.Picking) return;
            Item item = _items[index];
            if (MessageBox.Show(_owner, "Delete the engraving of " + item.Name + "?", "Engrave",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
            DeleteEngravings(new List<Item> { item }, "Delete Engraving");
        }

        private void OnDeleteAll()
        {
            if (_mode != PanelMode.Picking) return;
            var engraved = new List<Item>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // a part on several sheets once
            foreach (Item item in _items)
                if (seen.Add(((Document)item.Part).FullFileName) && Engraver.Find(item.Part) != null) engraved.Add(item);
            if (engraved.Count == 0) return;
            string what = _asm == null && _drawing == null ? "the engraving of this part"
                        : "the engravings of all " + engraved.Count + " parts in this " + (_drawing != null ? "drawing" : "assembly");
            if (MessageBox.Show(_owner, "Delete " + what + "?", "Engrave",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
            DeleteEngravings(engraved, "Delete All Engravings");
        }

        private void DeleteEngravings(List<Item> items, string undoName)
        {
            bool wasPicking = _events != null;
            StopPicking();
            var warnings = new List<string>();
            int deleted = 0;
            Transaction tx = _app.TransactionManager.StartTransaction(items.Count == 1 ? UndoDocument(items[0]) : (_Document)_top, undoName);
            try
            {
                foreach (Item item in items)
                {
                    try
                    {
                        Engraver.Delete(item.Part, warnings);
                        item.Done = false;
                        deleted++;
                    }
                    catch (Exception ex)
                    {
                        Log.Write("Deleting the engraving of " + item.Part.FullDocumentName + " failed: " + ex);
                        warnings.Add(item.Name + ": " + ex.Message);
                    }
                }
                tx.End();
                if (deleted > 0) _changes++;
            }
            catch (Exception ex)
            {
                try { tx.Abort(); } catch { }
                warnings.Add(ex.Message);
            }
            try { _asm?.Update(); } catch { }
            RefreshExisting();
            RefreshParts();
            _panel.SetStatus(deleted + (deleted == 1 ? " engraving" : " engravings") + " deleted." +
                (warnings.Count > 0 ? " " + string.Join(" ", warnings) : ""), warnings.Count > 0);
            if (_current >= 0)
            {
                Item cur = _items[_current];
                cur.Side = null;   // the faces changed; read the top side again
                cur.Side = TopSide.Find(cur.Part, _settings.TopIsInsideOfBends);
                Highlight(cur);
                if (wasPicking || deleted > 0) StartPicking();
            }
        }

        // ----- end ----------------------------------------------------------------------------------------------

        private void OnCloseDocument(_Document doc, string fullName, EventTimingEnum timing, NameValueMap context, out HandlingCodeEnum handling)
        {
            handling = HandlingCodeEnum.kEventNotHandled;
            if (timing != EventTimingEnum.kBefore) return;
            bool ours = ReferenceEquals(doc, _top) || (_topName.Length > 0 && string.Equals(fullName, _topName, StringComparison.OrdinalIgnoreCase));
            if (ours) Finish(false, false);
        }

        /// <param name="report">Show how many parts were engraved (Done).</param>
        /// <param name="undo">Go back to the state before the command started (Cancel).</param>
        private void Finish(bool report, bool undo)
        {
            if (_finished) return;
            _wait.Stop();
            if (_mode == PanelMode.Sketching && _sketch != null)
            {
                ExitSketch();
                try { Engraver.Abandon(_sketchItem.Part, _sketch); } catch { }
            }
            else if (_mode == PanelMode.EditingExisting) ExitSketch();
            _finished = true;
            _sketch = null;

            StopPicking();
            try { _appEvents.OnCloseDocument -= OnCloseDocument; } catch { }
            try { _hiPart?.Delete(); } catch { }
            try { _hiFaces?.Delete(); } catch { }
            ReturnToTop();
            if (undo && _changes > 0) UndoRun();
            try { _asm?.Update(); } catch { }
            try { _app.StatusBarText = ""; } catch { }

            try { if (_panel != null && _panel.ReadSettings(_settings) == null) _settings.Save(); } catch { }
            try { DockHost.Hide(_panel); } catch { }
            try { _app.ActiveView.Update(); } catch { }
            _wait.Dispose();
            _active = null;

            if (undo || !report || _engraved == 0 && _warnings.Count == 0) return;
            string message = _engraved + (_engraved == 1 ? " part" : " parts") + " engraved. Remember to save.";
            if (_warnings.Count > 0) message += "\n\nThe engravings are there, but some steps reported a problem:\n" + string.Join("\n", _warnings);
            MessageBox.Show(_owner, message, "Engrave", MessageBoxButtons.OK, _warnings.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        }

        /// <summary>Undoes every transaction since the command started, through the check point set in <see cref="Run"/>.</summary>
        private void UndoRun()
        {
            try
            {
                if (_start == null) throw new InvalidOperationException("there is no undo check point");
                _app.TransactionManager.GoToCheckPoint(_start);
            }
            catch (Exception ex)
            {
                Log.Write("Undoing the run failed: " + ex);
                MessageBox.Show(_owner, "The changes could not be undone automatically (" + ex.Message + ").\n\nUse Undo, or close the files without saving.",
                    "Engrave", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        /// <summary>Leaves in-place editing of a part, so the assembly is active again at its top level.</summary>
        private void ReturnToTop()
        {
            if (_drawing != null)
            {
                foreach (Document doc in _opened)
                {
                    var views = new List<Inventor.View>();
                    try { foreach (Inventor.View v in doc.Views) views.Add(v); } catch { }
                    foreach (Inventor.View v in views) { try { v.Close(); } catch (Exception ex) { Log.Write("Closing a part window failed: " + ex.Message); } }
                }
                _opened.Clear();
                try { _drawing.Activate(); } catch { }
                return;
            }
            if (_asm == null || _editedOcc == null) return;
            try { _editedOcc.ExitEdit(ExitTypeEnum.kExitToTop); }
            catch (Exception ex)
            {
                Log.Write("ExitEdit failed: " + ex.Message);
                try { _app.CommandManager.ControlDefinitions["AssemblyReturnToTopCmd"].Execute2(true); }
                catch (Exception ex2) { Log.Write("Return to top failed: " + ex2.Message); }
            }
        }
    }
}
