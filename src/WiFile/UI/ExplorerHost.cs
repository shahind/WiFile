using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using WiFile.Core;

namespace WiFile.UI
{
    public enum ViewMode { ExtraLargeIcons, LargeIcons, MediumIcons, SmallIcons, List, Details, Tiles, Content }

    /// <summary>
    /// Hosts the real Windows Explorer file view (IExplorerBrowser) locked to the shared folder.
    /// Everything Explorer does works natively: drag &amp; drop, copy/cut/paste, rename, delete,
    /// "New" menu, thumbnails, view modes and opening files with their default apps.
    /// </summary>
    public sealed class ExplorerHost : Control, IMessageFilter
    {
        readonly string _root;
        IExplorerBrowser _browser;
        EventSink _sink;
        uint _cookie;
        ViewMode _mode = ViewMode.Details;

        public string CurrentPath { get; private set; }
        public event EventHandler Navigated;

        public ExplorerHost(string root)
        {
            _root = Path.GetFullPath(root).TrimEnd('\\');
            CurrentPath = _root;
            TabStop = true;
            SetStyle(ControlStyles.Selectable, true);
        }

        public bool AtRoot => string.Equals(CurrentPath?.TrimEnd('\\'), _root, StringComparison.OrdinalIgnoreCase);

        public ViewMode ViewMode
        {
            get => _mode;
            set { _mode = value; ApplyViewMode(); }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            BackColor = Theme.Surface;
            Create(_root);
        }

        void Create(string path)
        {
            try
            {
                Native.AllowDarkModeForWindow(Handle, Theme.Dark);
                var type = Type.GetTypeFromCLSID(new Guid("71F96385-DDD6-48D3-A0C1-AE06E8B055FB"));
                _browser = (IExplorerBrowser)Activator.CreateInstance(type);
                var rc = new RECT { Right = ClientSize.Width, Bottom = ClientSize.Height };
                var fs = new FOLDERSETTINGS { ViewMode = 4 /* FVM_DETAILS */, fFlags = FolderFlags() };
                _browser.Initialize(Handle, ref rc, ref fs);
                _browser.SetOptions(0x40 /* EBO_NOBORDER */);
                _browser.SetEmptyText("This shared folder is empty. Drag files here to share them with everyone on your Wi-Fi.");
                _sink = new EventSink(this);
                _browser.Advise(_sink, out _cookie);
                Application.AddMessageFilter(this);
                NavigateTo(path);
            }
            catch (Exception ex) { Log.Error("ExplorerBrowser init", ex); }
        }

        void DestroyBrowser()
        {
            if (_browser == null) return;
            Application.RemoveMessageFilter(this);
            try
            {
                _browser.Unadvise(_cookie);
                _browser.Destroy();
            }
            catch { }
            Marshal.ReleaseComObject(_browser);
            _browser = null;
        }

        /// <summary>Recreate the view (after a light/dark switch) at the same folder.</summary>
        public void Rebuild()
        {
            if (!IsHandleCreated) return;
            var path = CurrentPath ?? _root;
            DestroyBrowser();
            BackColor = Theme.Surface;
            Create(path);
        }

        // Column header only in Details view, like File Explorer.
        // FWF_NOHEADERINALLVIEWS: the column header then appears only in Details view, like File Explorer.
        static uint FolderFlags() => 0x01000000u;

        void ApplyViewMode()
        {
            var fv = GetFolderView();
            if (fv == null) return;
            int mode, size;
            switch (_mode)
            {
                case ViewMode.ExtraLargeIcons: mode = 1; size = 256; break;
                case ViewMode.LargeIcons: mode = 1; size = 96; break;
                case ViewMode.MediumIcons: mode = 1; size = 48; break;
                case ViewMode.SmallIcons: mode = 2; size = 16; break;
                case ViewMode.List: mode = 3; size = 16; break;
                case ViewMode.Tiles: mode = 6; size = 48; break;
                case ViewMode.Content: mode = 8; size = 32; break;
                default: mode = 4; size = 16; break;
            }
            // The browser-level settings stick for this and future views; then fine-tune the icon size.
            var fs = new FOLDERSETTINGS { ViewMode = (uint)mode, fFlags = FolderFlags() };
            try { _browser.SetFolderSettings(ref fs); } catch { }
            fv.SetCurrentFolderFlags(0x01000000, FolderFlags());
            // Switching through another mode makes the view rebuild its header for the new flags.
            if (fv.GetViewModeAndIconSize(out int cur, out _) == 0 && cur == mode)
                fv.SetViewModeAndIconSize(mode == 4 ? 3 : 4, 16);
            fv.SetViewModeAndIconSize(mode, size);
        }

