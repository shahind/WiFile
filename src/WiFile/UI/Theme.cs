using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace WiFile.UI
{
    /// <summary>
    /// Windows 11 style palette that follows the system light/dark setting live,
    /// plus DPI scaling helpers (all hard-coded pixel sizes go through <see cref="S"/>).
    /// </summary>
    static class Theme
    {
        public static bool Dark { get; private set; }
        public static float Scale { get; private set; } = 1f;
        public static event Action Changed;

        static string _forced; // "dark" / "light" from the command line (testing)

        public static Font Base, Small, Bold, Title, Icons, IconsSmall;
        public static Color Window, Surface, Toolbar, Hover, Pressed, Text, Muted, Border, Accent, AccentHover, OnAccent,
            Mine, Theirs, Online, WarnBack, WarnText, Link, InputBack;

        public static int S(int px) => (int)Math.Round(px * Scale);
        public static Size S(int w, int h) => new Size(S(w), S(h));
        public static Padding S(int l, int t, int r, int b) => new Padding(S(l), S(t), S(r), S(b));

        public static void Init(string forced)
        {
            _forced = forced;
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) Scale = g.DpiX / 96f;

            var installed = new InstalledFontCollection().Families.Select(f => f.Name).ToList();
            string text = installed.Contains("Segoe UI Variable Text") ? "Segoe UI Variable Text" : "Segoe UI";
            string display = installed.Contains("Segoe UI Variable Display") ? "Segoe UI Variable Display" : "Segoe UI";
            string icons = installed.Contains("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
            Base = new Font(text, 9.75f);
            Small = new Font(text, 8.25f);
            Bold = new Font(text, 9.75f, FontStyle.Bold);
            Title = new Font(display, 13f, FontStyle.Bold);
            Icons = new Font(icons, 11.5f);
            IconsSmall = new Font(icons, 9f);

            Dark = ReadDark();
            Palette();
            Native.SetAppDarkMode(Dark);
            SystemEvents.UserPreferenceChanged += (s, e) =>
            {
                if (e.Category != UserPreferenceCategory.General && e.Category != UserPreferenceCategory.Color && e.Category != UserPreferenceCategory.VisualStyle) return;
                bool dark = ReadDark();
                var accent = ReadAccent();
                if (dark == Dark && accent == _accentRaw) return;
                Dark = dark;
                Palette();
                Native.SetAppDarkMode(Dark);
                Changed?.Invoke();
            };
        }

        static bool ReadDark()
        {
            if (_forced == "dark") return true;
            if (_forced == "light") return false;
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
            }
            catch { return false; }
        }

        static int? _accentRaw;

        static int? ReadAccent()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM"))
                    return k?.GetValue("AccentColor") is int v ? v : (int?)null;
            }
            catch { return null; }
        }

        static void Palette()
        {
            _accentRaw = ReadAccent();
            // AccentColor is stored as ABGR.
            Color sys = _accentRaw.HasValue
                ? Color.FromArgb(_accentRaw.Value & 0xFF, (_accentRaw.Value >> 8) & 0xFF, (_accentRaw.Value >> 16) & 0xFF)
                : Color.FromArgb(0, 95, 184);

            if (Dark)
            {
                Window = Color.FromArgb(32, 32, 32);
                Surface = Color.FromArgb(25, 25, 25);
                Toolbar = Color.FromArgb(32, 32, 32);
                Hover = Color.FromArgb(50, 50, 50);
                Pressed = Color.FromArgb(62, 62, 62);
                Text = Color.FromArgb(255, 255, 255);
                Muted = Color.FromArgb(160, 160, 160);
                Border = Color.FromArgb(51, 51, 51);
                Accent = Lighten(sys, 0.45f);
                OnAccent = Color.Black;
                Mine = Mix(sys, Surface, 0.45f);
                Theirs = Color.FromArgb(45, 45, 45);
                Online = Color.FromArgb(108, 203, 95);
                WarnBack = Color.FromArgb(67, 53, 25);
                WarnText = Color.FromArgb(252, 225, 0);
                Link = Accent;
                InputBack = Color.FromArgb(45, 45, 45);
            }
            else
            {
                Window = Color.FromArgb(243, 243, 243);
                Surface = Color.White;
                Toolbar = Color.FromArgb(249, 249, 249);
                Hover = Color.FromArgb(234, 234, 234);
                Pressed = Color.FromArgb(222, 222, 222);
                Text = Color.FromArgb(27, 27, 27);
                Muted = Color.FromArgb(96, 96, 96);
                Border = Color.FromArgb(229, 229, 229);
                Accent = Darken(sys, 0.1f);
                OnAccent = Color.White;
                Mine = Mix(sys, Color.White, 0.16f);
                Theirs = Color.FromArgb(240, 240, 240);
                Online = Color.FromArgb(15, 123, 15);
                WarnBack = Color.FromArgb(255, 244, 206);
                WarnText = Color.FromArgb(125, 80, 0);
                Link = Accent;
                InputBack = Color.White;
            }
            AccentHover = Dark ? Lighten(Accent, 0.15f) : Darken(Accent, 0.12f);
        }

        public static Color Mix(Color a, Color b, float amountOfA) => Color.FromArgb(
            (int)(a.R * amountOfA + b.R * (1 - amountOfA)),
            (int)(a.G * amountOfA + b.G * (1 - amountOfA)),
            (int)(a.B * amountOfA + b.B * (1 - amountOfA)));

        static Color Lighten(Color c, float k) => Mix(Color.White, c, k);
        static Color Darken(Color c, float k) => Mix(Color.Black, c, k);

        static readonly Color[] NameColorsLight =
        {
            Color.FromArgb(0, 99, 177), Color.FromArgb(135, 100, 184), Color.FromArgb(202, 80, 16),
            Color.FromArgb(16, 124, 16), Color.FromArgb(194, 57, 179), Color.FromArgb(3, 131, 135), Color.FromArgb(142, 86, 46),
        };

        static readonly Color[] NameColorsDark =
        {
            Color.FromArgb(96, 205, 255), Color.FromArgb(200, 170, 255), Color.FromArgb(255, 150, 90),
            Color.FromArgb(120, 220, 120), Color.FromArgb(255, 140, 230), Color.FromArgb(80, 220, 220), Color.FromArgb(230, 180, 120),
        };

        public static Color ForName(string name)
        {
            int h = 17;
            foreach (var c in name ?? "") h = h * 31 + c;
            var set = Dark ? NameColorsDark : NameColorsLight;
            return set[(h & 0x7fffffff) % set.Length];
        }

        public static GraphicsPath Rounded(Rectangle r, int radius)
        {
            var p = new GraphicsPath();
            int d = Math.Max(1, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
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

        /// <summary>Dark title bar + caption color matching the window (Windows 10 20H1+/11).</summary>
        public static void ApplyTitleBar(Form f)
        {
            if (!f.IsHandleCreated) return;
            int on = Dark ? 1 : 0;
            Native.DwmSetWindowAttribute(f.Handle, 20, ref on, 4); // DWMWA_USE_IMMERSIVE_DARK_MODE
            int caption = ColorTranslator.ToWin32(Window);
            Native.DwmSetWindowAttribute(f.Handle, 35, ref caption, 4); // DWMWA_CAPTION_COLOR (Win11)
        }

        /// <summary>Dark/light scrollbars and common controls for a native window.</summary>
        public static void ApplyScrollbars(Control c)
        {
            if (c.IsHandleCreated) Native.SetWindowTheme(c.Handle, Dark ? "DarkMode_Explorer" : "Explorer", null);
        }
    }

    static class Native
    {
        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        public static extern int SetWindowTheme(IntPtr hwnd, string app, string idList);

        [DllImport("uxtheme.dll", EntryPoint = "#135")]
        static extern int SetPreferredAppMode(int mode);

        [DllImport("uxtheme.dll", EntryPoint = "#136")]
        static extern void FlushMenuThemes();

        [DllImport("uxtheme.dll", EntryPoint = "#104")]
        static extern void RefreshImmersiveColorPolicyState();

        [DllImport("uxtheme.dll", EntryPoint = "#133")]
        public static extern bool AllowDarkModeForWindow(IntPtr hwnd, bool allow);

        [DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wp, IntPtr lp);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder name, int max);

        [DllImport("user32.dll")]
        public static extern IntPtr GetFocus();

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern void SHChangeNotify(int eventId, uint flags, string item1, IntPtr item2);

        /// <summary>Opt this process into dark mode for shell views, menus and dialogs (Windows 10 1903+).</summary>
        public static void SetAppDarkMode(bool dark)
        {
            try
            {
                if (Environment.OSVersion.Version.Build < 18362) return;
                SetPreferredAppMode(dark ? 2 /* ForceDark */ : 3 /* ForceLight */);
                RefreshImmersiveColorPolicyState();
                FlushMenuThemes();
            }
            catch { }
        }

        /// <summary>Tell Explorer views (ours included) that something changed on disk, so they update instantly.</summary>
        public static void NotifyShell(string path, bool isDir, bool removed)
        {
            const uint SHCNF_PATHW = 0x0005, SHCNF_FLUSHNOWAIT = 0x3000;
            int ev = removed ? (isDir ? 0x10 /* RMDIR */ : 0x4 /* DELETE */) : (isDir ? 0x8 /* MKDIR */ : 0x2 /* CREATE */);
            try
            {
                SHChangeNotify(ev, SHCNF_PATHW | SHCNF_FLUSHNOWAIT, path, IntPtr.Zero);
                if (!removed && !isDir) SHChangeNotify(0x2000 /* UPDATEITEM */, SHCNF_PATHW | SHCNF_FLUSHNOWAIT, path, IntPtr.Zero);
            }
            catch { }
        }
    }
}
