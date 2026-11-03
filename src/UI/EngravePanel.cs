using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Engrave.Core;

namespace Engrave.UI
{
    internal enum PanelMode { Picking, Sketching, EditingExisting }

    /// <summary>One line of the part list: a sheet metal part and how far it is.</summary>
    internal sealed class PartRow
    {
        public string Name, Status;
        public bool Done;       // engraved in this run
        public bool Engraved;   // has an engraving (from this run or before)
    }

    /// <summary>One line of the "Existing Engravings" list.</summary>
    internal sealed class EngravingRow
    {
        public int Part;   // index into the part list
        public string Name, Text;
        public bool Suppressed;
    }

    /// <summary>
    /// The Engrave panel, built like Inventor's own property panels (and the Tab and Slot panel): section bars with
    /// chevrons, flat fields, a face selector, the list of existing engravings and the buttons at the bottom.
    /// It only shows and collects values; picking, sketching and cutting are driven by <see cref="EngraveCommand"/>.
    /// </summary>
    internal sealed class EngravePanel : UserControl
    {
        private const int LabelWidth = 90;
        private const int PartRowHeight = 22;
        private const int MaxVisibleParts = 8;

        private readonly Theme _theme;
        private readonly bool _assembly;
        private readonly Dictionary<Control, Label> _rowLabels = new Dictionary<Control, Label>();
        private bool _loading;
        private PanelMode _mode = PanelMode.Picking;

        // iProperty in front of the part name (for example an order number)
        private readonly TableLayoutPanel _propList = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = Padding.Empty };
        private readonly List<PropertyRow> _propRows = new List<PropertyRow>();
        private InventorButton _propAdd;

        /// <summary>
        /// One iProperty in front of the name: its name, text before and after the value (for example "X" after a
        /// quantity, giving "5X"), the value itself (editable) and a remove button.
        /// </summary>
        private sealed class PropertyRow
        {
            public string Name;
            public Label Label;
            public FieldBox Before, Value, After;
            public IconButton Remove;
        }

        // column titles above the iProperty rows
        private Label _colBefore, _colValue, _colAfter;

        /// <summary>Reads the value of an iProperty from the open document; set by the command.</summary>
        public Func<string, string> ReadValue;
        private Control _textHeader, _textBody;

        /// <summary>Custom iProperties of the open document, with their values, for the pick menu; set by the command.</summary>
        public Func<IList<KeyValuePair<string, string>>> CustomProperties;
        private readonly Label _propHint = new Label { AutoSize = true, Tag = "keep" };

        // engraving
        private readonly ListView _parts = NewList();
        private readonly FieldBox _text;
        private readonly SelectorField _pick;
        private readonly Label _info = new Label { AutoSize = false, Height = 34, Tag = "keep", Dock = DockStyle.Fill };

        // existing engravings
        private readonly ListView _existing = NewList();
        private IconButton _edit, _delete;
        private InventorButton _deleteAll;
        private readonly Label _existingInfo = new Label { AutoSize = true, Tag = "keep" };

        // settings
        private readonly FieldBox _depth, _height, _maxChars, _placeholder;
        private readonly CheckBox _topInside = new CheckBox { Text = "Top is the inside of the bends", AutoSize = true };

        private readonly Label _status = new Label { AutoSize = false, Dock = DockStyle.Bottom, Height = 48, Padding = new Padding(8, 2, 8, 0), Tag = "keep", Visible = false };
        private readonly InventorButton _primary, _secondary, _skip;

        public event Action OrderChanged;
        public event Action TextEdited;
        public event Action<int> PartChosen;
        public event Action PickClicked;
        public event Action PrimaryClicked;
        public event Action SecondaryClicked;
        public event Action TertiaryClicked;
        public event Action<int> EditRequested;
        public event Action<int> DeleteRequested;
        public event Action DeleteAllRequested;
        public event Action SettingsChanged;