        public void NavigateTo(string path)
        {
            if (_browser == null) return;
            if (SHParseDisplayName(path, IntPtr.Zero, out var pidl, 0, out _) != 0 || pidl == IntPtr.Zero) return;
            try { _browser.BrowseToIDList(pidl, 0 /* SBSP_ABSOLUTE */); }
            finally { Marshal.FreeCoTaskMem(pidl); }
        }

        public void GoBack() => _browser?.BrowseToIDList(IntPtr.Zero, 0x4000 /* SBSP_NAVIGATEBACK */);
        public void GoForward() => _browser?.BrowseToIDList(IntPtr.Zero, 0x8000 /* SBSP_NAVIGATEFORWARD */);

        public void GoUp()
        {
            if (!AtRoot) _browser?.BrowseToIDList(IntPtr.Zero, 0x2000 /* SBSP_PARENT */);
        }

        public void GoHome() => NavigateTo(_root);

        public void RefreshView() => GetView()?.Refresh();

        IShellView GetView()
        {
            if (_browser == null) return null;
            var iid = typeof(IShellView).GUID;
            return _browser.GetCurrentView(ref iid, out var o) == 0 ? o as IShellView : null;
        }

        IFolderView2 GetFolderView()
        {
            if (_browser == null) return null;
            var iid = typeof(IFolderView2).GUID;
            return _browser.GetCurrentView(ref iid, out var o) == 0 ? o as IFolderView2 : null;
        }

        /// <summary>Runs a command of the folder background menu, exactly like Explorer's command bar.</summary>
        bool InvokeBackgroundVerb(string verb)
        {
            var view = GetView();
            if (view == null) return false;
            var iid = typeof(IContextMenu).GUID;
            if (view.GetItemObject(0 /* SVGIO_BACKGROUND */, ref iid, out var ptr) != 0 || ptr == IntPtr.Zero) return false;
            IntPtr menu = IntPtr.Zero, a = IntPtr.Zero, w = IntPtr.Zero;
            try
            {
                var cm = (IContextMenu)Marshal.GetObjectForIUnknown(ptr);
                menu = CreatePopupMenu();
                cm.QueryContextMenu(menu, 0, 1, 0x7FFF, 0);
                a = Marshal.StringToHGlobalAnsi(verb);
                w = Marshal.StringToHGlobalUni(verb);
                var ci = new CMINVOKECOMMANDINFOEX
                {
                    fMask = 0x4000 /* CMIC_MASK_UNICODE */, hwnd = Handle, lpVerb = a, lpVerbW = w, nShow = 1,
                };
                ci.cbSize = Marshal.SizeOf(ci);
                int hr = cm.InvokeCommand(ref ci);
                Log.Info($"Explorer verb '{verb}' -> 0x{hr:X8}");
                return hr == 0;
            }
            catch (Exception ex)
            {
                Log.Error("verb " + verb, ex);
                return false;
            }
            finally
            {
                if (menu != IntPtr.Zero) DestroyMenu(menu);
                if (a != IntPtr.Zero) Marshal.FreeHGlobal(a);
                if (w != IntPtr.Zero) Marshal.FreeHGlobal(w);
                Marshal.Release(ptr);
            }
        }

        /// <summary>New folder the Explorer way: created and immediately in rename mode.</summary>
        public void NewFolder()
        {
            Focus();
            if (InvokeBackgroundVerb("NewFolder")) return;
            // Fallback: create it ourselves and start rename.
            var path = PathUtil.UniquePath(CurrentPath ?? _root, "New folder");
            Directory.CreateDirectory(path);
            Native.NotifyShell(path, true, false);
            SelectLater(path, true);
        }

        /// <summary>Paste: files/folders via Explorer; image or text data on the clipboard becomes a new file.</summary>
        public void Paste()
        {
            try
            {
                if (Clipboard.ContainsFileDropList())
                {
                    Focus();
                    InvokeBackgroundVerb("paste");
                }
                else if (!PasteData())
                    System.Media.SystemSounds.Beep.Play();
            }
            catch (Exception ex) { Log.Error("paste", ex); }
        }

        bool PasteData()
        {
            var dir = CurrentPath ?? _root;
            string file = null;
            var stamp = DateTime.Now.ToString("yyyy-MM-dd HHmmss");
            if (Clipboard.ContainsImage())
            {
                using (var img = Clipboard.GetImage())
                {
                    if (img == null) return false;
                    file = PathUtil.UniquePath(dir, $"Pasted image {stamp}.png");
                    img.Save(file, ImageFormat.Png);
                }
            }
            else if (Clipboard.ContainsText())
            {
                file = PathUtil.UniquePath(dir, $"Pasted text {stamp}.txt");
                File.WriteAllText(file, Clipboard.GetText(), Encoding.UTF8);
            }
            if (file == null) return false;
            Native.NotifyShell(file, false, false);
            SelectLater(file, false);
            return true;
        }

