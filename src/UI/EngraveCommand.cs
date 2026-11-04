using System;
using System.Collections.Generic;
using System.Linq;
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
            public readonly Dictionary<string, string> Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // iProperty values typed for this part
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
        private readonly List<int> _walk = new List<int>();
        private readonly HashSet<int> _chosen = new HashSet<int>();   // items to engrave; empty: all in the walk   // the items the panel's part list shows (the visible ones)
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
        private string _lastCommand = "";         // the last Inventor command started, to recognise Return

        /// <summary>How a part was before this run, so Cancel can put it back; Done removes what was set aside.</summary>
        private sealed class PartState
        {
            public PartDocument Part;
            public bool HadEngraving;
            public bool SetAside;                        // its engraving was replaced or deleted in this run
            public List<Engraver.TextState> Before;      // the text boxes before the first edit
        }

        private readonly Dictionary<string, PartState> _touched = new Dictionary<string, PartState>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Inventor's Return commands; they end the picking, which Engrave then simply starts again.</summary>
        private static readonly HashSet<string> ReturnCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "AppReturnParentCmd", "AppReturnPreviousCmd", "AppReturnTopCmd", "AppReturnBaseEnv", "AssemblyFinishEditCmd"
        };

        private UserInputEvents _input;
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
            if (_active != null)
            {
                DockHost.Reshow();   // its panel may have been closed with the cross on its tab
                return null;
            }
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
                if (IsLibraryPart(part)) return "This is a read-only library (Content Center) part and cannot be engraved.";
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
                        if (IsLibraryPart(p)) continue;
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
                    if (IsLibraryPart(p)) continue;
                    _items.Add(new Item { Part = p, Name = EngraveText.PartName(p), Sheet = number, SheetMetal = p.ComponentDefinition is SheetMetalComponentDefinition });
                }
                catch { /* view without a readable part */ }
            }
            if (_items.Count == 0) return "No sheet in this drawing shows a part that can be engraved.";
            for (int i = 0; i < _items.Count; i++) _walk.Add(i);
            return null;
        }

        /// <summary>
        /// Standard Content Center parts (bolts, nuts...) are read-only library files and are left out. Every other part
        /// is listed, also a read-only one (for example checked in to Vault): engraving it then reports why it fails.
        /// </summary>
        private static bool IsLibraryPart(PartDocument part)
        {
            bool content = false, modifiable = true;
            try { content = part.ComponentDefinition.IsContentMember; } catch { }
            try { modifiable = ((Document)part).IsModifiable; } catch { }
            return content && !modifiable;
        }

        private static bool ReadOnly(PartDocument part)
        {
            try { return !((Document)part).IsModifiable; } catch { return false; }
        }

        private TopSide FindSide(Item item) => TopSide.Find(item.Part, _settings.TopIsInsideOfBends, _settings.TopSideOnly);

        private void Run()
        {
            _panel = new EngravePanel(Theme.Detect(_app), _asm != null || _drawing != null);
            _panel.ShowSheetOption(_drawing != null);
            _panel.LoadSettings(_settings);
            _panel.ReadValue = name => ValueFor(_current >= 0 ? _items[_current] : null, name);
            _panel.SetProperties(_settings.Properties);

            _panel.OrderChanged += OnOrderChanged;
            _panel.CustomProperties = CustomProperties;
            _panel.TextEdited += OnTextEdited;
            _panel.PartChosen += k => { _chosen.Clear(); Choose(_walk[k]); };
            _panel.PartsChosen += OnPartsChosen;
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
            _input = _app.CommandManager.UserInputEvents;
            _input.OnActivateCommand += OnActivateCommand;

            _appEvents = _app.ApplicationEvents;
            _appEvents.OnCloseDocument += OnCloseDocument;
            DockHost.Closed += OnPanelClosed;

            DockHost.Show(_panel, "Engrave");
            ShowOrderHint();
            RefreshExisting();
            ChooseFromSelection();
            if (_walk.Count == 0)
            {
                _panel.SetStatus("All parts are hidden; their engravings are listed below.", false);
                return;
            }
            Choose(_chosen.Count > 0 ? _walk.First(_chosen.Contains) : _walk[0]);
            if (_chosen.Count > 0)
                _panel.SetStatus(_chosen.Count + (_chosen.Count == 1 ? " part" : " parts") + " selected in Inventor: only " + (_chosen.Count == 1 ? "that one is" : "those are") + " engraved. Click a single part in the list to go back to all.", false);
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
            SetTransparent(item, false);   // a finished part chosen again is solid while it is worked on
            try { ShowPart(item); }
            catch (Exception ex)
            {
                _panel.SetStatus("Could not open " + item.Name + ": " + ex.Message, true);
                RefreshParts();
                return;
            }
            if (item.Side == null) item.Side = FindSide(item);
            _panel.RefreshPropertyValues();   // the iProperties of this part
            ShowOrderHint();
            if (!item.TextEdited) item.Text = ComposeText(item);
            _panel.SetText(item.Text);
            string info = item.Side.Info;
            if (ReadOnly(item.Part)) info += " The part file is read-only (checked in?): check it out to engrave it.";
            if (Engraver.Find(item.Part) != null) info += " This part is engraved already; a new engraving replaces it.";
            _panel.SetInfo(info);
            _panel.SetStatus("", false);
            Highlight(item);
            RefreshParts();
            StartPicking();
        }

        /// <summary>[iProperty texts ]part name, and in a drawing " - sheet number" when that option is on.</summary>
        private string ComposeText(Item item)
        {
            var parts = new List<string>();
            foreach (PropertySpec spec in _panel.PropertySpecs)
            {
                string value = ValueFor(item, spec.Name).Trim();
                if (value.Length > 0) parts.Add(spec.Before + value + spec.After);
            }
            string text = EngraveText.Compose(string.Join(" ", parts), item.Part, _settings.MaxPartChars);
            return item.Sheet > 0 && _settings.AddSheetNumber ? text + " - " + item.Sheet : text;
        }

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

        /// <summary>
        /// Next part that is neither engraved nor skipped in this run, after the current one; only among the chosen
        /// parts when some are chosen.
        /// </summary>
        private void Advance()
        {
            int start = Math.Max(0, _walk.IndexOf(_current));
            for (int n = 1; n <= _walk.Count; n++)
            {
                int index = _walk[(start + n) % _walk.Count];
                if (_chosen.Count > 0 && !_chosen.Contains(index)) continue;
                Item item = _items[index];
                if (!item.Done && !item.Skipped)
                {
                    Choose(index);
                    return;
                }
            }
            StopPicking();
            RefreshParts();
            string which = _chosen.Count > 0 ? " All chosen parts are done." : "";
            _panel.SetStatus(_engraved + (_engraved == 1 ? " part" : " parts") + " engraved." + which + " Click Done to finish, or pick a part to do it again.", false);
        }

        /// <summary>Several parts selected in the list: only those are engraved, starting with the first of them.</summary>
        private void OnPartsChosen(IList<int> rows)
        {
            if (_mode != PanelMode.Picking) return;
            _chosen.Clear();
            foreach (int k in rows) if (k >= 0 && k < _walk.Count) _chosen.Add(_walk[k]);
            if (_chosen.Count == 0) return;
            if (!_chosen.Contains(_current)) Choose(_walk.First(i => _chosen.Contains(i)));
            else RefreshParts();
            _panel.SetStatus(_chosen.Count + " parts chosen: Skip and the next part only go through those.", false);
        }

        /// <summary>
        /// Parts selected in Inventor before Engrave started (components or their faces in the browser or the window, or
        /// drawing views) become the chosen parts. A selected subassembly chooses all parts in it.
        /// </summary>
        private void ChooseFromSelection()
        {
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sheets = new HashSet<int>();
            try
            {
                foreach (object o in _top.SelectSet)
                {
                    try
                    {
                        ComponentOccurrence occ = o as ComponentOccurrence ?? (o as FaceProxy)?.ContainingOccurrence ?? (o as EdgeProxy)?.ContainingOccurrence;
                        if (occ != null) AddPartFiles(occ, files);
                        else if (o is DrawingView view && _drawing != null) sheets.Add(SheetNumber(view.Parent as Sheet));
                    }
                    catch { /* something that is not a part */ }
                }
                if (files.Count > 0 || sheets.Count > 0) _top.SelectSet.Clear();   // it would get in the way of picking faces
            }
            catch (Exception ex) { Log.Write("Reading the selection failed: " + ex.Message); }

            foreach (int i in _walk)
            {
                Item item = _items[i];
                if (item.Sheet > 0 ? sheets.Contains(item.Sheet) : files.Contains(((Document)item.Part).FullFileName)) _chosen.Add(i);
            }
        }

        private static void AddPartFiles(ComponentOccurrence occ, HashSet<string> files)
        {
            if (occ.Definition.Document is PartDocument part) { files.Add(((Document)part).FullFileName); return; }
            foreach (ComponentOccurrence sub in occ.SubOccurrences) { try { AddPartFiles(sub, files); } catch { } }
        }

        private int SheetNumber(Sheet sheet)
        {
            if (sheet == null) return 0;
            int n = 0;
            foreach (Sheet s in _drawing.Sheets)
            {
                n++;
                if (s.Name == sheet.Name) return n;
            }
            return 0;
        }

        private void RefreshParts()
        {
            var rows = new List<PartRow>();
            foreach (int i in _walk)
            {
                Item item = _items[i];
                bool engraved = Engraver.Find(item.Part) != null;
                string status = item.Done ? "Engraved" : item.Skipped ? "Skipped" : engraved ? "Engraved before" : ReadOnly(item.Part) ? "Read-only" : "";
                rows.Add(new PartRow { Name = item.Label, Status = status, Done = item.Done && engraved, Engraved = engraved, Current = i == _current, Chosen = _chosen.Contains(i) });
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

        /// <summary>The iProperties in front of the name may be used: none holds the placeholder for "not filled in".</summary>
        private bool OrderValid() => Placeholders().Count == 0;

        /// <summary>The names of the iProperties whose value is the placeholder; their fields are marked.</summary>
        private List<string> Placeholders()
        {
            var bad = new List<string>();
            IList<string> names = _panel.PropertyNames, values = _panel.PropertyValues;
            for (int i = 0; i < names.Count; i++)
            {
                bool placeholder = EngraveText.IsPlaceholder(values[i], _settings.PlaceholderValue);
                _panel.MarkProperty(i, placeholder);
                if (placeholder) bad.Add("\u201C" + names[i] + "\u201D");
            }
            return bad;
        }

        private void ShowOrderHint()
        {
            List<string> bad = Placeholders();
            var empty = new List<string>();
            IList<string> names = _panel.PropertyNames, values = _panel.PropertyValues;
            for (int i = 0; i < names.Count; i++) if (values[i].Length == 0) empty.Add("\u201C" + names[i] + "\u201D");

            if (bad.Count > 0)
                _panel.SetOrderHint(_settings.PlaceholderValue + " means " + string.Join(", ", bad) + " is not filled in yet. Type the right value.", true);
            else if (names.Count == 0)
                _panel.SetOrderHint("Add an iProperty (for example an order number) to put its value in front of the part number.", false);
            else if (empty.Count > 0)
                _panel.SetOrderHint(string.Join(", ", empty) + " is empty here and is left out.", false);
            else _panel.SetOrderHint("", false);
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

        /// <summary>
        /// The value of an iProperty for a part: what was typed for it in this run, else the part's own iProperty, else
        /// the iProperty of the open assembly or drawing (for example an order number kept only there).
        /// </summary>
        private string ValueFor(Item item, string name)
        {
            if (item != null && item.Values.TryGetValue(name, out string typed)) return typed;
            return OwnValue(item, name);
        }

        private string OwnValue(Item item, string name)
        {
            string value = item != null ? EngraveText.ReadProperty((Document)item.Part, name) : "";
            return value.Length > 0 ? value : EngraveText.ReadProperty(_top, name);
        }

        /// <summary>Values typed in the panel belong to the part being engraved now; others keep their own.</summary>
        private void KeepTypedValues()
        {
            if (_current < 0) return;
            Item item = _items[_current];
            IList<string> names = _panel.PropertyNames, values = _panel.PropertyValues;
            for (int i = 0; i < names.Count; i++)
            {
                if (values[i] == OwnValue(item, names[i]).Trim()) item.Values.Remove(names[i]);
                else item.Values[names[i]] = values[i];
            }
        }

        private void OnOrderChanged()
        {
            KeepTypedValues();
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
            bool topInside = _settings.TopIsInsideOfBends, topOnly = _settings.TopSideOnly;
            if (_panel.ReadSettings(_settings) != null) return;
            OnOrderChanged();   // the placeholder or the name length may have changed
            if (topInside != _settings.TopIsInsideOfBends || topOnly != _settings.TopSideOnly)
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
                _panel.SetStatus("Fill in the iProperty value, or remove that iProperty from the text.", true);
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

        private void OnActivateCommand(string commandName, NameValueMap context) => _lastCommand = commandName ?? "";

        /// <summary>
        /// Another command or Esc ended the picking; the command itself stays open. After one of Inventor's Return
        /// commands picking simply starts again, so Return does not get in the way.
        /// </summary>
        private void OnTerminate()
        {
            StopPicking();
            if (_finished) return;
            if (ReturnCommands.Contains(_lastCommand))
            {
                _lastCommand = "";
                _panel.BeginInvoke(new Action(() =>
                {
                    LeaveEdit();
                    if (_current >= 0) Highlight(_items[_current]);
                    StartPicking();
                }));
                return;
            }
            _panel.SetStatus("Picking stopped. Click the Face field, or a part in the list, to go on.", false);
        }

        /// <summary>The part this run is about to change; remembers how it was before, the first time.</summary>
        private PartState Touch(PartDocument part)
        {
            string key = ((Document)part).FullFileName;
            if (!_touched.TryGetValue(key, out PartState st))
            {
                st = new PartState { Part = part, HadEngraving = Engraver.Find(part) != null };
                _touched[key] = st;
            }
            return st;
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
                Touch(item.Part);   // remember how the part was, for Cancel
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

            // back to the assembly at once, so the next part is highlighted and can be picked straight away
            LeaveEdit();
            _panel.SetMode(PanelMode.Picking, _walk.Count > 1);
            RefreshExisting();
            if (engraved)
            {
                item.Done = true;
                item.Skipped = false;
                _engraved++;
                Touch(item.Part).SetAside = true;   // its earlier engraving, if any, is now set aside
                SetTransparent(item, true);   // finished parts fade out, so what is left stands out
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
                        "Put back everything Engrave changed since it started (" + _changes + (_changes == 1 ? " change" : " changes") + ") and close?",
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
            PartFeature existing = Engraver.Find(item.Part);
            PlanarSketch sketch = Engraver.SketchOf(existing);
            if (sketch == null) { _panel.SetStatus("The text sketch of this engraving cannot be found.", true); return; }
            PartState state = Touch(item.Part);
            if (state.Before == null && !state.SetAside) state.Before = Engraver.Snapshot(existing);   // for Cancel
            StopPicking();
            _current = index;
            try { ShowPart(item); }
            catch (Exception ex) { _panel.SetStatus("Could not open " + item.Name + ": " + ex.Message, true); return; }
            if (item.Side == null) item.Side = FindSide(item);
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
                        Touch(item.Part).SetAside = true;
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
                cur.Side = FindSide(cur);
                Highlight(cur);
                if (wasPicking || deleted > 0) StartPicking();
            }
        }

        // ----- end ----------------------------------------------------------------------------------------------

        /// <summary>
        /// The cross on the panel tab: ask what to do, a moment later (Inventor is still busy hiding the window). Keep
        /// is Done, Put back is Cancel, and anything else brings the panel back.
        /// </summary>
        private void OnPanelClosed()
        {
            if (_finished) return;
            var later = new Timer { Interval = 150 };
            later.Tick += (s, e) =>
            {
                later.Stop();
                later.Dispose();
                if (_finished || DockHost.Visible) return;
                if (_changes == 0 && _mode == PanelMode.Picking) { Finish(true, false); return; }
                DialogResult answer = MessageBox.Show(_owner,
                    "Close Engrave?\n\nYes: keep the changes (like Done).\nNo: put everything back (like Cancel).\nCancel: go on engraving.",
                    "Engrave", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button3);
                if (answer == DialogResult.Yes) Finish(true, false);
                else if (answer == DialogResult.No) Finish(false, true);
                else DockHost.Reshow();
            };
            later.Start();
        }

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
            try { _input.OnActivateCommand -= OnActivateCommand; } catch { }
            DockHost.Closed -= OnPanelClosed;
            try { _hiPart?.Delete(); } catch { }
            try { _hiFaces?.Delete(); } catch { }
            ReturnToTop();
            RestoreTransparency();
            if (undo) RevertRun(); else CommitRun();
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

        /// <summary>Done: the engravings that were replaced or deleted in this run are removed for good.</summary>
        private void CommitRun()
        {
            foreach (PartState st in _touched.Values)
            {
                try { Engraver.Commit(st.Part); }
                catch (Exception ex) { Log.Write("Tidying " + st.Part.FullDocumentName + " failed: " + ex); }
            }
        }

        /// <summary>Cancel: every part this run changed is put back as it was (one undo step).</summary>
        private void RevertRun()
        {
            if (_touched.Count == 0) return;
            var warnings = new List<string>();
            Transaction tx = null;
            try { tx = _app.TransactionManager.StartTransaction((_Document)_top, "Cancel Engrave"); } catch { }
            foreach (PartState st in _touched.Values)
            {
                try { Engraver.Revert(st.Part, st.HadEngraving, st.Before, warnings); }
                catch (Exception ex)
                {
                    Log.Write("Putting " + st.Part.FullDocumentName + " back failed: " + ex);
                    warnings.Add(EngraveText.PartName(st.Part) + ": " + ex.Message);
                }
            }
            try { tx?.End(); } catch { }
            if (warnings.Count > 0)
                MessageBox.Show(_owner, "Not everything could be put back:\n" + string.Join("\n", warnings) + "\n\nCheck these parts before saving.",
                    "Engrave", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
            LeaveEdit();
        }

        /// <summary>Assembly: leaves in-place editing of the last edited part, back to the top of the assembly.</summary>
        private void LeaveEdit()
        {
            if (_asm == null || _editedOcc == null) return;
            ComponentOccurrence occ = _editedOcc;
            _editedOcc = null;
            if (!InPartEdit()) return;   // already back at the top (for example after Return)
            try { occ.ExitEdit(ExitTypeEnum.kExitToTop); }
            catch (Exception ex)
            {
                Log.Write("ExitEdit failed: " + ex.Message);
                try { _app.CommandManager.ControlDefinitions["AppReturnTopCmd"].Execute2(true); }
                catch (Exception ex2) { Log.Write("Return to top failed: " + ex2.Message); }
            }
        }

        /// <summary>True while a part (or a sketch in it) is being edited in place in the assembly.</summary>
        private bool InPartEdit()
        {
            try
            {
                object edit = _app.ActiveEditObject;
                return edit is PlanarSketch || edit is PartComponentDefinition || edit is PartDocument || edit is ComponentOccurrence;
            }
            catch { return false; }
        }

        // ----- transparency of finished parts -------------------------------------------------------------------

        private readonly List<ComponentOccurrence> _madeTransparent = new List<ComponentOccurrence>();

        /// <summary>
        /// Makes every occurrence of the part see-through (or solid again). Only occurrences this command made
        /// see-through are turned back, and all of them are restored when the command ends.
        /// </summary>
        private void SetTransparent(Item item, bool on)
        {
            if (_asm == null) return;
            try
            {
                if (on)
                {
                    foreach (ComponentOccurrence occ in _asm.ComponentDefinition.Occurrences.get_AllReferencedOccurrences(item.Part.ComponentDefinition))
                    {
                        try
                        {
                            if (occ.Transparent) continue;
                            occ.Transparent = true;
                            _madeTransparent.Add(occ);
                        }
                        catch { /* occurrence cannot be changed */ }
                    }
                }
                else
                {
                    foreach (ComponentOccurrence occ in _madeTransparent.ToArray())
                    {
                        bool same;
                        try { same = SameFile(occ.Definition.Document as Document, item.Part); } catch { same = false; }
                        if (!same) continue;
                        try { occ.Transparent = false; } catch { }
                        _madeTransparent.Remove(occ);
                    }
                }
                _app.ActiveView.Update();
            }
            catch (Exception ex) { Log.Write("Changing transparency failed: " + ex.Message); }
        }

        private void RestoreTransparency()
        {
            foreach (ComponentOccurrence occ in _madeTransparent) { try { occ.Transparent = false; } catch { } }
            _madeTransparent.Clear();
        }
    }
}
