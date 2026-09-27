using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;
using WiFile.Core;

namespace WiFile.UI
{
    sealed class MainForm : Form
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        readonly WiFileNode _node;
        readonly ExplorerHost _explorer;
        readonly ChatPanel _chat;
        readonly ToolStripButton _back, _up;
        readonly ToolStripLabel _path;
        readonly ToolStripStatusLabel _sync, _peers, _folder;
        readonly NotifyIcon _tray;
        readonly ToolStripMenuItem _autoStart;
        bool _startHidden, _exiting;

        public MainForm(WiFileNode node, bool startHidden)
        {
            _node = node;
            _startHidden = startHidden;
            Text = "WiFile";
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            Font = Theme.Base;
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(1240, 760);
            MinimumSize = new Size(760, 480);
            BackColor = Color.White;

            // ---------------- left: shared folder (real Windows Explorer view)
            var left = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
            var bar = new ToolStrip
            {
                GripStyle = ToolStripGripStyle.Hidden, RenderMode = ToolStripRenderMode.System, BackColor = Color.White,
                Padding = new Padding(6, 4, 6, 4), AutoSize = false, Height = 40, CanOverflow = false,
            };
            bar.Renderer = new FlatRenderer();
            _back = Glyph("", "Back (Alt+Left)");
            _up = Glyph("", "Up one level (Alt+Up)");
            var home = Glyph("", "Shared folder home");
            var refresh = Glyph("", "Refresh (F5)");
            _path = new ToolStripLabel { Font = Theme.Bold, Margin = new Padding(8, 0, 0, 0) };
            var newFolder = new ToolStripButton("New folder") { Margin = new Padding(2, 0, 2, 0) };
            var openExplorer = new ToolStripButton("Open in Explorer") { Margin = new Padding(2, 0, 2, 0) };
            var more = new ToolStripDropDownButton("") { Font = Theme.Icons, ShowDropDownArrow = false, ToolTipText = "More" };
            newFolder.Alignment = openExplorer.Alignment = more.Alignment = ToolStripItemAlignment.Right;
            bar.Items.AddRange(new ToolStripItem[] { _back, _up, home, refresh, _path, more, openExplorer, newFolder });

            _autoStart = new ToolStripMenuItem("Start WiFile when I sign in") { Checked = IsAutoStart(), CheckOnClick = true };
            _autoStart.Click += (s, e) => SetAutoStart(_autoStart.Checked);
            more.DropDownItems.Add(_autoStart);
            more.DropDownItems.Add("Open received chat files", null, (s, e) => OpenDir(_node.Config.ReceiveDir));
            more.DropDownItems.Add("Open log folder", null, (s, e) => OpenDir(_node.Config.DataDir));
            more.DropDownItems.Add(new ToolStripSeparator());
            more.DropDownItems.Add("About WiFile", null, (s, e) => About());
            more.DropDownItems.Add("Exit WiFile", null, (s, e) => ExitApp());
            foreach (ToolStripItem i in more.DropDownItems) i.Font = Theme.Base;

            _explorer = new ExplorerHost(_node.Config.SharedDir) { Dock = DockStyle.Fill };
            _explorer.Navigated += (s, e) => UpdateNav();
            _back.Click += (s, e) => _explorer.GoBack();
            _up.Click += (s, e) => _explorer.GoUp();
            home.Click += (s, e) => _explorer.GoHome();
            refresh.Click += (s, e) => { _explorer.RefreshView(); _node.Sync.RequestScan(); };
            newFolder.Click += (s, e) => NewFolder();
            openExplorer.Click += (s, e) => OpenDir(_explorer.CurrentPath);

            var line = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Theme.Border };
            left.Controls.Add(_explorer);
            left.Controls.Add(line);
            left.Controls.Add(bar);

            // ---------------- right: chat
            _chat = new ChatPanel(_node) { Dock = DockStyle.Fill };
            _chat.Incoming += OnIncoming;

