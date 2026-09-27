using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace WiFile.UI
{
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

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { CreateBrowser(); }
            catch (Exception ex) { Core.Log.Error("ExplorerBrowser init", ex); }
        }

        void CreateBrowser()
        {
            var type = Type.GetTypeFromCLSID(new Guid("71F96385-DDD6-48D3-A0C1-AE06E8B055FB"));
            _browser = (IExplorerBrowser)Activator.CreateInstance(type);
            var rc = new RECT { Right = ClientSize.Width, Bottom = ClientSize.Height };
            var fs = new FOLDERSETTINGS { ViewMode = 4 /* FVM_DETAILS */, fFlags = 0 };
            _browser.Initialize(Handle, ref rc, ref fs);
            _browser.SetOptions(0x40 /* EBO_NOBORDER */);
            _browser.SetPropertyBag("WiFile.SharedView");
            _browser.SetEmptyText("This shared folder is empty. Drag files here to share them with everyone on your Wi-Fi.");
            _sink = new EventSink(this);
            _browser.Advise(_sink, out _cookie);
            Application.AddMessageFilter(this);
            NavigateTo(_root);
        }

        public void NavigateTo(string path)
        {
            if (_browser == null) return;
            if (SHParseDisplayName(path, IntPtr.Zero, out var pidl, 0, out _) != 0 || pidl == IntPtr.Zero) return;
            try { _browser.BrowseToIDList(pidl, 0 /* SBSP_ABSOLUTE */); }
            finally { Marshal.FreeCoTaskMem(pidl); }
        }

        public void GoBack() => _browser?.BrowseToIDList(IntPtr.Zero, 0x4000 /* SBSP_NAVIGATEBACK */);

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

        /// <summary>Selects an item in the view and starts in-place rename (used after "New folder").</summary>
        public bool SelectAndRename(string fullPath)
        {
            var view = GetView();
            if (view == null) return false;
            if (SHParseDisplayName(fullPath, IntPtr.Zero, out var pidl, 0, out _) != 0 || pidl == IntPtr.Zero) return false;
            try
            {
                // SVSI_EDIT | SVSI_DESELECTOTHERS | SVSI_ENSUREVISIBLE | SVSI_FOCUSED
                return view.SelectItem(ILFindLastID(pidl), 0x03 | 0x04 | 0x08 | 0x10) == 0;
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
            // Hand keyboard focus to the Explorer view itself.
            var view = GetView();
            if (view != null && view.GetWindow(out var hwnd) == 0) SetFocus(hwnd);
        }

        // Let every key reach the Explorer view instead of WinForms dialog navigation.
        protected override bool IsInputKey(Keys keyData) => true;
        protected override bool IsInputChar(char charCode) => true;

        /// <summary>Route keyboard accelerators (Ctrl+C/V/X/Z/A, Del, F2, ...) to the Explorer view.</summary>
        public bool PreFilterMessage(ref Message m)
        {
            if (_browser == null || m.Msg < 0x100 || m.Msg > 0x109) return false;
            if (m.HWnd != Handle && !IsChild(Handle, m.HWnd)) return false;
            if (_browser is IInputObject io)
            {
                var msg = new MSG { hwnd = m.HWnd, message = (uint)m.Msg, wParam = m.WParam, lParam = m.LParam };
                if (io.TranslateAcceleratorIO(ref msg) == 0) return true;
            }
            return false;
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

        internal void OnNavigationComplete(IntPtr pidl)
        {
            CurrentPath = PathFromPidl(pidl) ?? _root;
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
            if (_browser != null)
            {
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
            base.Dispose(disposing);
        }

        // ------------------------------------------------------------------ COM interop

        [ComVisible(true)]
        public sealed class EventSink : IExplorerBrowserEvents
        {
            readonly ExplorerHost _host;
            internal EventSink(ExplorerHost host) { _host = host; }
            public int OnNavigationPending(IntPtr pidlFolder) => _host.OnNavigationPending(pidlFolder);
            public int OnViewCreated(object psv) => 0;
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
    }
}
