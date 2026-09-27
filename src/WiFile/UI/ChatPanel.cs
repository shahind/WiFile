using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    static class Theme
    {
        public static readonly Font Base = new Font("Segoe UI", 9.75f);
        public static readonly Font Small = new Font("Segoe UI", 8.25f);
        public static readonly Font Bold = new Font("Segoe UI Semibold", 9.75f);
        public static readonly Font Title = new Font("Segoe UI Semibold", 12f);
        public static readonly Font Icons = new Font("Segoe MDL2 Assets", 11f);
        public static readonly Color Accent = Color.FromArgb(0, 103, 192);
        public static readonly Color Mine = Color.FromArgb(214, 234, 255);
        public static readonly Color Theirs = Color.FromArgb(240, 240, 240);
        public static readonly Color Muted = Color.FromArgb(110, 110, 110);
        public static readonly Color Online = Color.FromArgb(16, 137, 62);
        public static readonly Color Border = Color.FromArgb(225, 225, 225);

        static readonly Color[] NameColors =
        {
            Color.FromArgb(0, 99, 177), Color.FromArgb(135, 100, 184), Color.FromArgb(202, 80, 16),
            Color.FromArgb(16, 124, 16), Color.FromArgb(194, 57, 179), Color.FromArgb(3, 131, 135), Color.FromArgb(142, 86, 46),
        };

        public static Color ForName(string name)
        {
            int h = 17;
            foreach (var c in name ?? "") h = h * 31 + c;
            return NameColors[(h & 0x7fffffff) % NameColors.Length];
        }

        public static GraphicsPath Rounded(Rectangle r, int radius)
        {
            var p = new GraphicsPath();
            int d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void Open(string target)
        {
            try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show(ex.Message, "WiFile", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        public static void ShowInFolder(string file)
        {
            try { Process.Start("explorer.exe", "/select,\"" + file + "\""); } catch { }
        }
    }

    /// <summary>Right-hand chat: online devices, message list, composer.</summary>
    sealed class ChatPanel : UserControl
    {
        const int MaxRows = 300;
        readonly WiFileNode _node;
        readonly MessageList _list = new MessageList();
        readonly Label _online = new Label();
        readonly LinkLabel _me = new LinkLabel();
        readonly ComboBox _to = new ComboBox();
        readonly CueTextBox _input = new CueTextBox();
        readonly Dictionary<string, MessageRow> _rows = new Dictionary<string, MessageRow>();
        readonly ToolTip _tip = new ToolTip();

        public event Action<ChatMessage> Incoming;

        public ChatPanel(WiFileNode node)
        {
            _node = node;
            BackColor = Color.White;
            Font = Theme.Base;
            AllowDrop = true;

            // ---- header
            var header = new Panel { Dock = DockStyle.Top, Height = 58, Padding = new Padding(12, 8, 12, 6) };
            header.Paint += (s, e) => e.Graphics.DrawLine(new Pen(Theme.Border), 0, header.Height - 1, header.Width, header.Height - 1);
            var title = new Label { Text = "Chat", Font = Theme.Title, AutoSize = true, Location = new Point(10, 6) };
            _online.AutoSize = false;
            _online.AutoEllipsis = true;
            _online.ForeColor = Theme.Muted;
            _online.Location = new Point(12, 34);
            _online.Height = 18;
            _online.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
            _me.AutoSize = true;
            _me.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _me.LinkColor = Theme.Accent;
            _me.ActiveLinkColor = Theme.Accent;
            _me.LinkBehavior = LinkBehavior.HoverUnderline;
            _me.LinkClicked += (s, e) => Rename();
            _tip.SetToolTip(_me, "Click to change the name others see");
            header.Controls.AddRange(new Control[] { title, _online, _me });
            header.Resize += (s, e) =>
            {
                _me.Location = new Point(header.Width - _me.Width - 12, 12);
                _online.Width = header.Width - 24;
            };

            // ---- composer
            var composer = new Panel { Dock = DockStyle.Bottom, Height = 104, Padding = new Padding(10, 6, 10, 10) };
            composer.Paint += (s, e) => e.Graphics.DrawLine(new Pen(Theme.Border), 0, 0, composer.Width, 0);
            var toLabel = new Label { Text = "To:", AutoSize = true, ForeColor = Theme.Muted, Location = new Point(10, 12) };
            _to.DropDownStyle = ComboBoxStyle.DropDownList;
            _to.Location = new Point(38, 8);
            _to.Width = 190;
            _to.DisplayMember = "Text";
            var attach = IconButton("", "Send a file (or drag files here, or paste a screenshot)");
            attach.Click += (s, e) => PickFiles();
            var send = IconButton("", "Send (Enter)");
            send.BackColor = Theme.Accent;
            send.ForeColor = Color.White;
            send.FlatAppearance.MouseOverBackColor = Color.FromArgb(0, 84, 158);
            send.Click += (s, e) => SendText();
            _input.Multiline = true;
            _input.Cue = "Type a message…  (Enter to send, Shift+Enter for a new line)";
            _input.BorderStyle = BorderStyle.FixedSingle;
            _input.ScrollBars = ScrollBars.None;
            _input.Font = Theme.Base;
            _input.KeyDown += InputKeyDown;
            composer.Controls.AddRange(new Control[] { toLabel, _to, _input, attach, send });
            composer.Resize += (s, e) =>
            {
                int w = composer.ClientSize.Width;
                send.Location = new Point(w - 10 - send.Width, 40);
                attach.Location = new Point(send.Left - 6 - attach.Width, 40);
                _input.SetBounds(10, 40, attach.Left - 16, 52);
            };

            // ---- messages
            _list.Dock = DockStyle.Fill;
            _list.WidthChanged += w => { foreach (var r in _rows.Values) r.LayoutFor(w); };

            Controls.Add(_list);
            Controls.Add(composer);
            Controls.Add(header);

            DragEnter += (s, e) => e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            DragDrop += (s, e) => SendPaths(e.Data.GetData(DataFormats.FileDrop) as string[]);
            foreach (Control c in new Control[] { _list, _input })
            {
                c.AllowDrop = true;
                c.DragEnter += (s, e) => e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
                c.DragDrop += (s, e) => SendPaths(e.Data.GetData(DataFormats.FileDrop) as string[]);
            }

            _node.Chat.MessageReceived += m => UI(() => { Add(m); Incoming?.Invoke(m); });
            _node.Chat.MessageUpdated += m => UI(() => { if (_rows.TryGetValue(m.Id, out var r)) r.UpdateStatus(); });
            _node.Discovery.PeersChanged += () => UI(RefreshPeers);

            RefreshMe();
            RefreshPeers();
            foreach (var m in _node.Chat.LoadHistory()) Add(m, false);
            _list.ScrollToEnd();
        }

        static Button IconButton(string glyph, string tip)
        {
            var b = new Button
            {
                Text = glyph, Font = Theme.Icons, Size = new Size(44, 52), FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(243, 243, 243), Cursor = Cursors.Hand, TabStop = false,
            };
            b.FlatAppearance.BorderSize = 0;
            new ToolTip().SetToolTip(b, tip);
            return b;
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
            BeginInvoke(new Action(RefreshPeers));
        }

        public void FocusInput() => _input.Focus();

        void RefreshMe()
        {
            _me.Text = "You: " + _node.Name + "  ✎";
            _me.Location = new Point(_me.Parent?.Width - _me.Width - 12 ?? 0, 12);
        }

        void RefreshPeers()
        {
            var peers = _node.Discovery.Peers;
            _online.ForeColor = peers.Count > 0 ? Theme.Online : Theme.Muted;
            _online.Text = peers.Count == 0
                ? "○  No other devices found yet — open WiFile on another PC on this Wi-Fi"
                : $"●  {peers.Count} device{(peers.Count == 1 ? "" : "s")} online: " + string.Join(", ", peers.Select(p => p.Name));

            var selected = (_to.SelectedItem as Recipient)?.Id;
            _to.BeginUpdate();
            _to.Items.Clear();
            _to.Items.Add(new Recipient { Id = null, Text = "Everyone" });
            foreach (var p in peers) _to.Items.Add(new Recipient { Id = p.Id, Text = p.Name });
            _to.SelectedItem = _to.Items.Cast<Recipient>().FirstOrDefault(r => r.Id == selected) ?? _to.Items[0];
            _to.EndUpdate();
        }

        sealed class Recipient
        {
            public string Id;
            public string Text { get; set; }
        }

        string ToId => (_to.SelectedItem as Recipient)?.Id;

        void Rename()
        {
            var name = Microsoft.VisualBasic.Interaction.InputBox("Your name, as other devices will see it:", "WiFile", _node.Name);
            if (string.IsNullOrWhiteSpace(name)) return;
            _node.Rename(name);
            RefreshMe();
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
            Add(_node.Chat.SendText(text, ToId));
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
                    if (File.Exists(file)) Add(_node.Chat.SendFile(file, ToId));
                }
                catch (Exception ex)
                {
                    Cursor = Cursors.Default;
                    MessageBox.Show(this, "Could not send " + Path.GetFileName(p) + ":\n" + ex.Message, "WiFile", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        void Add(ChatMessage m, bool scroll = true)
        {
            if (_rows.ContainsKey(m.Id)) return;
            var row = new MessageRow(m);
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
        const int Gap = 2;
        public event Action<int> WidthChanged;
        int _lastWidth = -1;

        public MessageList()
        {
            AutoScroll = true;
            BackColor = Color.White;
            DoubleBuffered = true;
            Padding = new Padding(0, 8, 0, 8);
        }

        int RowWidth => ClientSize.Width;

        public void AddRow(MessageRow row)
        {
            SuspendLayout();
            int y = Controls.Count == 0 ? Padding.Top : Controls[Controls.Count - 1].Bottom + Gap;
            row.Location = new Point(0, y + AutoScrollPosition.Y);
            row.LayoutFor(RowWidth);
            row.HeightChanged += Restack;
            Controls.Add(row);
            ResumeLayout();
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

        bool _scrollPending;

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
            WidthChanged?.Invoke(RowWidth);
            Restack();
            ResumeLayout();
        }

        // Keep the scroll position when a child gets focus (e.g. a text bubble is clicked).
        protected override Point ScrollToControl(Control activeControl) => DisplayRectangle.Location;
    }

    /// <summary>One chat bubble: header (sender · time · status) + text / image preview / file card.</summary>
    sealed class MessageRow : Control
    {
        const int Pad = 9, Side = 12;
        static readonly string[] ImageExt = { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tif", ".tiff", ".ico" };

        public ChatMessage Message { get; }
        public event Action HeightChanged;

        readonly Label _header = new Label { AutoSize = true, Font = Theme.Small, ForeColor = Theme.Muted };
        readonly RichTextBox _text;
        readonly PictureBox _image;
        readonly Panel _file;
        readonly Color _fill;
        readonly bool _mine;
        Rectangle _bubble;
        int _width;

        public MessageRow(ChatMessage m)
        {
            Message = m;
            _mine = m.Outgoing;
            _fill = _mine ? Theme.Mine : Theme.Theirs;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            BackColor = Color.White;
            _header.BackColor = _fill;
            Controls.Add(_header);
            UpdateHeaderText();

            bool localFile = m.LocalPath != null && File.Exists(m.LocalPath);
            if (m.IsFile && localFile && ImageExt.Contains(Path.GetExtension(m.FileName).ToLowerInvariant()) && TryThumb(m.LocalPath, out var thumb))
            {
                _image = new PictureBox { Image = thumb, Size = thumb.Size, Cursor = Cursors.Hand, BackColor = _fill };
                _image.Click += (s, e) => Theme.Open(m.LocalPath);
                new ToolTip().SetToolTip(_image, m.FileName + " — click to open");
                Controls.Add(_image);
            }
            if (m.IsFile)
            {
                _file = BuildFileCard(m, localFile);
                Controls.Add(_file);
            }
            if (!string.IsNullOrEmpty(m.Text))
            {
                _text = new RichTextBox
                {
                    Text = m.Text, ReadOnly = true, BorderStyle = BorderStyle.None, ScrollBars = RichTextBoxScrollBars.None,
                    DetectUrls = true, BackColor = _fill, Font = Theme.Base, TabStop = false, WordWrap = true,
                    Cursor = Cursors.IBeam, ShortcutsEnabled = true,
                };
                _text.LinkClicked += (s, e) => Theme.Open(e.LinkText);
                _text.ContentsResized += (s, e) =>
                {
                    int h = e.NewRectangle.Height + 2;
                    if (_text.Height != h) { _text.Height = h; Relayout(); }
                };
                Controls.Add(_text);
            }
        }

        Panel BuildFileCard(ChatMessage m, bool exists)
        {
            var card = new Panel { Height = 44, BackColor = _fill };
            var icon = new PictureBox { Size = new Size(32, 32), Location = new Point(0, 6), SizeMode = PictureBoxSizeMode.CenterImage, BackColor = _fill };
            try
            {
                icon.Image = ShellIcon(exists ? m.LocalPath : m.FileName, !exists);
            }
            catch { }
            var name = new LinkLabel
            {
                Text = m.FileName, AutoSize = false, AutoEllipsis = true, Location = new Point(38, 4), Height = 20,
                Font = Theme.Bold, LinkBehavior = LinkBehavior.HoverUnderline, LinkColor = Color.FromArgb(30, 30, 30),
                ActiveLinkColor = Theme.Accent, BackColor = _fill, Enabled = exists,
            };
            name.LinkClicked += (s, e) => Theme.Open(m.LocalPath);
            var meta = new LinkLabel
            {
                AutoSize = true, Location = new Point(38, 24), Font = Theme.Small, ForeColor = Theme.Muted,
                LinkColor = Theme.Accent, ActiveLinkColor = Theme.Accent, LinkBehavior = LinkBehavior.HoverUnderline, BackColor = _fill,
            };
            var size = PathUtil.FormatSize(m.FileSize) + "   ";
            if (exists)
            {
                meta.Text = size + "Open   ·   Show in folder";
                meta.Links.Clear();
                meta.Links.Add(size.Length, 4, "open");
                meta.Links.Add(size.Length + 11, 14, "folder");
                meta.LinkClicked += (s, e) =>
                {
                    if ((string)e.Link.LinkData == "open") Theme.Open(m.LocalPath);
                    else Theme.ShowInFolder(m.LocalPath);
                };
            }
            else
            {
                meta.Text = size + (m.LocalPath == null ? "" : "(file moved or deleted)");
                meta.Links.Clear();
            }
            card.Controls.AddRange(new Control[] { icon, name, meta });
            card.Resize += (s, e) => name.Width = card.Width - 38;
            card.Tag = TextRenderer.MeasureText(m.FileName, Theme.Bold).Width + 42;
            return card;
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
            // SHGFI_ICON | SHGFI_LARGEICON (+ SHGFI_USEFILEATTRIBUTES when the file isn't local)
            uint flags = 0x100 | 0x0 | (byExtensionOnly ? 0x10u : 0u);
            if (SHGetFileInfo(path, 0x80 /* FILE_ATTRIBUTE_NORMAL */, ref info, (uint)Marshal.SizeOf(info), flags) == IntPtr.Zero || info.hIcon == IntPtr.Zero)
                return null;
            try
            {
                using (var ic = Icon.FromHandle(info.hIcon)) return ic.ToBitmap();
            }
            finally { DestroyIcon(info.hIcon); }
        }

        static bool TryThumb(string path, out Image thumb)
        {
            thumb = null;
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var img = Image.FromStream(fs, false, false))
                {
                    const int maxW = 260, maxH = 220;
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
            if (m.To != null) who += _mine ? " → " + (m.ToName ?? "private") : " → you (private)";
            _header.Text = who + "  ·  " + when + (string.IsNullOrEmpty(m.Status) ? "" : "  ·  " + m.Status);
            _header.ForeColor = _mine ? Theme.Muted : Theme.ForName(m.FromName);
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
            int maxInner = Math.Max(120, (int)(_width * 0.8) - 2 * Pad - 2 * Side);
            int contentW = _header.PreferredWidth;
            if (_text != null)
            {
                var sz = TextRenderer.MeasureText(_text.Text, _text.Font, new Size(maxInner, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
                contentW = Math.Max(contentW, Math.Min(maxInner, sz.Width + 12));
            }
            if (_image != null) contentW = Math.Max(contentW, _image.Width);
            if (_file != null) contentW = Math.Max(contentW, Math.Min(maxInner, (int)_file.Tag + 110));
            contentW = Math.Min(contentW, maxInner);

            int bubbleW = contentW + 2 * Pad;
            int x = _mine ? _width - Side - bubbleW : Side;
            int y = Pad - 2;
            _header.Location = new Point(x + Pad, y);
            y = _header.Bottom + 3;
            if (_image != null)
            {
                _image.Location = new Point(x + Pad, y);
                y = _image.Bottom + 4;
            }
            if (_file != null)
            {
                _file.SetBounds(x + Pad, y, contentW, 44);
                y = _file.Bottom;
            }
            if (_text != null)
            {
                if (_text.Width != contentW)
                {
                    _text.Width = contentW;
                    SendMessage(_text.Handle, 0x0400 + 65 /* EM_REQUESTRESIZE */, IntPtr.Zero, IntPtr.Zero);
                }
                _text.Location = new Point(x + Pad - 1, y);
                y = _text.Bottom;
            }
            int bubbleH = y + Pad - 2;
            _bubble = new Rectangle(x, 2, bubbleW, bubbleH - 2);
            int h = bubbleH + 4;
            bool heightChanged = Height != h;
            SetBounds(Left, Top, _width, h);
            Invalidate();
            if (heightChanged) HeightChanged?.Invoke();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            if (_bubble.Width <= 0) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Theme.Rounded(_bubble, 10))
            using (var b = new SolidBrush(_fill))
                e.Graphics.FillPath(b, path);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _image?.Image?.Dispose();
            base.Dispose(disposing);
        }

        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wp, IntPtr lp);
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
                    TextRenderer.DrawText(g, Cue, Font, new Rectangle(2, 2, ClientSize.Width - 4, ClientSize.Height - 4),
                        SystemColors.GrayText, TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
            }
        }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            Invalidate();
        }
    }
}