        void SelectLater(string path, bool rename)
        {
            int tries = 0;
            var t = new Timer { Interval = 120 };
            t.Tick += (s, e) =>
            {
                if (SelectItem(path, rename) || ++tries > 15) { t.Stop(); t.Dispose(); }
            };
            t.Start();
        }

        bool SelectItem(string fullPath, bool rename)
        {
            var view = GetView();
            if (view == null) return false;
            if (SHParseDisplayName(fullPath, IntPtr.Zero, out var pidl, 0, out _) != 0 || pidl == IntPtr.Zero) return false;
            try
            {
                // SVSI_SELECT | DESELECTOTHERS | ENSUREVISIBLE | FOCUSED (+ EDIT)
                uint flags = 0x01 | 0x04 | 0x08 | 0x10 | (rename ? 0x03u : 0u);
                return view.SelectItem(ILFindLastID(pidl), flags) == 0;
            }
            finally { Marshal.FreeCoTaskMem(pidl); }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            _browser?.SetRect(IntPtr.Zero, new RECT { Right = ClientSize.Width, Bottom = ClientSize.Height });
        }

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            var view = GetView();
            if (view != null && view.GetWindow(out var hwnd) == 0) SetFocus(hwnd);
        }

        protected override bool IsInputKey(Keys keyData) => true;
        protected override bool IsInputChar(char charCode) => true;

        /// <summary>Route keyboard accelerators (Ctrl+C/V/X/Z/A, Del, F2, ...) to the Explorer view.</summary>
        public bool PreFilterMessage(ref Message m)
        {
            if (_browser == null || m.Msg < 0x100 || m.Msg > 0x109) return false;
            if (m.HWnd != Handle && !IsChild(Handle, m.HWnd)) return false;

            // Ctrl+V with an image/text (not files) on the clipboard: Explorer can't paste that, we save it as a file.
            if (m.Msg == 0x100 && (Keys)(int)m.WParam == Keys.V && ModifierKeys == Keys.Control && !IsEditFocused())
            {
                try
                {
                    if (!Clipboard.ContainsFileDropList() && (Clipboard.ContainsImage() || Clipboard.ContainsText()))
                        return PasteData();
                }
                catch { }
            }

            if (_browser is IInputObject io)
            {
                var msg = new MSG { hwnd = m.HWnd, message = (uint)m.Msg, wParam = m.WParam, lParam = m.LParam };
                if (io.TranslateAcceleratorIO(ref msg) == 0) return true;
            }
            return false;
        }

        static bool IsEditFocused()
        {
            var sb = new StringBuilder(64);
            Native.GetClassName(Native.GetFocus(), sb, sb.Capacity);
            return sb.ToString() == "Edit";
        }

        internal int OnNavigationPending(IntPtr pidl)
        {
            var path = PathFromPidl(pidl);
            if (path != null && IsInsideRoot(path)) return 0;
            // Anything outside the shared folder opens in a normal Explorer window instead.
            if (path != null && Directory.Exists(path))
                BeginInvoke(new Action(() => { try { Process.Start("explorer.exe", "\"" + path + "\""); } catch { } }));
            return unchecked((int)0x80004005); // E_FAIL = cancel
        }

        internal void OnViewCreated(object view)
        {
            try
            {
                if (view is IFolderView2 fv) fv.SetCurrentFolderFlags(0x01000000, FolderFlags());
                if (view is IShellView sv && sv.GetWindow(out var hwnd) == 0)
                {
                    // Only opt in to dark mode; overriding the view's visual style loses the selection highlight.
                    Native.AllowDarkModeForWindow(hwnd, Theme.Dark);
                }
            }
            catch { }
        }

        internal void OnNavigationComplete(IntPtr pidl)
        {
            CurrentPath = PathFromPidl(pidl) ?? _root;
            ApplyViewMode(); // same view in every folder
            Navigated?.Invoke(this, EventArgs.Empty);
        }