            var split = new SplitContainer
            {
                Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel2, SplitterWidth = 5, BackColor = Theme.Border,
            };
            split.Panel1.BackColor = Color.White;
            split.Panel2.BackColor = Color.White;
            split.Panel1.Controls.Add(left);
            split.Panel2.Controls.Add(_chat);

            // ---------------- status bar
            var status = new StatusStrip { SizingGrip = true, BackColor = Color.FromArgb(248, 248, 248) };
            _sync = new ToolStripStatusLabel("Starting…") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
            _peers = new ToolStripStatusLabel();
            _folder = new ToolStripStatusLabel(_node.Config.SharedDir) { IsLink = true, LinkColor = Theme.Accent, ToolTipText = "Your copy of the shared folder — click to open" };
            _folder.Click += (s, e) => OpenDir(_node.Config.SharedDir);
            status.Items.AddRange(new ToolStripItem[] { _sync, _peers, new ToolStripStatusLabel("│") { ForeColor = Theme.Border }, _folder });

            Controls.Add(split);
            Controls.Add(status);

            // ---------------- tray
            var trayMenu = new ContextMenuStrip();
            trayMenu.Items.Add("Open WiFile", null, (s, e) => ShowFromTray());
            trayMenu.Items.Add("Open shared folder", null, (s, e) => OpenDir(_node.Config.SharedDir));
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add("Exit", null, (s, e) => ExitApp());
            _tray = new NotifyIcon { Icon = Icon, Text = "WiFile", ContextMenuStrip = trayMenu, Visible = true };
            _tray.DoubleClick += (s, e) => ShowFromTray();
            _tray.BalloonTipClicked += (s, e) => ShowFromTray();

            // ---------------- engine events
            _node.Sync.StatusChanged += st => UI(() => ShowStatus(st));
            ShowStatus(_node.Sync.CurrentStatus);
            _node.Discovery.PeersChanged += () => UI(UpdatePeers);

