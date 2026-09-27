using System;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using WiFile.Core;
using WiFile.UI;

namespace WiFile
{
    static class Program
    {
        /// <summary>Optional "--profile NAME" runs an isolated second instance (for testing on one PC).</summary>
        public static string Profile;

        [STAThread]
        static int Main(string[] args)
        {
            int pi = Array.IndexOf(args, "--profile");
            if (pi >= 0 && pi + 1 < args.Length) Profile = args[pi + 1];
            bool minimized = args.Contains("--minimized");

            if (args.Contains("--unregister"))
            {
                MainForm.RemoveAutoStart();
                return 0;
            }

            var id = "WiFile" + (Profile == null ? "" : "-" + Profile);
            using (var mutex = new Mutex(true, @"Local\" + id + "-instance", out bool first))
            using (var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\" + id + "-show"))
            {
                if (!first)
                {
                    showEvent.Set(); // bring the running instance to the front
                    return 0;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                WiFileNode node;
                try
                {
                    node = new WiFileNode(NodeConfig.Default(Profile));
                    node.Start();
                }
                catch (Exception ex)
                {
                    MessageBox.Show("WiFile could not start:\n\n" + ex.Message, "WiFile", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 1;
                }

                Application.ThreadException += (s, e) => Log.Error("UI", e.Exception);
                AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Error("fatal", e.ExceptionObject as Exception);

                using (var form = new MainForm(node, minimized))
                {
                    var waiter = new Thread(() =>
                    {
                        while (showEvent.WaitOne())
                        {
                            try { form.BeginInvoke(new Action(form.ShowFromTray)); }
                            catch { break; }
                        }
                    }) { IsBackground = true };
                    waiter.Start();
                    Application.Run(form);
                }
                node.Dispose();
                return 0;
            }
        }
    }
}