        public EngravePanel(Theme theme, bool assembly)
        {
            _theme = theme;
            _assembly = assembly;
            Font = new Font("Segoe UI", 9f);
            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = theme.Back;
            ForeColor = theme.Text;
            Dock = DockStyle.Fill;

            _placeholder = new FieldBox(theme);
            _text = new FieldBox(theme);
            _pick = new SelectorField(theme);
            _depth = new FieldBox(theme);
            _height = new FieldBox(theme);
            _maxChars = new FieldBox(theme); _maxChars.AsInteger(1, 99);
            _primary = new InventorButton(theme, "Done", true);
            _secondary = new InventorButton(theme, "Cancel", false);
            _skip = new InventorButton(theme, "Skip", false);
            _deleteAll = new InventorButton(theme, "Delete All", false) { Width = 84 };

            BuildLayout();
            theme.Apply(this);
            _parts.BackColor = _existing.BackColor = theme.Field;
            _parts.ForeColor = _existing.ForeColor = theme.Text;
            _info.ForeColor = _propHint.ForeColor = _existingInfo.ForeColor = theme.Dim;
            WireEvents();
            _pick.SetValue("", false);
            SetMode(PanelMode.Picking, assembly);
        }

        private static ListView NewList() => new ListView
        {
            View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, Scrollable = true,
            HeaderStyle = ColumnHeaderStyle.None, OwnerDraw = true, BorderStyle = BorderStyle.None
        };

        // ----- iProperty in front of the name -------------------------------------------------------------------


        /// <summary>Shows the iProperties to put in front of the name, with their values read from the open document.</summary>
        public void SetProperties(IEnumerable<PropertySpec> specs)
        {
            _loading = true;
            foreach (PropertyRow row in _propRows) DisposeRow(row);
            _propRows.Clear();
            foreach (PropertySpec p in specs ?? new PropertySpec[0]) AddPropertyRow(p.Name, false, p.Before, p.After);
            _loading = false;
        }

        public IList<string> PropertyNames => _propRows.ConvertAll(r => r.Name);
        public IList<string> PropertyValues => _propRows.ConvertAll(r => r.Value.Text.Trim());

        /// <summary>What each iProperty adds to the text: before + value + after, or "" when its value is empty.</summary>
        public IList<string> PropertyTexts => _propRows.ConvertAll(r =>
            r.Value.Text.Trim().Length == 0 ? "" : r.Before.Text + r.Value.Text.Trim() + r.After.Text);

        public List<PropertySpec> PropertySpecs => _propRows.ConvertAll(r => new PropertySpec { Name = r.Name, Before = r.Before.Text, After = r.After.Text });

        /// <summary>Marks the value of one iProperty as unusable (for example the placeholder for "not filled in").</summary>
        public void MarkProperty(int index, bool invalid)
        {
            if (index >= 0 && index < _propRows.Count) _propRows[index].Value.Invalid = invalid;
        }

