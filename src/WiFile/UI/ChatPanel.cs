using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using WiFile.Core;

namespace WiFile.UI
{
    /// <summary>Right-hand chat: online devices, message list, composer.</summary>
    sealed class ChatPanel : UserControl
    {
        const int MaxRows = 300;
        readonly WiFileNode _node;
        readonly MessageList _list = new MessageList();
        readonly Panel _header = new Panel(), _composer = new Panel(), _inputFrame = new Panel();
        readonly Label _title = new Label(), _online = new Label();
        readonly FlatButton _me, _to, _attach, _send;
        readonly CueTextBox _input = new CueTextBox();
        readonly Dictionary<string, MessageRow> _rows = new Dictionary<string, MessageRow>();
        readonly ContextMenuStrip _toMenu = ThemedRenderer.Menu();
        readonly ContextMenuStrip _msgMenu = ThemedRenderer.Menu();
        string _toId;

        public event Action<ChatMessage> Incoming;

        public ChatPanel(WiFileNode node)
        {
            _node = node;
            Font = Theme.Base;
            AllowDrop = true;

            // ---- header: title, your name, who's online
            _header.Dock = DockStyle.Top;
            _header.Height = Theme.S(68);
            _title.Text = "Chat";
            _title.Font = Theme.Title;
            _title.AutoSize = true;
            _title.Location = new Point(Theme.S(14), Theme.S(10));
            _me = new FlatButton("\uE77B", _node.Name, "Your name as others see it \u2014 click to change");
            _me.Click += (s, e) => Rename();
            _online.AutoSize = false;
            _online.AutoEllipsis = true;
            _online.Location = new Point(Theme.S(16), Theme.S(44));
            _online.Height = Theme.S(20);
            _header.Controls.AddRange(new Control[] { _title, _me, _online });
            _header.Resize += (s, e) => LayoutHeader();
            _header.Paint += (s, e) => { using (var p = new Pen(Theme.Border)) e.Graphics.DrawLine(p, 0, _header.Height - 1, _header.Width, _header.Height - 1); };

            // ---- composer: recipient, input box, attach, send
            _composer.Dock = DockStyle.Bottom;
            _composer.Height = Theme.S(118);
            _to = new FlatButton("\uE716", "To: Everyone", "Choose who receives your messages") { ShowChevron = true };
            _to.AutoFit();
            _to.Click += (s, e) => { BuildRecipientMenu(); _toMenu.Show(_to, new Point(0, _to.Height)); };
            _attach = new FlatButton("\uE723", null, "Send files (or drag files here, or paste with Ctrl+V)");
            _attach.Click += (s, e) => PickFiles();
            _send = new FlatButton("\uE724", null, "Send (Enter)") { Accent = true };
            _send.Click += (s, e) => SendText();
            _input.Multiline = true;
            _input.Cue = "Message\u2026";
            _input.BorderStyle = BorderStyle.None;
            _input.ScrollBars = ScrollBars.None;
            _input.Font = Theme.Base;
            _input.Dock = DockStyle.Fill;
            _input.KeyDown += InputKeyDown;
            _inputFrame.Padding = Theme.S(10, 8, 10, 6);
            _inputFrame.Controls.Add(_input);
            _inputFrame.Paint += (s, e) => PaintInputFrame(e.Graphics);
            _input.GotFocus += (s, e) => _inputFrame.Invalidate();
            _input.LostFocus += (s, e) => _inputFrame.Invalidate();
            _composer.Controls.AddRange(new Control[] { _to, _inputFrame, _attach, _send });
            _composer.Resize += (s, e) => LayoutComposer();
            _composer.Paint += (s, e) => { using (var p = new Pen(Theme.Border)) e.Graphics.DrawLine(p, 0, 0, _composer.Width, 0); };

            // ---- messages
            _list.Dock = DockStyle.Fill;
            _list.WidthChanged += w => { foreach (var r in _rows.Values) r.LayoutFor(w); };

            Controls.Add(_list);
            Controls.Add(_composer);
            Controls.Add(_header);

            foreach (Control c in new Control[] { this, _list, _input })
            {
                c.AllowDrop = true;
                c.DragEnter += (s, e) => e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
                c.DragDrop += (s, e) => SendPaths(e.Data.GetData(DataFormats.FileDrop) as string[]);
            }

            _node.Chat.MessageReceived += m => UI(() => { Add(m); Incoming?.Invoke(m); });
            _node.Chat.MessageUpdated += m => UI(() => { if (_rows.TryGetValue(m.Id, out var r)) r.UpdateStatus(); });
            _node.Chat.MessageDeleted += id => UI(() => RemoveRow(id));
            _msgMenu.Opening += (s, e) => e.Cancel = !BuildMessageMenu(RowOf(_msgMenu.SourceControl));
            _node.Discovery.PeersChanged += () => UI(RefreshPeers);

            ApplyTheme();
            RefreshPeers();
            foreach (var m in _node.Chat.LoadHistory()) Add(m, false);
            _list.ScrollToEnd();
        }

