using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace WiFile.UI
{
    /// <summary>Windows 11 style flat button: icon glyph and/or text, rounded hover/pressed background.</summary>
    class FlatButton : Control
    {
        string _glyph = "";
        bool _hover, _down, _accent;
        readonly ToolTip _tip = new ToolTip();

        public FlatButton(string glyph, string text = null, string tooltip = null)
        {
            _glyph = glyph ?? "";
            Text = text ?? "";
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;
            if (tooltip != null) _tip.SetToolTip(this, tooltip);
            AccessibleDescription = tooltip;
            AccessibleName = string.IsNullOrEmpty(text) ? tooltip : text;
            AutoFit();
        }

        public string Glyph { get => _glyph; set { _glyph = value ?? ""; AutoFit(); Invalidate(); } }
        public bool Accent { get => _accent; set { _accent = value; Invalidate(); } }
        public bool ShowChevron { get; set; }
        public int FixedWidth { get; set; }
        public void SetTip(string tip) { _tip.SetToolTip(this, tip); AccessibleDescription = tip; }

        protected override AccessibleObject CreateAccessibilityInstance() => new ButtonAccessible(this);

        sealed class ButtonAccessible : ControlAccessibleObject
        {
            readonly FlatButton _b;
            public ButtonAccessible(FlatButton b) : base(b) { _b = b; }
            public override AccessibleRole Role => AccessibleRole.PushButton;
            public override string Name => _b.AccessibleName ?? (_b.Text.Length > 0 ? _b.Text : _b._tip.GetToolTip(_b));
            public override string DefaultAction => "Press";
            public override void DoDefaultAction() { if (_b.Enabled) _b.OnClick(EventArgs.Empty); }
        }

        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); AutoFit(); Invalidate(); }

        public void AutoFit()
        {
            int w = Theme.S(8);
            if (_glyph.Length > 0) w += TextRenderer.MeasureText(_glyph, Theme.Icons, Size.Empty, TextFormatFlags.NoPadding).Width + Theme.S(8);
            if (Text.Length > 0) w += TextRenderer.MeasureText(Text, Theme.Base, Size.Empty, TextFormatFlags.NoPadding).Width + Theme.S(_glyph.Length > 0 ? 6 : 8);
            if (ShowChevron) w += Theme.S(16);
            Size = new Size(FixedWidth > 0 ? FixedWidth : Math.Max(Theme.S(34), w), Theme.S(34));
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { _down = true; Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent?.BackColor ?? Theme.Window);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            Color bg = Color.Empty, fg = Enabled ? Theme.Text : Theme.Muted;
            if (_accent)
            {
                bg = !Enabled ? Theme.Hover : _down ? Theme.Accent : _hover ? Theme.AccentHover : Theme.Accent;
                fg = Enabled ? Theme.OnAccent : Theme.Muted;
            }
            else if (Enabled && (_hover || _down)) bg = _down ? Theme.Pressed : Theme.Hover;
            if (bg != Color.Empty)
                using (var path = Theme.Rounded(r, Theme.S(5)))
                using (var b = new SolidBrush(bg))
                    g.FillPath(b, path);

            int x = Theme.S(8);
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
            bool iconOnly = Text.Length == 0 && !ShowChevron;
            if (_glyph.Length > 0)
            {
                if (iconOnly)
                    TextRenderer.DrawText(g, _glyph, Theme.Icons, r, fg, flags | TextFormatFlags.HorizontalCenter);
                else
                {
                    var gw = TextRenderer.MeasureText(_glyph, Theme.Icons, Size.Empty, TextFormatFlags.NoPadding).Width;
                    TextRenderer.DrawText(g, _glyph, Theme.Icons, new Rectangle(x, 0, gw, Height), fg, flags);
                    x += gw + Theme.S(6);
                }
            }
            if (Text.Length > 0)
            {
                int right = ShowChevron ? Width - Theme.S(20) : Width - Theme.S(4);
                TextRenderer.DrawText(g, Text, Theme.Base, new Rectangle(x, 0, right - x, Height), fg, flags | TextFormatFlags.EndEllipsis);
            }
            if (ShowChevron)
                TextRenderer.DrawText(g, "\uE70D", Theme.IconsSmall, new Rectangle(Width - Theme.S(20), 0, Theme.S(14), Height), Theme.Muted, flags);
        }
    }

    /// <summary>Clickable path: Shared \u203A Photos \u203A 2024. Collapses leading parts into "\u2026" when narrow.</summary>
    sealed class Breadcrumb : Control
    {
        sealed class Part { public string Name, Path; public Rectangle Bounds; }

        readonly List<Part> _parts = new List<Part>();
        int _hover = -1;
        string _root;

        public event Action<string> Navigate;

        public Breadcrumb()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            Height = Theme.S(34);
            AccessibleName = "Location";
        }

        public void SetPath(string root, string current)
        {
            _root = root.TrimEnd('\\');
            _parts.Clear();
            _parts.Add(new Part { Name = "Shared", Path = _root });
            var rel = current != null && current.Length > _root.Length ? current.Substring(_root.Length).Trim('\\') : "";
            var acc = _root;
            if (rel.Length > 0)
                foreach (var seg in rel.Split('\\'))
                {
                    acc = Path.Combine(acc, seg);
                    _parts.Add(new Part { Name = seg, Path = acc });
                }
            Relayout();
            Invalidate();
        }

        int _first; // first visible part index (after collapsing)

        void Relayout()
        {
            int chevron = Theme.S(22), pad = Theme.S(8);
            foreach (var p in _parts)
                p.Bounds = new Rectangle(0, 0, TextRenderer.MeasureText(p.Name, _parts.IndexOf(p) == _parts.Count - 1 ? Theme.Bold : Theme.Base).Width + pad, Height);
            _first = 0;
            int ellipsisW = Theme.S(28);
            Func<int> total = () => _parts.Skip(_first).Sum(p => p.Bounds.Width + chevron) - chevron + (_first > 0 ? ellipsisW + chevron : 0);
            while (_first < _parts.Count - 1 && total() > Width) _first++;
            int x = _first > 0 ? ellipsisW + chevron : 0;
            for (int i = 0; i < _parts.Count; i++)
            {
                if (i < _first) { _parts[i].Bounds = Rectangle.Empty; continue; }
                _parts[i].Bounds = new Rectangle(x, Theme.S(3), _parts[i].Bounds.Width, Height - Theme.S(6));
                x += _parts[i].Bounds.Width + chevron;
            }
        }

        Rectangle EllipsisRect => new Rectangle(0, Theme.S(3), Theme.S(28), Height - Theme.S(6));

        protected override void OnResize(EventArgs e) { base.OnResize(e); Relayout(); }

        int HitTest(Point pt)
        {
            if (_first > 0 && EllipsisRect.Contains(pt)) return -2;
            for (int i = _first; i < _parts.Count - 1; i++) // the last part is the current folder
                if (_parts[i].Bounds.Contains(pt)) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = HitTest(e.Location);
            if (h != _hover) { _hover = h; Cursor = h == -1 ? Cursors.Default : Cursors.Hand; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { _hover = -1; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            int h = HitTest(e.Location);
            if (h >= 0) Navigate?.Invoke(_parts[h].Path);
            else if (h == -2) Navigate?.Invoke(_parts[_first - 1].Path);
            base.OnMouseClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent?.BackColor ?? Theme.Toolbar);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
            int chevron = Theme.S(22);
            if (_first > 0)
            {
                DrawHover(g, EllipsisRect, _hover == -2);
                TextRenderer.DrawText(g, "\u2026", Theme.Base, EllipsisRect, Theme.Text, flags);
                TextRenderer.DrawText(g, "\uE76C", Theme.IconsSmall, new Rectangle(EllipsisRect.Right, 0, chevron, Height), Theme.Muted, flags);
            }
            for (int i = _first; i < _parts.Count; i++)
            {
                var p = _parts[i];
                bool last = i == _parts.Count - 1;
                DrawHover(g, p.Bounds, _hover == i);
                TextRenderer.DrawText(g, p.Name, last ? Theme.Bold : Theme.Base, p.Bounds, last ? Theme.Text : Theme.Muted, flags);
                if (!last)
                    TextRenderer.DrawText(g, "\uE76C", Theme.IconsSmall, new Rectangle(p.Bounds.Right, 0, chevron, Height), Theme.Muted, flags);
            }
        }

        protected override AccessibleObject CreateAccessibilityInstance() => new CrumbAccessible(this);

        sealed class CrumbAccessible : ControlAccessibleObject
        {
            readonly Breadcrumb _c;
            public CrumbAccessible(Breadcrumb c) : base(c) { _c = c; }
            public override AccessibleRole Role => AccessibleRole.ToolBar;
            public override string Name => "Location";
            public override int GetChildCount() => _c._parts.Count;
            public override AccessibleObject GetChild(int index) => index >= 0 && index < _c._parts.Count ? new PartAccessible(this, _c, index) : null;
        }

        sealed class PartAccessible : AccessibleObject
        {
            readonly AccessibleObject _parent;
            readonly Breadcrumb _c;
            readonly int _i;
            public PartAccessible(AccessibleObject parent, Breadcrumb c, int i) { _parent = parent; _c = c; _i = i; }
            public override string Name => _c._parts[_i].Name;
            public override AccessibleRole Role => AccessibleRole.Link;
            public override AccessibleObject Parent => _parent;
            public override string DefaultAction => "Open";
            public override Rectangle Bounds => _c.RectangleToScreen(_c._parts[_i].Bounds);
            public override void DoDefaultAction() => _c.Navigate?.Invoke(_c._parts[_i].Path);
        }

        static void DrawHover(Graphics g, Rectangle r, bool on)
        {
            if (!on) return;
            using (var path = Theme.Rounded(r, Theme.S(5)))
            using (var b = new SolidBrush(Theme.Hover))
                g.FillPath(b, path);
        }
    }

    /// <summary>Menus that match the current theme.</summary>
    sealed class ThemedRenderer : ToolStripRenderer
    {
        public static ContextMenuStrip Menu()
        {
            var m = new ContextMenuStrip { Renderer = new ThemedRenderer(), ShowImageMargin = false, ShowCheckMargin = true, Font = Theme.Base, Padding = Theme.S(4, 4, 4, 4) };
            m.Opening += (s, e) => Style(m);
            return m;
        }

        public static void Style(ToolStripDropDown m)
        {
            m.BackColor = Theme.Dark ? Color.FromArgb(44, 44, 44) : Color.FromArgb(249, 249, 249);
            m.ForeColor = Theme.Text;
            foreach (ToolStripItem i in m.Items)
            {
                i.ForeColor = i.Enabled ? Theme.Text : Theme.Muted;
                i.Padding = Theme.S(4, 5, 12, 5);
                if (i is ToolStripMenuItem mi && mi.HasDropDownItems) Style(mi.DropDown);
            }
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using (var b = new SolidBrush(e.ToolStrip.BackColor)) e.Graphics.FillRectangle(b, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using (var p = new Pen(Theme.Dark ? Color.FromArgb(70, 70, 70) : Color.FromArgb(215, 215, 215)))
                e.Graphics.DrawRectangle(p, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(Theme.S(2), 0, e.Item.Width - Theme.S(4), e.Item.Height);
            using (var path = Theme.Rounded(r, Theme.S(4)))
            using (var b = new SolidBrush(Theme.Dark ? Color.FromArgb(61, 61, 61) : Color.FromArgb(232, 232, 232)))
                e.Graphics.FillPath(b, path);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Theme.Text : Theme.Muted;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var r = e.ImageRectangle;
            TextRenderer.DrawText(e.Graphics, "\uE73E", Theme.IconsSmall, new Rectangle(r.X, 0, r.Width + Theme.S(4), e.Item.Height), Theme.Accent,
                TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using (var p = new Pen(Theme.Dark ? Color.FromArgb(70, 70, 70) : Color.FromArgb(222, 222, 222)))
                e.Graphics.DrawLine(p, Theme.S(8), y, e.Item.Width - Theme.S(8), y);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Theme.Muted;
            base.OnRenderArrow(e);
        }
    }

    /// <summary>Small themed text prompt (used to change your device name).</summary>
    sealed class Prompt : Form
    {
        readonly TextBox _box;

        Prompt(string title, string label, string value)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            Font = Theme.Base;
            BackColor = Theme.Window;
            ForeColor = Theme.Text;
            ClientSize = Theme.S(380, 150);
            var lbl = new Label { Text = label, AutoSize = true, Location = new Point(Theme.S(16), Theme.S(16)), ForeColor = Theme.Text };
            var frame = new Panel { Location = new Point(Theme.S(16), Theme.S(44)), Size = Theme.S(348, 34), BackColor = Theme.InputBack, Padding = Theme.S(8, 8, 8, 4) };
            frame.Paint += (s, e) =>
            {
                using (var p = new Pen(Theme.Border)) e.Graphics.DrawRectangle(p, 0, 0, frame.Width - 1, frame.Height - 1);
                using (var p = new Pen(Theme.Accent, Theme.S(2))) e.Graphics.DrawLine(p, 0, frame.Height - 1, frame.Width, frame.Height - 1);
            };
            _box = new TextBox { Text = value, BorderStyle = BorderStyle.None, Dock = DockStyle.Fill, BackColor = Theme.InputBack, ForeColor = Theme.Text, Font = Theme.Base };
            frame.Controls.Add(_box);
            var ok = new FlatButton(null, "Save") { Accent = true, FixedWidth = Theme.S(96) };
            ok.AutoFit();
            var cancel = new FlatButton(null, "Cancel") { FixedWidth = Theme.S(96) };
            cancel.AutoFit();
            ok.Location = new Point(ClientSize.Width - Theme.S(16) - ok.Width * 2 - Theme.S(8), Theme.S(100));
            cancel.Location = new Point(ClientSize.Width - Theme.S(16) - cancel.Width, Theme.S(100));
            ok.Click += (s, e) => { DialogResult = DialogResult.OK; Close(); };
            cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            _box.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; DialogResult = DialogResult.OK; Close(); }
                if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; Close(); }
            };
            Controls.AddRange(new Control[] { lbl, frame, ok, cancel });
            HandleCreated += (s, e) => Theme.ApplyTitleBar(this);
            Shown += (s, e) => { _box.Focus(); _box.SelectAll(); };
        }

        public static string Ask(IWin32Window owner, string title, string label, string value)
        {
            using (var p = new Prompt(title, label, value))
                return p.ShowDialog(owner) == DialogResult.OK ? p._box.Text : null;
        }
    }
}