            Load += (s, e) =>
            {
                split.SplitterDistance = Math.Max(300, split.Width - 430);
                UpdatePeers();
                UpdateNav();
            };
            Shown += (s, e) => _chat.FocusInput();
            FirstRunSetup();
            var _ = Handle; // create the handle now so background events can marshal to the UI thread
        }

        void ShowStatus(SyncStatus st)
        {
            _sync.Text = (st.Busy ? "⟳  " : "✓  ") + st.Text;
            _sync.ForeColor = st.Busy ? Theme.Accent : Theme.Online;
        }

        static ToolStripButton Glyph(string glyph, string tip) =>
            new ToolStripButton(glyph) { Font = Theme.Icons, ToolTipText = tip, AutoSize = false, Width = 34, Height = 30 };

        void UI(Action a)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(a); } catch (InvalidOperationException) { }
        }

        void UpdateNav()
        {
            _up.Enabled = !_explorer.AtRoot;
            var root = _node.Config.SharedDir.TrimEnd('\\');
            var cur = _explorer.CurrentPath ?? root;
            var rel = cur.Length > root.Length ? cur.Substring(root.Length).Trim('\\') : "";
            _path.Text = "Shared" + (rel.Length == 0 ? "" : "  ›  " + string.Join("  ›  ", rel.Split('\\')));
        }

        void UpdatePeers()
        {
            int n = _node.Discovery.Peers.Count;
            _peers.Text = n == 0 ? "No other devices online" : $"{n} device{(n == 1 ? "" : "s")} online";
            _tray.Text = "WiFile — " + _peers.Text;
        }

        void NewFolder()
        {
            var dir = _explorer.CurrentPath ?? _node.Config.SharedDir;
            var path = PathUtil.UniquePath(dir, "New folder");
            try { Directory.CreateDirectory(path); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "WiFile"); return; }
            // The view picks the folder up asynchronously; start rename once it's there.
            int tries = 0;
            var t = new Timer { Interval = 150 };
            t.Tick += (s, e) =>
            {
                if (_explorer.SelectAndRename(path) || ++tries > 20)
                {
                    t.Stop();
                    t.Dispose();
                }
            };
            t.Start();
        }

        static void OpenDir(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                Process.Start("explorer.exe", "\"" + dir + "\"");
            }
            catch { }
        }

        void OnIncoming(ChatMessage m)
        {
            if (Visible && ContainsFocus && WindowState != FormWindowState.Minimized) return;
            var text = m.IsFile ? "sent you " + m.FileName : m.Text;
            if (text.Length > 120) text = text.Substring(0, 117) + "…";
            _tray.ShowBalloonTip(4000, m.FromName, text, ToolTipIcon.None);
            if (Visible) FlashWindow();
        }

        void About()
        {
            MessageBox.Show(this,
                "WiFile " + Application.ProductVersion + "\n\n" +
                "Share files and chat with every PC on the same Wi-Fi — no internet, accounts or setup.\n\n" +
                "Shared folder (your synced copy):\n" + _node.Config.SharedDir + "\n\n" +
                "Files received in chat:\n" + _node.Config.ReceiveDir + "\n\n" +
                "Device name: " + _node.Name,
                "About WiFile", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // ---------------- tray / lifetime

        protected override void SetVisibleCore(bool value)
        {
            if (_startHidden)
            {
                _startHidden = false;
                value = false;
                if (!IsHandleCreated) CreateHandle();
            }
            base.SetVisibleCore(value);
        }

        public void ShowFromTray()
        {
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_exiting && e.CloseReason == CloseReason.UserClosing)
            {
                // Keep syncing in the background.
                e.Cancel = true;
                Hide();
                if (!_node.Settings.TrayHintShown)
                {
                    _node.Settings.TrayHintShown = true;
                    _node.Settings.Save();
                    _tray.ShowBalloonTip(5000, "WiFile is still running",
                        "Files keep syncing in the background. Right-click the tray icon and choose Exit to quit.", ToolTipIcon.Info);
                }
                return;
            }
            base.OnFormClosing(e);
        }

        void ExitApp()
        {
            _exiting = true;
            _tray.Visible = false;
            Close();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _tray?.Dispose();
            base.Dispose(disposing);
        }

        // ---------------- autostart (per user, no admin needed)

        void FirstRunSetup()
        {
            if (_node.Settings.AutoStartConfigured) return;
            _node.Settings.AutoStartConfigured = true;
            _node.Settings.Save();
            if (Program.Profile == null) SetAutoStart(true);
            _autoStart.Checked = IsAutoStart();
        }

        static bool IsAutoStart()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return k?.GetValue("WiFile") != null;
            }
            catch { return false; }
        }

        static void SetAutoStart(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (on) k.SetValue("WiFile", "\"" + Application.ExecutablePath + "\" --minimized");
                    else k.DeleteValue("WiFile", false);
                }
            }
            catch (Exception ex) { Log.Error("autostart", ex); }
        }

        public static void RemoveAutoStart() => SetAutoStart(false);

        // ---------------- misc

        [StructLayout(LayoutKind.Sequential)]
        struct FLASHWINFO { public uint cbSize; public IntPtr hwnd; public uint dwFlags; public uint uCount; public uint dwTimeout; }

        [DllImport("user32.dll")]
        static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

        void FlashWindow()
        {
            var fi = new FLASHWINFO { hwnd = Handle, dwFlags = 3 | 12 /* ALL | TIMERNOFG */, uCount = 3 };
            fi.cbSize = (uint)Marshal.SizeOf(fi);
            FlashWindowEx(ref fi);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Alt | Keys.Left: _explorer.GoBack(); return true;
                case Keys.Alt | Keys.Up: _explorer.GoUp(); return true;
                case Keys.F5: _explorer.RefreshView(); _node.Sync.RequestScan(); return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        sealed class FlatRenderer : ToolStripSystemRenderer
        {
            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) { }
        }
    }
}
