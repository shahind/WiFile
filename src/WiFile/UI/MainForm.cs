using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
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
        readonly Panel _left = new Panel(), _bar = new Panel(), _status = new Panel(), _banner = new Panel();
        readonly FlatButton _back, _forward, _up, _refresh, _newFolder, _paste, _view, _openExplorer, _more, _fix, _dismiss;
        readonly Breadcrumb _crumbs = new Breadcrumb();
        readonly Label _sync = new Label(), _peers = new Label(), _bannerText = new Label();
        readonly LinkLabel _folder = new LinkLabel();
        readonly SplitContainer _split;
        readonly NotifyIcon _tray;
        readonly ContextMenuStrip _viewMenu = ThemedRenderer.Menu(), _moreMenu = ThemedRenderer.Menu(), _trayMenu = ThemedRenderer.Menu();
        readonly ToolStripMenuItem _autoStart;
        bool _startHidden, _exiting, _bannerDismissed;
        SyncStatus _lastStatus;

        public MainForm(WiFileNode node, bool startHidden)
        {
            _node = node;
            _startHidden = startHidden;
            Text = "WiFile";
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            Font = Theme.Base;
            StartPosition = FormStartPosition.CenterScreen;
            Size = Theme.S(1240, 780);
            MinimumSize = Theme.S(820, 500);

            // ---------------- top bar (left pane)
            _bar.Dock = DockStyle.Top;
            _bar.Height = Theme.S(50);
            _back = new FlatButton("\uE72B", null, "Back (Alt+Left)");
            _forward = new FlatButton("\uE72A", null, "Forward (Alt+Right)");
            _up = new FlatButton("\uE74A", null, "Up (Alt+Up)");
            _refresh = new FlatButton("\uE72C", null, "Refresh (F5)");
            _newFolder = new FlatButton("\uE8F4", "New folder", "New folder (Ctrl+Shift+N)");
            _paste = new FlatButton("\uE77F", null, "Paste (Ctrl+V) \u2014 files, or an image/text from the clipboard");
            _view = new FlatButton("\uE8A9", "View", "Change how files are shown") { ShowChevron = true };
            _view.AutoFit();
            _openExplorer = new FlatButton("\uE8A7", null, "Open this folder in File Explorer");
            _more = new FlatButton("\uE712", null, "More");
            _bar.Controls.AddRange(new Control[] { _back, _forward, _up, _refresh, _crumbs, _newFolder, _paste, _view, _openExplorer, _more });
            _bar.Resize += (s, e) => LayoutBar();

            _explorer = new ExplorerHost(_node.Config.SharedDir) { Dock = DockStyle.Fill };
            _explorer.ViewMode = Enum.TryParse(_node.Settings.ViewMode, out ViewMode vm) ? vm : ViewMode.Details;
            _explorer.Navigated += (s, e) => UpdateNav();
            _crumbs.Navigate += p => _explorer.NavigateTo(p);
            _back.Click += (s, e) => _explorer.GoBack();
            _forward.Click += (s, e) => _explorer.GoForward();
            _up.Click += (s, e) => _explorer.GoUp();
            _refresh.Click += (s, e) => { _explorer.RefreshView(); _node.Sync.RequestScan(); };
            _newFolder.Click += (s, e) => _explorer.NewFolder();
            _paste.Click += (s, e) => _explorer.Paste();
            _view.Click += (s, e) => { BuildViewMenu(); _viewMenu.Show(_view, new Point(0, _view.Height)); };
            _openExplorer.Click += (s, e) => OpenDir(_explorer.CurrentPath);
            _more.Click += (s, e) => { ThemedRenderer.Style(_moreMenu); _moreMenu.Show(_more, new Point(_more.Width - _moreMenu.Width, _more.Height)); };

            _autoStart = new ToolStripMenuItem("Start WiFile when I sign in") { Checked = IsAutoStart(), CheckOnClick = true };
            _autoStart.Click += (s, e) => SetAutoStart(_autoStart.Checked);
            _moreMenu.Items.Add(_autoStart);
            _moreMenu.Items.Add("Change my name\u2026", null, (s, e) => _chat.Rename());
            _moreMenu.Items.Add("Open received chat files", null, (s, e) => OpenDir(_node.Config.ReceiveDir));
            _moreMenu.Items.Add("Open log folder", null, (s, e) => OpenDir(_node.Config.DataDir));
            _moreMenu.Items.Add("Allow WiFile in Windows Firewall\u2026", null, (s, e) => FixFirewall());
            _moreMenu.Items.Add(new ToolStripSeparator());
            _moreMenu.Items.Add("About WiFile", null, (s, e) => About());
            _moreMenu.Items.Add("Exit WiFile", null, (s, e) => ExitApp());

            _left.Dock = DockStyle.Fill;
            _left.Controls.Add(_explorer);
            _left.Controls.Add(_bar);

            // ---------------- right: chat
            _chat = new ChatPanel(_node) { Dock = DockStyle.Fill };
            _chat.Incoming += OnIncoming;

            _split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel2, SplitterWidth = Theme.S(1) + 1 };
            _split.Panel1.Controls.Add(_left);
            _split.Panel2.Controls.Add(_chat);

            // ---------------- firewall banner
            _banner.Dock = DockStyle.Top;
            _banner.Height = Theme.S(44);
            _banner.Visible = false;
            _bannerText.AutoSize = false;
            _bannerText.TextAlign = ContentAlignment.MiddleLeft;
            _bannerText.Text = "\u26A0  Windows Firewall is blocking other PCs from connecting to this one. WiFile works around it, but allowing it is faster and more reliable.";
            _fix = new FlatButton("\uE83D", "Allow", "Add a Windows Firewall rule for WiFile (asks for administrator permission)") { Accent = true };
            _fix.Click += (s, e) => FixFirewall();
            _dismiss = new FlatButton("\uE711", null, "Hide");
            _dismiss.Click += (s, e) => { _bannerDismissed = true; _banner.Visible = false; };
            _banner.Controls.AddRange(new Control[] { _bannerText, _fix, _dismiss });
            _banner.Resize += (s, e) =>
            {
                int m = Theme.S(8);
                _dismiss.Location = new Point(_banner.Width - m - _dismiss.Width, (_banner.Height - _dismiss.Height) / 2);
                _fix.Location = new Point(_dismiss.Left - Theme.S(6) - _fix.Width, (_banner.Height - _fix.Height) / 2);
                _bannerText.SetBounds(Theme.S(14), 0, _fix.Left - Theme.S(24), _banner.Height);
            };

            // ---------------- status bar
            _status.Dock = DockStyle.Bottom;
            _status.Height = Theme.S(30);
            _sync.AutoSize = false;
            _sync.AutoEllipsis = true;
            _sync.TextAlign = ContentAlignment.MiddleLeft;
            _peers.AutoSize = true;
            _folder.AutoSize = true;
            _folder.Text = _node.Config.SharedDir;
            _folder.LinkBehavior = LinkBehavior.HoverUnderline;
            _folder.LinkClicked += (s, e) => OpenDir(_node.Config.SharedDir);
            new ToolTip().SetToolTip(_folder, "Your copy of the shared folder \u2014 click to open in File Explorer");
            _status.Controls.AddRange(new Control[] { _sync, _peers, _folder });
            _status.Resize += (s, e) => LayoutStatus();
            _status.Paint += (s, e) => { using (var p = new Pen(Theme.Border)) e.Graphics.DrawLine(p, 0, 0, _status.Width, 0); };

            Controls.Add(_split);
            Controls.Add(_banner);
            Controls.Add(_status);

            // ---------------- tray
            _trayMenu.Items.Add("Open WiFile", null, (s, e) => ShowFromTray());
            _trayMenu.Items.Add("Open shared folder", null, (s, e) => OpenDir(_node.Config.SharedDir));
            _trayMenu.Items.Add(new ToolStripSeparator());
            _trayMenu.Items.Add("Exit", null, (s, e) => ExitApp());
            _tray = new NotifyIcon { Icon = Icon, Text = "WiFile", ContextMenuStrip = _trayMenu, Visible = true };
            _tray.DoubleClick += (s, e) => ShowFromTray();
            _tray.BalloonTipClicked += (s, e) => ShowFromTray();

            // ---------------- engine events
            _node.Sync.StatusChanged += st => UI(() => ShowStatus(st));
            _node.Sync.ItemChanged += (path, isDir, removed) => Native.NotifyShell(path, isDir, removed);
            _node.Discovery.PeersChanged += () => UI(UpdatePeers);
            _node.Connector.InboundBlocked += () => UI(() =>
            {
                if (!_bannerDismissed && !_banner.Visible) { _banner.Visible = true; ApplyTheme(); }
            });
            Theme.Changed += () => UI(() => { ApplyTheme(); _explorer.Rebuild(); });

            Load += (s, e) =>
            {
                _split.SplitterDistance = Math.Max(Theme.S(360), _split.Width - Theme.S(420));
                UpdatePeers();
                UpdateNav();
            };
            HandleCreated += (s, e) => Theme.ApplyTitleBar(this);
            Shown += (s, e) => _chat.FocusInput();
            ApplyTheme();
            ShowStatus(_node.Sync.CurrentStatus);
            FirstRunSetup();
            var _ = Handle; // create the handle now so background events can marshal to the UI thread
        }

        void ApplyTheme()
        {
            BackColor = Theme.Window;
            _left.BackColor = _explorer.BackColor = Theme.Surface;
            _bar.BackColor = _crumbs.BackColor = Theme.Toolbar;
            _split.BackColor = Theme.Border;
            _split.Panel1.BackColor = _split.Panel2.BackColor = Theme.Surface;
            _status.BackColor = Theme.Window;
            _folder.LinkColor = _folder.ActiveLinkColor = Theme.Link;
            _banner.BackColor = _bannerText.BackColor = Theme.WarnBack;
            _bannerText.ForeColor = Theme.Dark ? Theme.Text : Theme.WarnText;
            foreach (var m in new[] { _viewMenu, _moreMenu, _trayMenu }) ThemedRenderer.Style(m);
            if (_lastStatus != null) ShowStatus(_lastStatus);
            if (_tray != null) UpdatePeers();
            _chat.ApplyTheme();
            Theme.ApplyTitleBar(this);
            Invalidate(true);
        }

        void LayoutBar()
        {
            int m = Theme.S(6), y = (_bar.Height - _back.Height) / 2, x = m;
            foreach (var b in new[] { _back, _forward, _up, _refresh })
            {
                b.Location = new Point(x, y);
                x += b.Width + Theme.S(2);
            }
            int right = _bar.Width - m;
            foreach (var b in new[] { _more, _openExplorer, _view, _paste, _newFolder })
            {
                right -= b.Width;
                b.Location = new Point(right, y);
                right -= Theme.S(2);
            }
            _crumbs.SetBounds(x + Theme.S(6), y, Math.Max(0, right - x - Theme.S(12)), _back.Height);
        }

        void LayoutStatus()
        {
            int m = Theme.S(12);
            _folder.Location = new Point(_status.Width - m - _folder.Width, (_status.Height - _folder.Height) / 2);
            _peers.Location = new Point(_folder.Left - Theme.S(24) - _peers.Width, (_status.Height - _peers.Height) / 2);
            _sync.SetBounds(m, 0, Math.Max(0, _peers.Left - 2 * m), _status.Height);
        }

        void BuildViewMenu()
        {
            _viewMenu.Items.Clear();
            (ViewMode mode, string text)[] modes =
            {
                (ViewMode.ExtraLargeIcons, "Extra large icons"), (ViewMode.LargeIcons, "Large icons"),
                (ViewMode.MediumIcons, "Medium icons"), (ViewMode.SmallIcons, "Small icons"),
                (ViewMode.List, "List"), (ViewMode.Details, "Details"), (ViewMode.Tiles, "Tiles"), (ViewMode.Content, "Content"),
            };
            foreach (var (mode, text) in modes)
            {
                var item = new ToolStripMenuItem(text) { Checked = _explorer.ViewMode == mode };
                item.Click += (s, e) =>
                {
                    _explorer.ViewMode = mode;
                    _node.Settings.ViewMode = mode.ToString();
                    _node.Settings.Save();
                };
                _viewMenu.Items.Add(item);
            }
            ThemedRenderer.Style(_viewMenu);
        }

        void UI(Action a)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(a); } catch (InvalidOperationException) { }
        }

        void ShowStatus(SyncStatus st)
        {
            _lastStatus = st;
            _sync.Text = (st.Busy ? "\u21BB  " : "\u2713  ") + st.Text;
            _sync.ForeColor = st.Busy ? Theme.Accent : Theme.Online;
        }

        void UpdateNav()
        {
            _up.Enabled = !_explorer.AtRoot;
            _crumbs.SetPath(_node.Config.SharedDir, _explorer.CurrentPath);
        }

        void UpdatePeers()
        {
            int n = _node.Discovery.Peers.Count;
            _peers.Text = n == 0 ? "No other devices online" : $"\u25CF  {n} device{(n == 1 ? "" : "s")} online";
            _peers.ForeColor = n == 0 ? Theme.Muted : Theme.Online;
            _tray.Text = "WiFile \u2014 " + (n == 0 ? "no other devices online" : $"{n} device{(n == 1 ? "" : "s")} online");
            LayoutStatus();
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

        /// <summary>Replace any "block" rules Windows created for WiFile with an allow rule (UAC prompt).</summary>
        void FixFirewall()
        {
            var exe = Application.ExecutablePath;
            var cmd = "/c netsh advfirewall firewall delete rule name=\"wifile.exe\" & " +
                      "netsh advfirewall firewall delete rule name=\"WiFile\" & " +
                      $"netsh advfirewall firewall add rule name=\"WiFile\" dir=in action=allow program=\"{exe}\" enable=yes profile=any";
            try
            {
                var p = Process.Start(new ProcessStartInfo("cmd.exe", cmd) { Verb = "runas", UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden });
                p?.WaitForExit(15000);
                if (p != null && p.ExitCode == 0)
                {
                    _banner.Visible = false;
                    _bannerDismissed = true;
                    _tray.ShowBalloonTip(3000, "WiFile", "Windows Firewall now allows WiFile.", ToolTipIcon.Info);
                }
            }
            catch (System.ComponentModel.Win32Exception) { /* user cancelled the UAC prompt */ }
            catch (Exception ex) { Log.Error("firewall fix", ex); }
        }

        void OnIncoming(ChatMessage m)
        {
            if (Visible && ContainsFocus && WindowState != FormWindowState.Minimized) return;
            var text = m.IsFile ? "sent you " + m.FileName : m.Text;
            if (text.Length > 120) text = text.Substring(0, 117) + "\u2026";
            _tray.ShowBalloonTip(4000, m.FromName, text, ToolTipIcon.None);
            if (Visible) FlashWindow();
        }

        void About()
        {
            MessageBox.Show(this,
                "WiFile " + Application.ProductVersion + "\n\n" +
                "Share files and chat with every PC on the same Wi-Fi \u2014 no internet, accounts or setup.\n\n" +
                "Shared folder (your synced copy):\n" + _node.Config.SharedDir + "\n\n" +
                "Files received in chat:\n" + _node.Config.ReceiveDir + "\n\n" +
                "Device name: " + _node.Name + "\n\nhttps://github.com/shahind/WiFile",
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
                case Keys.Alt | Keys.Right: _explorer.GoForward(); return true;
                case Keys.Alt | Keys.Up: _explorer.GoUp(); return true;
                case Keys.F5: _explorer.RefreshView(); _node.Sync.RequestScan(); return true;
                case Keys.Control | Keys.Shift | Keys.N: _explorer.NewFolder(); return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }
}