        bool IsInsideRoot(string path)
        {
            path = path.TrimEnd('\\');
            return path.Equals(_root, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(_root + "\\", StringComparison.OrdinalIgnoreCase);
        }

        static string PathFromPidl(IntPtr pidl)
        {
            var sb = new StringBuilder(1024);
            return pidl != IntPtr.Zero && SHGetPathFromIDListW(pidl, sb) ? sb.ToString() : null;
        }

        protected override void Dispose(bool disposing)
        {
            DestroyBrowser();
            base.Dispose(disposing);
        }

        // ------------------------------------------------------------------ COM interop

        [ComVisible(true)]
        public sealed class EventSink : IExplorerBrowserEvents
        {
            readonly ExplorerHost _host;
            internal EventSink(ExplorerHost host) { _host = host; }
            public int OnNavigationPending(IntPtr pidlFolder) => _host.OnNavigationPending(pidlFolder);
            public int OnViewCreated(object psv) { _host.OnViewCreated(psv); return 0; }
            public int OnNavigationComplete(IntPtr pidlFolder) { _host.OnNavigationComplete(pidlFolder); return 0; }
            public int OnNavigationFailed(IntPtr pidlFolder) => 0;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct FOLDERSETTINGS { public uint ViewMode; public uint fFlags; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int ptX, ptY;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct CMINVOKECOMMANDINFOEX
        {
            public int cbSize;
            public uint fMask;
            public IntPtr hwnd;
            public IntPtr lpVerb;
            public IntPtr lpParameters;
            public IntPtr lpDirectory;
            public int nShow;
            public uint dwHotKey;
            public IntPtr hIcon;
            public IntPtr lpTitle;
            public IntPtr lpVerbW;
            public IntPtr lpParametersW;
            public IntPtr lpDirectoryW;
            public IntPtr lpTitleW;
            public int ptX, ptY;
        }

        [ComImport, Guid("DFD3B6B5-C10C-4BE9-85F6-A66969F402F6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IExplorerBrowser
        {
            void Initialize(IntPtr hwndParent, [In] ref RECT prc, [In] ref FOLDERSETTINGS pfs);
            void Destroy();
            void SetRect(IntPtr phdwp, RECT rcBrowser);
            void SetPropertyBag([MarshalAs(UnmanagedType.LPWStr)] string pszPropertyBag);
            void SetEmptyText([MarshalAs(UnmanagedType.LPWStr)] string pszEmptyText);
            void SetFolderSettings([In] ref FOLDERSETTINGS pfs);
            void Advise(IExplorerBrowserEvents psbe, out uint pdwCookie);
            void Unadvise(uint dwCookie);
            void SetOptions(uint dwFlag);
            void GetOptions(out uint pdwFlag);
            [PreserveSig] int BrowseToIDList(IntPtr pidl, uint uFlags);
            [PreserveSig] int BrowseToObject([MarshalAs(UnmanagedType.IUnknown)] object punk, uint uFlags);
            [PreserveSig] int FillFromObject([MarshalAs(UnmanagedType.IUnknown)] object punk, uint dwFlags);
            [PreserveSig] int RemoveAll();
            [PreserveSig] int GetCurrentView(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
        }

        [ComImport, Guid("361BBDC7-E6EE-4E13-BE58-58E2240C810F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IExplorerBrowserEvents
        {
            [PreserveSig] int OnNavigationPending(IntPtr pidlFolder);
            [PreserveSig] int OnViewCreated([MarshalAs(UnmanagedType.IUnknown)] object psv);
            [PreserveSig] int OnNavigationComplete(IntPtr pidlFolder);
            [PreserveSig] int OnNavigationFailed(IntPtr pidlFolder);
        }

        [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IShellView
        {
            [PreserveSig] int GetWindow(out IntPtr phwnd);
            [PreserveSig] int ContextSensitiveHelp(bool fEnterMode);
            [PreserveSig] int TranslateAccelerator(ref MSG pmsg);
            [PreserveSig] int EnableModeless(bool fEnable);
            [PreserveSig] int UIActivate(uint uState);
            [PreserveSig] int Refresh();
            [PreserveSig] int CreateViewWindow(IntPtr psvPrevious, IntPtr pfs, IntPtr psb, IntPtr prcView, out IntPtr phWnd);
            [PreserveSig] int DestroyViewWindow();
            [PreserveSig] int GetCurrentInfo(IntPtr pfs);
            [PreserveSig] int AddPropertySheetPages(uint dwReserved, IntPtr pfn, IntPtr lparam);
            [PreserveSig] int SaveViewState();
            [PreserveSig] int SelectItem(IntPtr pidlItem, uint uFlags);
            [PreserveSig] int GetItemObject(uint uItem, ref Guid riid, out IntPtr ppv);
        }

        [ComImport, Guid("1af3a467-214f-4298-908e-06b03e0b39f9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IFolderView2
        {
            // IFolderView
            [PreserveSig] int GetCurrentViewMode(out uint pViewMode);
            [PreserveSig] int SetCurrentViewMode(uint viewMode);
            [PreserveSig] int GetFolder(ref Guid riid, out IntPtr ppv);
            [PreserveSig] int Item(int iItemIndex, out IntPtr ppidl);
            [PreserveSig] int ItemCount(uint uFlags, out int pcItems);
            [PreserveSig] int Items(uint uFlags, ref Guid riid, out IntPtr ppv);
            [PreserveSig] int GetSelectionMarkedItem(out int piItem);
            [PreserveSig] int GetFocusedItem(out int piItem);
            [PreserveSig] int GetItemPosition(IntPtr pidl, out long ppt);
            [PreserveSig] int GetSpacing(IntPtr ppt);
            [PreserveSig] int GetDefaultSpacing(out long ppt);
            [PreserveSig] int GetAutoArrange();
            [PreserveSig] int SelectItem(int iItem, uint dwFlags);
            [PreserveSig] int SelectAndPositionItems(uint cidl, IntPtr apidl, IntPtr apt, uint dwFlags);
            // IFolderView2
            [PreserveSig] int SetGroupBy(IntPtr key, bool fAscending);
            [PreserveSig] int GetGroupBy(IntPtr pkey, IntPtr pfAscending);
            [PreserveSig] int SetViewProperty(IntPtr pidl, IntPtr propkey, IntPtr propvar);
            [PreserveSig] int GetViewProperty(IntPtr pidl, IntPtr propkey, IntPtr ppropvar);
            [PreserveSig] int SetTileViewProperties(IntPtr pidl, IntPtr pszPropList);
            [PreserveSig] int SetExtendedTileViewProperties(IntPtr pidl, IntPtr pszPropList);
            [PreserveSig] int SetText(int iType, [MarshalAs(UnmanagedType.LPWStr)] string pwszText);
            [PreserveSig] int SetCurrentFolderFlags(uint dwMask, uint dwFlags);
            [PreserveSig] int GetCurrentFolderFlags(out uint pdwFlags);
            [PreserveSig] int GetSortColumnCount(out int pcColumns);
            [PreserveSig] int SetSortColumns(IntPtr rgSortColumns, int cColumns);
            [PreserveSig] int GetSortColumns(IntPtr rgSortColumns, int cColumns);
            [PreserveSig] int GetItem(int iItem, ref Guid riid, out IntPtr ppv);
            [PreserveSig] int GetVisibleItem(int iStart, bool fPrevious, out int piItem);
            [PreserveSig] int GetSelectedItem(int iStart, out int piItem);
            [PreserveSig] int GetSelection(bool fNoneImpliesFolder, out IntPtr ppsia);
            [PreserveSig] int GetSelectionState(IntPtr pidl, out uint pdwFlags);
            [PreserveSig] int InvokeVerbOnSelection([MarshalAs(UnmanagedType.LPStr)] string pszVerb);
            [PreserveSig] int SetViewModeAndIconSize(int uViewMode, int iImageSize);
            [PreserveSig] int GetViewModeAndIconSize(out int puViewMode, out int piImageSize);
            [PreserveSig] int SetGroupSubsetCount(uint cVisibleRows);
            [PreserveSig] int GetGroupSubsetCount(out uint pcVisibleRows);
            [PreserveSig] int SetRedraw(bool fRedrawOn);
            [PreserveSig] int IsMoveInSameFolder();
            [PreserveSig] int DoRename();
        }

        [ComImport, Guid("000214e4-0000-0000-c000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IContextMenu
        {
            [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
            [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);
            [PreserveSig] int GetCommandString(UIntPtr idcmd, uint uflags, IntPtr reserved, IntPtr commandstring, int cch);
        }

        [ComImport, Guid("68284FAA-6A48-11D0-8C78-00C04FD918B4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IInputObject
        {
            [PreserveSig] int UIActivateIO(bool fActivate, ref MSG pMsg);
            [PreserveSig] int HasFocusIO();
            [PreserveSig] int TranslateAcceleratorIO(ref MSG pMsg);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHParseDisplayName(string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern bool SHGetPathFromIDListW(IntPtr pidl, StringBuilder pszPath);

        [DllImport("shell32.dll")]
        static extern IntPtr ILFindLastID(IntPtr pidl);

        [DllImport("user32.dll")]
        static extern bool IsChild(IntPtr hWndParent, IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern IntPtr SetFocus(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll")]
        static extern bool DestroyMenu(IntPtr hMenu);
    }
}