        public void ApplyTheme()
        {
            BackColor = _list.BackColor = Theme.Surface;
            _header.BackColor = _composer.BackColor = Theme.Surface;
            _title.ForeColor = Theme.Text;
            _inputFrame.BackColor = _input.BackColor = Theme.InputBack;
            _input.ForeColor = Theme.Text;
            ThemedRenderer.Style(_toMenu);
            ThemedRenderer.Style(_msgMenu);
            Theme.ApplyScrollbars(_list);
            RefreshPeers();
            foreach (var r in _rows.Values) r.ApplyTheme();
            Invalidate(true);
        }

        void LayoutHeader()
        {
            _me.AutoFit();
            _me.Width = Math.Min(_me.Width, _header.Width / 2);
            _me.Location = new Point(_header.Width - _me.Width - Theme.S(8), Theme.S(8));
            _online.Width = _header.Width - Theme.S(28);
        }

        void LayoutComposer()
        {
            int w = _composer.ClientSize.Width, m = Theme.S(10);
            _to.Location = new Point(m - Theme.S(4), Theme.S(6));
            int top = _to.Bottom + Theme.S(4), h = _composer.ClientSize.Height - top - m;
            _send.Size = _attach.Size = new Size(Theme.S(40), Theme.S(40));
            _send.Location = new Point(w - m - _send.Width, top + h - _send.Height);
            _attach.Location = new Point(_send.Left - Theme.S(4) - _attach.Width, _send.Top);
            _inputFrame.SetBounds(m, top, _attach.Left - Theme.S(8) - m, h);
        }

        void PaintInputFrame(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.Surface);
            var r = new Rectangle(0, 0, _inputFrame.Width - 1, _inputFrame.Height - 1);
            using (var path = Theme.Rounded(r, Theme.S(6)))
            {
                using (var b = new SolidBrush(Theme.InputBack)) g.FillPath(b, path);
                using (var p = new Pen(Theme.Dark ? Color.FromArgb(70, 70, 70) : Color.FromArgb(210, 210, 210))) g.DrawPath(p, path);
            }
            if (_input.Focused)
                using (var p = new Pen(Theme.Accent, Theme.S(2)))
                    g.DrawLine(p, Theme.S(6), _inputFrame.Height - Theme.S(1), _inputFrame.Width - Theme.S(6), _inputFrame.Height - Theme.S(1));
        }

        // Background events can arrive before this control has a window handle (e.g. when WiFile
        // starts hidden in the tray); queue them and replay once the handle exists.
        readonly Queue<Action> _early = new Queue<Action>();

        void UI(Action a)
        {
            if (IsDisposed) return;
            lock (_early)
            {
                if (!IsHandleCreated)
                {
                    _early.Enqueue(a);
                    return;
                }
            }
            try { BeginInvoke(a); } catch (InvalidOperationException) { }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            lock (_early)
                while (_early.Count > 0) BeginInvoke(_early.Dequeue());
            BeginInvoke(new Action(() => { RefreshPeers(); Theme.ApplyScrollbars(_list); }));
        }

        public void FocusInput() => _input.Focus();

