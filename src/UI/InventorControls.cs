using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Engrave.UI
{
    /// <summary>Section bar with a chevron, like "Input Geometry" in Inventor's property panels; click to fold.</summary>
    internal sealed class SectionHeader : Control
    {
        private readonly Theme _theme;
        private bool _open;

        public event Action<bool> Toggled;

        public SectionHeader(Theme theme, string title, bool open)
        {
            _theme = theme;
            Text = title;
            _open = open;
            Height = 20;
            Dock = DockStyle.Top;
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        public bool Open { get => _open; set { _open = value; Invalidate(); } }

        protected override void OnClick(EventArgs e)
        {
            _open = !_open;
            Invalidate();
            Toggled?.Invoke(_open);
            base.OnClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(_theme.Header);
            // Autodesk Weave caret, or a drawn triangle if the icon cannot be loaded
            if (!WeaveIcons.Draw(g, new Rectangle(4, 4, 12, 12), _open ? "caret-down-xs" : "caret-right-xs"))
            {
                using (var tri = new SolidBrush(_theme.Dim))
                {
                    PointF[] pts = _open
                        ? new[] { new PointF(7, 8), new PointF(14, 8), new PointF(10.5f, 13) }
                        : new[] { new PointF(8.5f, 5.5f), new PointF(8.5f, 12.5f), new PointF(13.5f, 9) };
                    g.FillPolygon(tri, pts);
                }
            }
            TextRenderer.DrawText(g, Text, Font, new Rectangle(22, 0, Width - 22, Height), _theme.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        }
    }

    /// <summary>Flat dark edit field with Inventor's focus outline.</summary>
    internal sealed class FieldBox : Panel
    {
        private readonly Theme _theme;
        private readonly TextBox _text = new TextBox { BorderStyle = BorderStyle.None };
        private bool _focused;
        private int _min = int.MinValue, _max = int.MaxValue;
        private bool _integer;

        public new event EventHandler TextChanged;

        public FieldBox(Theme theme)
        {
            _theme = theme;
            Height = 22;
            BackColor = theme.Field;
            Padding = new Padding(5, 4, 5, 0);
            _text.Dock = DockStyle.Fill;
            _text.BackColor = theme.Field;
            _text.ForeColor = theme.Text;
            Controls.Add(_text);
            _text.TextChanged += (s, e) => { Invalidate(); TextChanged?.Invoke(this, e); };
            _text.Enter += (s, e) => { _focused = true; Invalidate(); };
            _text.Leave += (s, e) => { _focused = false; Invalidate(); };
            _text.KeyDown += OnKeyDown;
        }

        public override string Text { get => _text.Text; set => _text.Text = value; }

        public bool ReadOnly
        {
            get => _text.ReadOnly;
            set { _text.ReadOnly = value; _text.TabStop = !value; _text.ForeColor = value ? _theme.Dim : _theme.Text; }
        }

        public bool Invalid
        {
            set { BackColor = _text.BackColor = value ? _theme.FieldInvalid : _theme.Field; }
        }

        /// <summary>Makes the field a whole-number spinner (arrow keys and mouse wheel step it).</summary>
        public void AsInteger(int min, int max)
        {
            _integer = true; _min = min; _max = max;
            _text.MouseWheel += (s, e) => Step(e.Delta > 0 ? 1 : -1);
        }

        public int IntValue
        {
            get => int.TryParse(_text.Text.Trim(), out int v) ? Math.Max(_min, Math.Min(_max, v)) : _min;
            set => _text.Text = Math.Max(_min, Math.Min(_max, value)).ToString();
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (!_integer) return;
            if (e.KeyCode == Keys.Up) { Step(1); e.Handled = true; }
            else if (e.KeyCode == Keys.Down) { Step(-1); e.Handled = true; }
        }

        private void Step(int d) => IntValue = IntValue + d;

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            // Same as the face selectors in Input Geometry (minus the pick arrow): a theme-coloured underline while the
            // field is being edited, the normal text colour once it holds a value, nothing when it is empty or read-only.
            bool hasValue = _text.Text.Length > 0;
            if (!_focused && !hasValue) return;
            using (var pen = new Pen(_focused ? _theme.Accent : _theme.Text, 2f))
                e.Graphics.DrawLine(pen, 0, Height - 1.5f, Width, Height - 1.5f);
        }
    }

    /// <summary>
    /// A "Select Face" input like those of Inventor's property panels: red pick arrow, text, and a clear button once
    /// something is selected. Highlighted while it is the active selector.
    /// </summary>
    internal sealed class SelectorField : Control
    {
        private readonly Theme _theme;
        private bool _active;
        private bool _hasValue;
        private string _placeholder = "Select Face";
        private Image _arrow;

        public event Action Clicked;
        public event Action ClearClicked;

        public SelectorField(Theme theme)
        {
            _theme = theme;
            Height = 22;
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        public bool Active { get => _active; set { _active = value; Invalidate(); } }

        public void SetValue(string text, bool has)
        {
            _hasValue = has;
            Text = has ? text : _placeholder;
            Invalidate();
        }

        private Rectangle ClearRect => new Rectangle(Width - 22, 3, 16, 16);

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (_hasValue && ClearRect.Contains(e.Location)) ClearClicked?.Invoke();
            else Clicked?.Invoke();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(_theme.Field);   // the box itself is never filled with the accent colour

            // Inventor's own select arrow (white on dark themes, dark on light ones); a drawn arrow if the icon cannot be read
            _arrow = _arrow ?? InventorIcons.SelectArrow();
            if (_arrow != null) g.DrawImage(_arrow, new Rectangle(4, 3, 16, 16));
            else
            {
                var pts = new[] { new PointF(6, 4), new PointF(6, 16), new PointF(9.2f, 13), new PointF(11.5f, 18), new PointF(13.5f, 17), new PointF(11.2f, 12.3f), new PointF(15.5f, 12.3f) };
                using (var fill = new SolidBrush(_theme.Dark ? Color.White : Color.FromArgb(40, 46, 58)))
                using (var edge = new Pen(_theme.Dark ? Color.FromArgb(40, 46, 58) : Color.White))
                {
                    g.FillPolygon(fill, pts);
                    g.DrawPolygon(edge, pts);
                }
            }

            Rectangle textRect = new Rectangle(24, 0, Width - 24 - (_hasValue ? 26 : 4), Height);
            TextRenderer.DrawText(g, Text, Font, textRect, _hasValue ? _theme.Text : _theme.Dim,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);

            if (_hasValue)
            {
                Rectangle c = ClearRect;
                // Autodesk Weave close icon, otherwise a drawn circle with a cross
                if (WeaveIcons.Draw(g, new Rectangle(c.X, c.Y, 16, 16), "close-xs")) { }
                else using (var pen = new Pen(_theme.Dim, 1.4f))
                {
                    g.DrawEllipse(pen, c.X + 1, c.Y + 1, c.Width - 3, c.Height - 3);
                    g.DrawLine(pen, c.X + 5, c.Y + 5, c.Right - 6, c.Bottom - 6);
                    g.DrawLine(pen, c.Right - 6, c.Y + 5, c.X + 5, c.Bottom - 6);
                }
            }

            // Underline only: theme blue while this selector is waiting for a pick, the normal text colour once it holds
            // something, nothing at all when empty. Colours come from the Inventor theme, so every theme works.
            if (_active || _hasValue)
            {
                using (var pen = new Pen(_active ? _theme.Accent : _theme.Text, 2f))
                    g.DrawLine(pen, 0, Height - 1.5f, Width, Height - 1.5f);
            }
        }
    }

    /// <summary>
    /// OK / Cancel button drawn in Inventor's property-panel colours: dark body, light outline for Cancel, a blue outline for OK
    /// (dimmed while it is disabled). Windows' own button would fill a disabled button with its grey.
    /// </summary>
    internal sealed class InventorButton : Control
    {
        private readonly Theme _theme;
        private readonly bool _primary;
        private bool _hover, _down;

        public InventorButton(Theme theme, string text, bool primary)
        {
            _theme = theme;
            _primary = primary;
            Text = text;
            Size = new Size(74, 24);
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        }

        private static Color Mix(Color a, Color b, double t) => Color.FromArgb(
            (int)Math.Round(a.R + (b.R - a.R) * t), (int)Math.Round(a.G + (b.G - a.G) * t), (int)Math.Round(a.B + (b.B - a.B) * t));

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(_theme.Back);

            Color fill = _theme.Back, border, text = Enabled ? _theme.Text : _theme.Dim;
            if (_primary)
            {
                border = Enabled ? _theme.OkBorder : Mix(_theme.Back, _theme.Accent, 0.22);
                if (Enabled) fill = Mix(_theme.Back, _theme.Accent, _down ? 0.30 : _hover ? 0.22 : 0.12);
            }
            else
            {
                border = Enabled ? _theme.Dim : _theme.Outline;
                if (Enabled) fill = _down ? _theme.Header : _hover ? _theme.Hover : _theme.Back;
            }

            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = RoundedRectangle(r, 2))
            using (var brush = new SolidBrush(fill))
            using (var pen = new Pen(border))
            {
                g.FillPath(brush, path);
                g.DrawPath(pen, path);
            }
            TextRenderer.DrawText(g, Text, Font, r, text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        private static GraphicsPath RoundedRectangle(Rectangle r, int radius)
        {
            int d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    /// <summary>A small flat icon button (Edit, Delete...) with a tooltip, hover and disabled states.</summary>
    internal sealed class IconButton : Control
    {
        public delegate void IconDrawer(Graphics g, Rectangle bounds, Color ink);

        private readonly Theme _theme;
        private readonly IconDrawer _draw;
        private readonly ToolTip _tip = new ToolTip();
        private bool _hover;

        public IconButton(Theme theme, string tip, IconDrawer draw)
        {
            _theme = theme;
            _draw = draw;
            Size = new Size(30, 26);
            Cursor = Cursors.Hand;
            _tip.SetToolTip(this, tip);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = theme.Back;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(_theme.Back);
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            if (_hover && Enabled)
                using (var fill = new SolidBrush(_theme.Hover)) g.FillRectangle(fill, r);
            _draw(g, r, Enabled ? _theme.Text : _theme.Dim);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _tip.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>Icons of the Edit and Delete buttons under the list of existing engravings.</summary>
    internal static class ActionIcons
    {
        private static Rectangle Centre(Rectangle b) => new Rectangle(b.X + (b.Width - 16) / 2, b.Y + (b.Height - 16) / 2, 16, 16);

        public static void Edit(Graphics g, Rectangle b, Color ink)
        {
            if (WeaveIcons.Draw(g, Centre(b), "edit")) return;
            float cx = b.X + b.Width / 2f, cy = b.Y + b.Height / 2f;
            using (var pen = new Pen(ink, 1.4f))
            {
                g.DrawLine(pen, cx - 6, cy + 6, cx - 4, cy + 1);
                g.DrawLine(pen, cx - 4, cy + 1, cx + 4, cy - 7);
                g.DrawLine(pen, cx + 4, cy - 7, cx + 7, cy - 4);
                g.DrawLine(pen, cx + 7, cy - 4, cx - 1, cy + 4);
                g.DrawLine(pen, cx - 1, cy + 4, cx - 6, cy + 6);
            }
        }

        public static void Delete(Graphics g, Rectangle b, Color ink)
        {
            if (WeaveIcons.Draw(g, Centre(b), "close-s")) return;
            float cx = b.X + b.Width / 2f, cy = b.Y + b.Height / 2f;
            using (var pen = new Pen(ink, 1.6f))
            {
                g.DrawLine(pen, cx - 5, cy - 5, cx + 5, cy + 5);
                g.DrawLine(pen, cx + 5, cy - 5, cx - 5, cy + 5);
            }
        }
    }
}