        private void AddPropertyRow(string name, bool notify, string before = "", string after = "")
        {
            name = (name ?? "").Trim();
            if (name.Length == 0 || _propRows.Exists(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase))) return;
            var row = new PropertyRow
            {
                Name = name,
                Label = new Label { Text = name, AutoSize = false, AutoEllipsis = true, Width = LabelWidth - 4, Height = 22, TextAlign = ContentAlignment.MiddleLeft, ForeColor = _theme.Text, BackColor = Color.Transparent, Margin = new Padding(0, 3, 4, 3), Tag = "keep" },
                Before = Affix(name, "before"),
                Value = new FieldBox(_theme) { Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, 3, 0, 3) },
                After = Affix(name, "after"),
                Remove = new IconButton(_theme, "Remove " + name + " from the text", ActionIcons.Delete) { Size = new Size(26, 22), Margin = new Padding(4, 3, 0, 3) }
            };
            bool wasLoading = _loading;
            _loading = true;
            try { row.Value.Text = ReadValue?.Invoke(name) ?? ""; } catch { }
            row.Before.Text = before ?? "";
            row.After.Text = after ?? "";
            _loading = wasLoading;
            foreach (FieldBox box in new[] { row.Before, row.Value, row.After })
                box.TextChanged += (s, e) => { if (!_loading) OrderChanged?.Invoke(); };
            row.Remove.Click += (s, e) =>
            {
                _propRows.Remove(row);
                DisposeRow(row);
                LayoutPropertyRows();
                OrderChanged?.Invoke();
            };
            _propRows.Add(row);
            LayoutPropertyRows();
            if (notify) OrderChanged?.Invoke();
        }

        /// <summary>A narrow field for text before or after the value.</summary>
        private FieldBox Affix(string name, string where)
        {
            var box = new FieldBox(_theme) { Width = 36, Margin = where == "before" ? new Padding(0, 3, 4, 3) : new Padding(4, 3, 0, 3) };
            _tips.SetToolTip(box, "Text " + where + " the value of " + name + " (for example X after a quantity: 5X)");
            return box;
        }

        private readonly ToolTip _tips = new ToolTip();

        private void DisposeRow(PropertyRow row)
        {
            foreach (Control c in new Control[] { row.Label, row.Before, row.Value, row.After, row.Remove })
            {
                try { _propList.Controls.Remove(c); c.Dispose(); } catch { }
            }
        }

        private void LayoutPropertyRows()
        {
            _propList.SuspendLayout();
            _propList.Controls.Clear();
            _propList.RowStyles.Clear();
            _propList.RowCount = _propRows.Count + 1;
            _propList.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _propList.Controls.Add(_colBefore, 1, 0);
            _propList.Controls.Add(_colValue, 2, 0);
            _propList.Controls.Add(_colAfter, 3, 0);
            for (int i = 0; i < _propRows.Count; i++)
            {
                _propList.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                _propList.Controls.Add(_propRows[i].Label, 0, i + 1);
                _propList.Controls.Add(_propRows[i].Before, 1, i + 1);
                _propList.Controls.Add(_propRows[i].Value, 2, i + 1);
                _propList.Controls.Add(_propRows[i].After, 3, i + 1);
                _propList.Controls.Add(_propRows[i].Remove, 4, i + 1);
            }
            _propList.Visible = _propRows.Count > 0;
            _propList.ResumeLayout();
        }

        /// <summary>The Text section is hidden in a drawing, where the text is always "part number - sheet".</summary>
        public void ShowTextOptions(bool visible)
        {
            if (_textHeader != null) _textHeader.Visible = visible;
            if (_textBody != null) _textBody.Visible = visible;
        }

        /// <summary>Shows what goes in front of the name, or why the value cannot be used.</summary>
        public void SetOrderHint(string text, bool isError)
        {
            _propHint.Text = text ?? "";
            _propHint.ForeColor = isError ? ErrorColor : _theme.Dim;
        }

        // ----- engraving ----------------------------------------------------------------------------------------

        public void SetParts(IList<PartRow> rows, int current)
        {
            _loading = true;
            _parts.BeginUpdate();
            _parts.Items.Clear();
            for (int i = 0; i < rows.Count; i++)
            {
                var item = new ListViewItem(rows[i].Name) { Tag = rows[i] };
                _parts.Items.Add(item);
                if (i == current) item.Selected = true;
            }
            _parts.EndUpdate();
            _parts.Height = Math.Max(1, Math.Min(rows.Count, MaxVisibleParts)) * PartRowHeight + 2;
            if (current >= 0 && current < _parts.Items.Count) _parts.EnsureVisible(current);
            _loading = false;
        }

        public void SetText(string text)
        {
            _loading = true;
            _text.Text = text ?? "";
            _loading = false;
            _text.Invalid = false;
        }

        public string EngravingText => _text.Text.Trim();

        public void MarkTextInvalid() => _text.Invalid = true;

        public void SetPick(bool active, string value)
        {
            _pick.Active = active;
            _pick.SetValue(value ?? "", !string.IsNullOrEmpty(value));
        }

        public void SetInfo(string text) => _info.Text = text ?? "";

        // ----- existing engravings ------------------------------------------------------------------------------

        public void SetExisting(IList<EngravingRow> rows)
        {
            int keep = SelectedExisting();
            _existing.BeginUpdate();
            _existing.Items.Clear();
            foreach (EngravingRow r in rows)
            {
                var item = new ListViewItem(r.Name) { Tag = r, ToolTipText = r.Text };
                _existing.Items.Add(item);
                if (r.Part == keep) item.Selected = true;
            }
            _existing.EndUpdate();
            _existing.Height = Math.Max(1, Math.Min(rows.Count, 5)) * 40 + 2;
            _existingInfo.Text = rows.Count == 0 ? "No engravings yet."
                               : rows.Count + (rows.Count == 1 ? " engraved part." : " engraved parts.");
            UpdateExistingButtons();
        }

        private int SelectedExisting() => _existing.SelectedItems.Count == 1 ? (_existing.SelectedItems[0].Tag as EngravingRow)?.Part ?? -1 : -1;

        private void UpdateExistingButtons()
        {
            bool idle = _mode == PanelMode.Picking;
            _existing.Enabled = idle;
            _edit.Enabled = _delete.Enabled = idle && SelectedExisting() >= 0;
            _deleteAll.Enabled = idle && _existing.Items.Count > 0;
        }

        // ----- settings -----------------------------------------------------------------------------------------

        public void LoadSettings(EngraveSettings s)
        {
            _loading = true;
            _depth.Text = EngraveSettings.Format(s.DepthMm);
            _height.Text = s.TextHeightMm > 0 ? EngraveSettings.Format(s.TextHeightMm) : "";
            _maxChars.IntValue = s.MaxPartChars;
            _topInside.Checked = s.TopIsInsideOfBends;
            _placeholder.Text = s.PlaceholderValue ?? "";
            _loading = false;
        }

        /// <summary>Reads the settings fields into <paramref name="s"/>; returns an error message, or null when all parsed.</summary>
        public string ReadSettings(EngraveSettings s)
        {
            string error = null;
            bool depthOk = EngraveSettings.TryParse(_depth.Text, out double depth) && depth > 0;
            _depth.Invalid = !depthOk;
            if (depthOk) s.DepthMm = depth; else error = "Depth is not a valid size.";

            if (_height.Text.Trim().Length == 0) { s.TextHeightMm = 0; _height.Invalid = false; }
            else
            {
                bool ok = EngraveSettings.TryParse(_height.Text, out double h) && h >= 0;
                _height.Invalid = !ok;
                if (ok) s.TextHeightMm = h; else error = error ?? "Text height is not a valid size.";
            }
            s.MaxPartChars = _maxChars.IntValue;
            s.TopIsInsideOfBends = _topInside.Checked;
            s.Properties = PropertySpecs;
            s.PlaceholderValue = _placeholder.Text.Trim();
            return error;
        }

        // ----- mode and status ----------------------------------------------------------------------------------

        /// <summary>
        /// Picking: Done / Cancel (undo the whole run) / Skip. Sketching a new text: Finish Sketch / Cancel (this text only).
        /// Editing an engraving: Finish Sketch.
        /// </summary>
        public void SetMode(PanelMode mode, bool canSkip)
        {
            _mode = mode;
            bool picking = mode == PanelMode.Picking;
            _primary.Text = picking ? "Done" : "Finish Sketch";
            _primary.Width = picking ? 74 : 96;
            _secondary.Text = "Cancel";
            _secondary.Visible = picking || mode == PanelMode.Sketching;
            _skip.Visible = picking && canSkip;
            foreach (Control c in new Control[] { _propList, _propAdd, _placeholder, _parts, _text, _pick, _depth, _height, _maxChars, _topInside })
                c.Enabled = picking;
            UpdateExistingButtons();
            _primary.Invalidate();
            _secondary.Invalidate();
            _skip.Invalidate();
        }

        /// <summary>Shows a message above the buttons; an empty text hides the line.</summary>
        public void SetStatus(string text, bool isError)
        {
            _status.Text = text ?? "";
            _status.Visible = !string.IsNullOrEmpty(text);
            _status.ForeColor = isError ? ErrorColor : _theme.Dim;
        }

        private static readonly Color ErrorColor = Color.FromArgb(240, 110, 100);

        // ----- layout -------------------------------------------------------------------------------------------

        private void BuildLayout()
        {
            var stack = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = Padding.Empty };
            stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _textBody = OrderTable();
            _textHeader = AddSection(stack, "Text", _textBody, true);
            AddSection(stack, "Engrave", EngraveTable(), true);
            AddSection(stack, "Existing Engravings", ExistingTable(), true);
            AddSection(stack, "Settings", SettingsTable(), false);

            var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
            scroll.Controls.Add(stack);

            // the main button on the left, as in Inventor
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 38, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Padding = new Padding(8, 7, 8, 0) };
            _primary.Margin = new Padding(0, 0, 6, 0);
            _secondary.Margin = new Padding(0, 0, 6, 0);
            _skip.Margin = Padding.Empty;
            buttons.Controls.Add(_primary);
            buttons.Controls.Add(_secondary);
            buttons.Controls.Add(_skip);

            Controls.Add(scroll);
            Controls.Add(_status);
            Controls.Add(buttons);
        }

        private SectionHeader AddSection(TableLayoutPanel stack, string title, Control body, bool open)
        {
            var header = new SectionHeader(_theme, title, open) { Font = Font, Dock = DockStyle.Fill, Margin = Padding.Empty };
            body.Dock = DockStyle.Fill;
            body.Margin = new Padding(8, 6, 8, 6);
            body.Visible = open;
            header.Toggled += isOpen => body.Visible = isOpen;
            int r = stack.RowCount++;
            stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            stack.Controls.Add(header, 0, r);
            r = stack.RowCount++;
            stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            stack.Controls.Add(body, 0, r);
            return header;
        }

        private static TableLayoutPanel Table()
        {
            var t = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, LabelWidth));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            return t;
        }

        private TableLayoutPanel OrderTable()
        {
            var t = Table();
            var intro = new Label { Text = "iProperties in front of the part name, in this order:", AutoSize = true, Tag = "keep", ForeColor = _theme.Dim };
            Wide(t, intro);
            _propList.ColumnCount = 5;
            _propList.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, LabelWidth));
            _propList.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 40));
            _propList.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _propList.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 40));
            _propList.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 30));
            Label Title(string text) => new Label { Text = text, AutoSize = true, Tag = "keep", ForeColor = _theme.Dim, BackColor = Color.Transparent, Margin = new Padding(0, 0, 0, 0), Font = new Font(Font.FontFamily, 7.5f) };
            _colBefore = Title("before");
            _colValue = Title("value");
            _colAfter = Title("after");
            _propList.Visible = false;
            Wide(t, _propList);
            _propList.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            _propAdd = new InventorButton(_theme, "+ iProperty", false) { Width = 96 };
            Wide(t, _propAdd);
            Wide(t, _propHint);
            Row(t, "Name length", _maxChars);
            var hint = new Label { Text = "The most characters of the part name (part number) that are engraved.", AutoSize = false, Height = 32, Dock = DockStyle.Fill, Tag = "keep", ForeColor = _theme.Dim };
            Wide(t, hint);
            return t;
        }

        private TableLayoutPanel EngraveTable()
        {
            var t = Table();
            if (_assembly)
            {
                _parts.Columns.Add("Part", 240);
                _parts.SmallImageList = new ImageList { ImageSize = new Size(1, PartRowHeight) };   // sets the row height
                _parts.DrawItem += DrawPartRow;
                _parts.Resize += (s, e) => FitColumn(_parts);
                _parts.Dock = DockStyle.Fill;
                Wide(t, _parts);
            }
            Row(t, "Text", _text);
            Row(t, "Face", _pick);
            Wide(t, _info);
            return t;
        }

        private Control ExistingTable()
        {
            _existing.Columns.Add("Engraving", 240);
            _existing.SmallImageList = new ImageList { ImageSize = new Size(1, 40) };
            _existing.DrawItem += DrawExistingRow;
            _existing.Resize += (s, e) => FitColumn(_existing);

            _edit = new IconButton(_theme, "Edit the text sketch of the selected engraving", ActionIcons.Edit);
            _delete = new IconButton(_theme, "Delete the selected engraving", ActionIcons.Delete);

            var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = Padding.Empty, WrapContents = false };
            _edit.Margin = new Padding(0, 6, 4, 0);
            _delete.Margin = new Padding(0, 6, 12, 0);
            _deleteAll.Margin = new Padding(0, 7, 0, 0);
            buttons.Controls.Add(_edit);
            buttons.Controls.Add(_delete);
            buttons.Controls.Add(_deleteAll);

            var t = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _existing.Dock = DockStyle.Top;
            t.Controls.Add(_existing, 0, 0);
            t.Controls.Add(buttons, 0, 1);
            t.Controls.Add(_existingInfo, 0, 2);
            return t;
        }

        private TableLayoutPanel SettingsTable()
        {
            var t = Table();
            Row(t, "Depth (mm)", _depth);
            Row(t, "Height (mm)", _height);
            Wide(t, _topInside);
            Row(t, "Placeholder", _placeholder);
            var hint = new Label { Text = "Empty height: Inventor's default text size. The depth is never more than a quarter of the sheet thickness. Placeholder: an iProperty value that means \"not filled in yet\" (for example 160100); such a value is refused.", AutoSize = false, Height = 92, Dock = DockStyle.Fill, Tag = "keep", ForeColor = _theme.Dim };
            Wide(t, hint);
            return t;
        }

        private static void FitColumn(ListView list)
        {
            if (list.Columns.Count > 0) list.Columns[0].Width = Math.Max(60, list.ClientSize.Width);
        }

        private void DrawPartRow(object sender, DrawListViewItemEventArgs e)
        {
            var row = e.Item.Tag as PartRow;
            Rectangle b = e.Bounds;
            using (var fill = new SolidBrush(e.Item.Selected ? _theme.AccentFill : _theme.Field)) e.Graphics.FillRectangle(fill, b);
            if (row == null) return;
            Color ink = _parts.Enabled ? _theme.Text : _theme.Dim;
            if (row.Engraved) DrawCheck(e.Graphics, new Rectangle(b.X + 5, b.Y + (b.Height - 14) / 2, 14, 14), row.Done ? _theme.Link : _theme.Dim);
            TextRenderer.DrawText(e.Graphics, row.Name, _parts.Font, new Rectangle(b.X + 24, b.Y, b.Width - 116, b.Height), ink,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(e.Graphics, row.Status, _parts.Font, new Rectangle(b.Right - 92, b.Y, 86, b.Height),
                row.Done ? _theme.Link : _theme.Dim, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        // ----- iProperty pick menu -------------------------------------------------------------------------------

        /// <summary>
        /// Like the "+" menu of the iProperty Manager: a submenu per tab of Inventor's iProperties dialog, and a Custom
        /// submenu with the custom iProperties of the open document (and their values) and a box to type any name.
        /// </summary>
        private ContextMenuStrip PropertyMenu()
        {
            var menu = new ThemedMenu(_theme);
            foreach ((string tab, string[] names) in PropertyCatalog.Tabs)
            {
                var sub = new ToolStripMenuItem(tab);
                foreach (string n in names) sub.DropDownItems.Add(PickItem(n, n));
                ThemedMenu.Style(sub, _theme);
                menu.Items.Add(sub);
            }

            var custom = new ToolStripMenuItem("Custom");
            IList<KeyValuePair<string, string>> existing = null;
            try { existing = CustomProperties?.Invoke(); } catch { }
            if (existing != null && existing.Count > 0)
            {
                foreach (KeyValuePair<string, string> kv in existing)
                    custom.DropDownItems.Add(PickItem(kv.Key, kv.Value.Length > 0 ? kv.Key + "   (" + kv.Value + ")" : kv.Key));
                custom.DropDownItems.Add(new ToolStripSeparator());
            }
            var typed = new ToolStripTextBox { AutoSize = false, Width = 180, BackColor = _theme.Back, ForeColor = _theme.Text, BorderStyle = BorderStyle.FixedSingle, ToolTipText = "Type a name and press Enter" };
            typed.KeyDown += (s, e) =>
            {
                if (e.KeyCode != Keys.Enter) return;
                e.SuppressKeyPress = true;
                menu.Close();
                AddPropertyRow(typed.Text, true);
            };
            custom.DropDownItems.Add(new ToolStripLabel("Other name (Enter to use):") { ForeColor = _theme.Dim });
            custom.DropDownItems.Add(typed);
            custom.DropDownOpened += (s, e) => typed.Focus();
            ThemedMenu.Style(custom, _theme);
            menu.Items.Add(custom);
            return menu;
        }

        private ToolStripMenuItem PickItem(string name, string text)
        {
            bool added = _propRows.Exists(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
            var item = new ToolStripMenuItem(text) { Enabled = !added, ForeColor = added ? _theme.Dim : _theme.Text };
            item.Click += (s, e) => AddPropertyRow(name, true);
            return item;
        }

        /// <summary>A tick: bright for parts engraved in this run, dim for parts that were engraved before.</summary>
        private static void DrawCheck(Graphics g, Rectangle r, Color color)
        {
            var mode = g.SmoothingMode;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var pen = new Pen(color, 2f) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round, LineJoin = System.Drawing.Drawing2D.LineJoin.Round })
                g.DrawLines(pen, new[] { new PointF(r.X + 2, r.Y + r.Height * 0.55f), new PointF(r.X + r.Width * 0.4f, r.Bottom - 2), new PointF(r.Right - 1, r.Y + 2) });
            g.SmoothingMode = mode;
        }

        private void DrawExistingRow(object sender, DrawListViewItemEventArgs e)
        {
            var row = e.Item.Tag as EngravingRow;
            Rectangle b = e.Bounds;
            using (var fill = new SolidBrush(e.Item.Selected ? _theme.AccentFill : _theme.Field)) e.Graphics.FillRectangle(fill, b);
            using (var line = new Pen(_theme.Back)) e.Graphics.DrawLine(line, b.Left, b.Bottom - 1, b.Right, b.Bottom - 1);
            if (row == null) return;
            TextRenderer.DrawText(e.Graphics, row.Name, _existing.Font, new Rectangle(b.X + 8, b.Y + 3, b.Width - 16, 18), _theme.Text,
                TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            string second = row.Suppressed ? "Suppressed" : row.Text.Length > 0 ? "“" + row.Text + "”" : "(no text)";
            TextRenderer.DrawText(e.Graphics, second, _existing.Font, new Rectangle(b.X + 8, b.Y + 21, b.Width - 16, 16), _theme.Dim,
                TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }

        private void Row(TableLayoutPanel t, string label, Control c)
        {
            int r = t.RowCount++;
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var l = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left };
            c.Margin = new Padding(0, 3, 0, 3);
            if (c is FieldBox || c is SelectorField) c.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            t.Controls.Add(l, 0, r);
            t.Controls.Add(c, 1, r);
            _rowLabels[c] = l;
        }

        /// <summary>A control over both columns.</summary>
        private static void Wide(TableLayoutPanel t, Control c)
        {
            int r = t.RowCount++;
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            c.Margin = new Padding(0, 3, 0, 3);
            t.Controls.Add(c, 0, r);
            t.SetColumnSpan(c, 2);
        }

        private void ShowRow(Control c, bool visible)
        {
            c.Visible = visible;
            if (_rowLabels.TryGetValue(c, out Label l)) l.Visible = visible;
        }

        // ----- behaviour ----------------------------------------------------------------------------------------

        private void WireEvents()
        {
            _propAdd.Click += (s, e) => PropertyMenu().Show(_propAdd, new Point(0, _propAdd.Height));
            _text.TextChanged += (s, e) => { if (!_loading) { _text.Invalid = false; TextEdited?.Invoke(); } };
            _parts.SelectedIndexChanged += (s, e) =>
            {
                if (_loading || _parts.SelectedIndices.Count != 1) return;
                PartChosen?.Invoke(_parts.SelectedIndices[0]);
            };
            // A click on the row that is already selected changes no selection, but should still go on at that part.
            int selectedBefore = -1;
            _parts.MouseDown += (s, e) => selectedBefore = _parts.SelectedIndices.Count == 1 ? _parts.SelectedIndices[0] : -1;
            _parts.MouseClick += (s, e) =>
            {
                ListViewItem hit = _parts.HitTest(e.Location).Item;
                if (hit != null && hit.Index == selectedBefore) PartChosen?.Invoke(hit.Index);
            };
            _pick.Clicked += () => PickClicked?.Invoke();
            _pick.ClearClicked += () => PickClicked?.Invoke();

            _existing.SelectedIndexChanged += (s, e) => UpdateExistingButtons();
            _existing.DoubleClick += (s, e) => { int i = SelectedExisting(); if (i >= 0) EditRequested?.Invoke(i); };
            _edit.Click += (s, e) => { int i = SelectedExisting(); if (i >= 0) EditRequested?.Invoke(i); };
            _delete.Click += (s, e) => { int i = SelectedExisting(); if (i >= 0) DeleteRequested?.Invoke(i); };
            _deleteAll.Click += (s, e) => DeleteAllRequested?.Invoke();

            foreach (FieldBox box in new[] { _depth, _height, _maxChars, _placeholder })
                box.TextChanged += (s, e) => { if (!_loading) SettingsChanged?.Invoke(); };
            _topInside.CheckedChanged += (s, e) => { if (!_loading) SettingsChanged?.Invoke(); };

            _primary.Click += (s, e) => PrimaryClicked?.Invoke();
            _secondary.Click += (s, e) => SecondaryClicked?.Invoke();
            _skip.Click += (s, e) => TertiaryClicked?.Invoke();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape)
            {
                if (_mode == PanelMode.EditingExisting) PrimaryClicked?.Invoke();
                else SecondaryClicked?.Invoke();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }
}