        void RefreshPeers()
        {
            var peers = _node.Discovery.Peers;
            _online.ForeColor = peers.Count > 0 ? Theme.Online : Theme.Muted;
            _online.Text = peers.Count == 0
                ? "\u25CB  Looking for other devices on this network\u2026"
                : $"\u25CF  {peers.Count} online: " + string.Join(", ", peers.Select(p => p.Name));
            if (_toId != null && peers.All(p => p.Id != _toId)) SetRecipient(null, null);
            _me.Text = _node.Name;
            LayoutHeader();
        }

        void BuildRecipientMenu()
        {
            _toMenu.Items.Clear();
            var everyone = new ToolStripMenuItem("Everyone") { Checked = _toId == null };
            everyone.Click += (s, e) => SetRecipient(null, null);
            _toMenu.Items.Add(everyone);
            var peers = _node.Discovery.Peers;
            if (peers.Count > 0) _toMenu.Items.Add(new ToolStripSeparator());
            foreach (var p in peers)
            {
                var item = new ToolStripMenuItem(p.Name) { Checked = p.Id == _toId };
                var id = p.Id;
                var name = p.Name;
                item.Click += (s, e) => SetRecipient(id, name);
                _toMenu.Items.Add(item);
            }
            ThemedRenderer.Style(_toMenu);
        }

        void SetRecipient(string id, string name)
        {
            _toId = id;
            _to.Text = "To: " + (id == null ? "Everyone" : name);
            _to.Glyph = id == null ? "\uE716" : "\uE77B";
            _to.AutoFit();
        }

        public void Rename()
        {
            var name = Prompt.Ask(FindForm(), "Your name", "Name other devices see for this PC:", _node.Name);
            if (string.IsNullOrWhiteSpace(name)) return;
            _node.Rename(name);
            RefreshPeers();
        }

        void InputKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter && !e.Shift)
            {
                e.SuppressKeyPress = true;
                SendText();
            }
            else if (e.KeyCode == Keys.V && e.Control)
            {
                if (Clipboard.ContainsFileDropList())
                {
                    e.SuppressKeyPress = true;
                    SendPaths(Clipboard.GetFileDropList().Cast<string>().ToArray());
                }
                else if (Clipboard.ContainsImage())
                {
                    e.SuppressKeyPress = true;
                    using (var img = Clipboard.GetImage())
                    {
                        if (img == null) return;
                        var dir = Path.Combine(_node.Config.DataDir, "sent");
                        Directory.CreateDirectory(dir);
                        var file = Path.Combine(dir, $"Screenshot {DateTime.Now:yyyy-MM-dd HHmmss}.png");
                        img.Save(file, System.Drawing.Imaging.ImageFormat.Png);
                        SendPaths(new[] { file });
                    }
                }
            }
        }

        void SendText()
        {
            var text = _input.Text.Trim();
            if (text.Length == 0) return;
            _input.Clear();
            Add(_node.Chat.SendText(text, _toId));
        }

        void PickFiles()
        {
            using (var dlg = new OpenFileDialog { Multiselect = true, Title = "Send files" })
                if (dlg.ShowDialog(this) == DialogResult.OK) SendPaths(dlg.FileNames);
        }

        void SendPaths(string[] paths)
        {
            if (paths == null) return;
            foreach (var p in paths)
            {
                try
                {
                    var file = p;
                    if (Directory.Exists(p))
                    {
                        // Folders travel as a zip.
                        var dir = Path.Combine(_node.Config.DataDir, "sent");
                        Directory.CreateDirectory(dir);
                        file = PathUtil.UniquePath(dir, Path.GetFileName(p.TrimEnd('\\')) + ".zip");
                        Cursor = Cursors.WaitCursor;
                        ZipFile.CreateFromDirectory(p, file, CompressionLevel.Fastest, true);
                        Cursor = Cursors.Default;
                    }
                    if (File.Exists(file)) Add(_node.Chat.SendFile(file, _toId));
                }
                catch (Exception ex)
                {
                    Cursor = Cursors.Default;
                    MessageBox.Show(this, "Could not send " + Path.GetFileName(p) + ":\n" + ex.Message, "WiFile", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        static MessageRow RowOf(Control c)
        {
            while (c != null && !(c is MessageRow)) c = c.Parent;
            return c as MessageRow;
        }

        /// <summary>Right-click menu of a message: copy / open / delete.</summary>
        bool BuildMessageMenu(MessageRow row)
        {
            if (row == null) return false;
            var m = row.Message;
            _msgMenu.Items.Clear();
            if (!string.IsNullOrEmpty(m.Text))
                _msgMenu.Items.Add("Copy text", null, (s, e) => { try { Clipboard.SetText(m.Text); } catch { } });
            if (m.IsFile && m.LocalPath != null && File.Exists(m.LocalPath))
            {
                _msgMenu.Items.Add("Open", null, (s, e) => Theme.Open(m.LocalPath));
                _msgMenu.Items.Add("Show in folder", null, (s, e) => Theme.ShowInFolder(m.LocalPath));
                _msgMenu.Items.Add("Copy file", null, (s, e) =>
                {
                    try { Clipboard.SetFileDropList(new System.Collections.Specialized.StringCollection { m.LocalPath }); } catch { }
                });
            }
            if (_msgMenu.Items.Count > 0) _msgMenu.Items.Add(new ToolStripSeparator());
            _msgMenu.Items.Add("Delete for me", null, (s, e) => _node.Chat.Delete(m, false));
            if (m.Outgoing)
                _msgMenu.Items.Add("Delete for everyone", null, (s, e) =>
                {
                    var ask = MessageBox.Show(FindForm(), "Delete this message for everyone?\n\nIt is removed from every device that is online now.",
                        "Delete message", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
                    if (ask == DialogResult.OK) _node.Chat.Delete(m, true);
                });
            ThemedRenderer.Style(_msgMenu);
            return true;
        }

        void RemoveRow(string id)
        {
            if (!_rows.TryGetValue(id, out var row)) return;
            _rows.Remove(id);
            _list.RemoveRow(row);
            row.Dispose();
        }

        static void SetMenu(Control c, ContextMenuStrip menu)
        {
            c.ContextMenuStrip = menu;
            foreach (Control child in c.Controls) SetMenu(child, menu);
        }

        void Add(ChatMessage m, bool scroll = true)
        {
            if (_rows.ContainsKey(m.Id)) return;
            var row = new MessageRow(m);
            SetMenu(row, _msgMenu);
            _rows[m.Id] = row;
            _list.AddRow(row);
            if (_rows.Count > MaxRows)
            {
                var oldest = _list.RemoveFirst();
                if (oldest != null)
                {
                    _rows.Remove(oldest.Message.Id);
                    oldest.Dispose();
                }
            }
            if (scroll) _list.ScrollToEnd();
        }
    }

    /// <summary>Vertical, double-buffered, auto-scrolling stack of message rows.</summary>
    sealed class MessageList : Panel
    {
        public event Action<int> WidthChanged;
        int _lastWidth = -1;
        bool _scrollPending;

        public MessageList()
        {
            AutoScroll = true;
            DoubleBuffered = true;
            Padding = new Padding(0, Theme.S(8), 0, Theme.S(8));
        }

        int Gap => Theme.S(2);

        public void AddRow(MessageRow row)
        {
            SuspendLayout();
            int y = Controls.Count == 0 ? Padding.Top + AutoScrollPosition.Y : Controls[Controls.Count - 1].Bottom + Gap;
            row.Location = new Point(0, y);
            row.LayoutFor(ClientSize.Width);
            row.HeightChanged += Restack;
            Controls.Add(row);
            ResumeLayout();
        }

        public void RemoveRow(MessageRow row)
        {
            row.HeightChanged -= Restack;
            Controls.Remove(row);
            Restack();
        }

        public MessageRow RemoveFirst()
        {
            if (Controls.Count == 0) return null;
            var r = (MessageRow)Controls[0];
            Controls.RemoveAt(0);
            Restack();
            return r;
        }

        void Restack()
        {
            SuspendLayout();
            int y = Padding.Top + AutoScrollPosition.Y;
            foreach (Control c in Controls)
            {
                c.Top = y;
                y += c.Height + Gap;
            }
            ResumeLayout();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (_scrollPending) ScrollToEnd();
        }

        public void ScrollToEnd()
        {
            if (Controls.Count == 0) return;
            if (!IsHandleCreated)
            {
                _scrollPending = true;
                return;
            }
            _scrollPending = false;
            BeginInvoke(new Action(() =>
            {
                if (Controls.Count == 0) return;
                ScrollControlIntoView(Controls[Controls.Count - 1]);
                VerticalScroll.Value = VerticalScroll.Maximum;
                PerformLayout();
            }));
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (ClientSize.Width == _lastWidth) return;
            _lastWidth = ClientSize.Width;
            SuspendLayout();
            WidthChanged?.Invoke(ClientSize.Width);
            Restack();
            ResumeLayout();
        }

        // Keep the scroll position when a child gets focus (e.g. a text bubble is clicked).
        protected override Point ScrollToControl(Control activeControl) => DisplayRectangle.Location;
    }

    /// <summary>One chat bubble: header (sender \u00B7 time \u00B7 status) + text / image preview / file card.</summary>
    sealed class MessageRow : Control
    {
        static readonly string[] ImageExt = { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tif", ".tiff", ".ico" };

        public ChatMessage Message { get; }
        public event Action HeightChanged;

        readonly Label _header = new Label { AutoSize = true };
        readonly RichTextBox _text;
        readonly PictureBox _image;
        readonly FileCard _file;
        readonly bool _mine;
        Rectangle _bubble;
        int _width;

        static int Pad => Theme.S(10);
        static int Side => Theme.S(12);

        Color Fill => _mine ? Theme.Mine : Theme.Theirs;

        public MessageRow(ChatMessage m)
        {
            Message = m;
            _mine = m.Outgoing;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            _header.Font = Theme.Small;
            Controls.Add(_header);

            bool localFile = m.LocalPath != null && File.Exists(m.LocalPath);
            if (m.IsFile && localFile && ImageExt.Contains(Path.GetExtension(m.FileName).ToLowerInvariant()) && TryThumb(m.LocalPath, out var thumb))
            {
                _image = new PictureBox { Image = thumb, Size = thumb.Size, Cursor = Cursors.Hand };
                _image.Click += (s, e) => Theme.Open(m.LocalPath);
                new ToolTip().SetToolTip(_image, m.FileName + " \u2014 click to open");
                Controls.Add(_image);
            }
            if (m.IsFile)
            {
                _file = new FileCard(m, localFile);
                Controls.Add(_file);
            }
            if (!string.IsNullOrEmpty(m.Text))
            {
                _text = new RichTextBox
                {
                    ReadOnly = true, BorderStyle = BorderStyle.None, ScrollBars = RichTextBoxScrollBars.None,
                    DetectUrls = true, Font = Theme.Base, TabStop = false, WordWrap = true, Cursor = Cursors.IBeam,
                };
                _text.Text = m.Text;
                _text.LinkClicked += (s, e) => Theme.Open(e.LinkText);
                _text.ContentsResized += (s, e) =>
                {
                    int h = e.NewRectangle.Height + Theme.S(2);
                    if (_text.Height != h) { _text.Height = h; Relayout(); }
                };
                Controls.Add(_text);
            }
            ApplyTheme();
            UpdateHeaderText();
        }

        public void ApplyTheme()
        {
            BackColor = Theme.Surface;
            _header.BackColor = Fill;
            _header.ForeColor = _mine ? Theme.Muted : Theme.ForName(Message.FromName);
            if (_image != null) _image.BackColor = Fill;
            if (_text != null)
            {
                _text.BackColor = Fill;
                _text.ForeColor = Theme.Text;
            }
            _file?.ApplyTheme(Fill);
            Invalidate(true);
        }

        static bool TryThumb(string path, out Image thumb)
        {
            thumb = null;
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var img = Image.FromStream(fs, false, false))
                {
                    int maxW = Theme.S(260), maxH = Theme.S(220);
                    double k = Math.Min(1.0, Math.Min((double)maxW / img.Width, (double)maxH / img.Height));
                    var sz = new Size(Math.Max(1, (int)(img.Width * k)), Math.Max(1, (int)(img.Height * k)));
                    var bmp = new Bitmap(sz.Width, sz.Height);
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.DrawImage(img, new Rectangle(Point.Empty, sz));
                    }
                    thumb = bmp;
                    return true;
                }
            }
            catch { return false; }
        }

        void UpdateHeaderText()
        {
            var m = Message;
            var time = m.LocalTime;
            var when = time.Date == DateTime.Today ? time.ToString("HH:mm") : time.ToString("MMM d, HH:mm");
            string who = _mine ? "You" : m.FromName;
            if (m.To != null) who += _mine ? " \u2192 " + (m.ToName ?? "private") : " \u2192 you (private)";
            _header.Text = who + "  \u00B7  " + when + (string.IsNullOrEmpty(m.Status) ? "" : "  \u00B7  " + m.Status);
        }

        public void UpdateStatus()
        {
            UpdateHeaderText();
            Relayout();
        }

        public void LayoutFor(int width)
        {
            _width = width;
            Relayout();
        }

        void Relayout()
        {
            if (_width <= 0) return;
            int maxInner = Math.Max(Theme.S(120), (int)(_width * 0.82) - 2 * Pad - 2 * Side);
            int contentW = _header.PreferredWidth;
            if (_text != null)
            {
                var sz = TextRenderer.MeasureText(_text.Text, _text.Font, new Size(maxInner, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
                contentW = Math.Max(contentW, Math.Min(maxInner, sz.Width + Theme.S(12)));
            }
            if (_image != null) contentW = Math.Max(contentW, _image.Width);
            if (_file != null) contentW = Math.Max(contentW, Math.Min(maxInner, _file.PreferredWidth));
            contentW = Math.Min(contentW, maxInner);

            int bubbleW = contentW + 2 * Pad;
            int x = _mine ? _width - Side - bubbleW : Side;
            int y = Pad - Theme.S(2);
            _header.Location = new Point(x + Pad, y);
            y = _header.Bottom + Theme.S(4);
            if (_image != null)
            {
                _image.Location = new Point(x + Pad, y);
                y = _image.Bottom + Theme.S(4);
            }
            if (_file != null)
            {
                _file.SetBounds(x + Pad, y, contentW, Theme.S(44));
                y = _file.Bottom;
            }
            if (_text != null)
            {
                if (_text.Width != contentW)
                {
                    _text.Width = contentW;
                    if (_text.IsHandleCreated) Native.SendMessage(_text.Handle, 0x0400 + 65 /* EM_REQUESTRESIZE */, IntPtr.Zero, IntPtr.Zero);
                }
                _text.Location = new Point(x + Pad - 1, y);
                y = _text.Bottom;
            }
            int bubbleH = y + Pad - Theme.S(2);
            _bubble = new Rectangle(x, Theme.S(2), bubbleW, bubbleH - Theme.S(2));
            int h = bubbleH + Theme.S(4);
            bool heightChanged = Height != h;
            SetBounds(Left, Top, _width, h);
            Invalidate();
            if (heightChanged) HeightChanged?.Invoke();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (_text != null && _text.IsHandleCreated)
                Native.SendMessage(_text.Handle, 0x0400 + 65, IntPtr.Zero, IntPtr.Zero);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            if (_bubble.Width <= 0) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Theme.Rounded(_bubble, Theme.S(10)))
            using (var b = new SolidBrush(Fill))
                e.Graphics.FillPath(b, path);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _image?.Image?.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>File attachment: shell icon, name, size, Open / Show in folder.</summary>
    sealed class FileCard : Panel
    {
        readonly PictureBox _icon = new PictureBox();
        readonly LinkLabel _name = new LinkLabel(), _meta = new LinkLabel();
        public int PreferredWidth { get; }

        public FileCard(ChatMessage m, bool exists)
        {
            Height = Theme.S(44);
            _icon.Size = Theme.S(32, 32);
            _icon.Location = new Point(0, Theme.S(6));
            _icon.SizeMode = PictureBoxSizeMode.Zoom;
            try { _icon.Image = ShellIcon(exists ? m.LocalPath : m.FileName, !exists); } catch { }
            _name.Text = m.FileName;
            _name.AutoSize = false;
            _name.AutoEllipsis = true;
            _name.Location = new Point(Theme.S(40), Theme.S(4));
            _name.Height = Theme.S(20);
            _name.Font = Theme.Bold;
            _name.LinkBehavior = LinkBehavior.HoverUnderline;
            _name.Enabled = exists;
            _name.LinkClicked += (s, e) => Theme.Open(m.LocalPath);
            _meta.AutoSize = true;
            _meta.Location = new Point(Theme.S(40), Theme.S(24));
            _meta.Font = Theme.Small;
            _meta.LinkBehavior = LinkBehavior.HoverUnderline;
            var size = PathUtil.FormatSize(m.FileSize) + "   ";
            if (exists)
            {
                _meta.Text = size + "Open   \u00B7   Show in folder";
                _meta.Links.Clear();
                _meta.Links.Add(size.Length, 4, "open");
                _meta.Links.Add(size.Length + 11, 14, "folder");
                _meta.LinkClicked += (s, e) =>
                {
                    if ((string)e.Link.LinkData == "open") Theme.Open(m.LocalPath);
                    else Theme.ShowInFolder(m.LocalPath);
                };
            }
            else
            {
                _meta.Text = size + (m.LocalPath == null ? "" : "(file moved or deleted)");
                _meta.Links.Clear();
            }
            Controls.AddRange(new Control[] { _icon, _name, _meta });
            Resize += (s, e) => _name.Width = Width - Theme.S(40);
            PreferredWidth = Math.Max(TextRenderer.MeasureText(m.FileName, Theme.Bold).Width, TextRenderer.MeasureText(_meta.Text, Theme.Small).Width) + Theme.S(52);
        }

        public void ApplyTheme(Color fill)
        {
            BackColor = _icon.BackColor = _name.BackColor = _meta.BackColor = fill;
            _name.LinkColor = Theme.Text;
            _name.ActiveLinkColor = _meta.LinkColor = _meta.ActiveLinkColor = Theme.Link;
            _name.DisabledLinkColor = Theme.Muted;
            _meta.ForeColor = Theme.Muted;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SHGetFileInfo(string path, uint attrs, ref SHFILEINFO info, uint size, uint flags);

        [DllImport("user32.dll")]
        static extern bool DestroyIcon(IntPtr h);

        /// <summary>The same large icon Explorer shows for this file (or its type).</summary>
        static Bitmap ShellIcon(string path, bool byExtensionOnly)
        {
            var info = new SHFILEINFO();
            uint flags = 0x100 /* SHGFI_ICON | LARGEICON */ | (byExtensionOnly ? 0x10u /* USEFILEATTRIBUTES */ : 0u);
            if (SHGetFileInfo(path, 0x80, ref info, (uint)Marshal.SizeOf(info), flags) == IntPtr.Zero || info.hIcon == IntPtr.Zero)
                return null;
            try
            {
                using (var ic = Icon.FromHandle(info.hIcon)) return ic.ToBitmap();
            }
            finally { DestroyIcon(info.hIcon); }
        }
    }

    /// <summary>Multiline TextBox with a grey placeholder.</summary>
    sealed class CueTextBox : TextBox
    {
        public string Cue;

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == 0x000F /* WM_PAINT */ && TextLength == 0 && !string.IsNullOrEmpty(Cue))
            {
                using (var g = CreateGraphics())
                    TextRenderer.DrawText(g, Cue, Font, new Rectangle(1, 1, ClientSize.Width - 2, ClientSize.Height - 2),
                        Theme.Muted, BackColor, TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
            }
        }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            Invalidate();
        }
    }
}
