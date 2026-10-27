using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("ChatGPT Manager")]
[assembly: AssemblyProduct("ChatGPT Manager")]
[assembly: AssemblyDescription("轻量化 ChatGPT 官方 / 中转配置管理器")]
[assembly: AssemblyVersion("1.8.1.0")]
[assembly: AssemblyFileVersion("1.8.1.0")]

namespace ChatGPTManager
{
    internal static class Program
    {
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (args.Length == 2 && args[0] == "--self-test") return SelfTests.Run(args[1]);
            if (args.Length == 2 && args[0] == "--shell-test")
            {
                // This mode creates synthetic files exclusively below APP/tests/native-shell.
                // It cannot point at the real profile or either user-supplied source directory.
                string testRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tests", "native-shell");
                Directory.CreateDirectory(Path.Combine(testRoot, "source"));
                Directory.CreateDirectory(Path.Combine(testRoot, "target"));
                foreach (string name in ConfigFiles.Names)
                {
                    File.WriteAllText(Path.Combine(testRoot, "source", name), "NEW fake test data: " + name);
                    File.WriteAllText(Path.Combine(testRoot, "target", name), "OLD fake test data: " + name);
                }
                using (Form form = new Form { Text = "ChatGPT 管理器 · 原生覆盖测试（仅模拟文件）", Width = 680, Height = 240, StartPosition = FormStartPosition.CenterScreen })
                {
                    var label = new Label { Dock = DockStyle.Fill, TextAlign = System.Drawing.ContentAlignment.MiddleCenter,
                        Text = "本窗口仅复制 APP\\tests 下的模拟文件。\n不会访问真实 ChatGPT 配置。", Font = new System.Drawing.Font("Microsoft YaHei UI", 12) };
                    form.Controls.Add(label);
                    form.Shown += delegate {
                        form.BeginInvoke((Action)delegate {
                            try
                            {
                                bool result = new WindowsConfirmedCopy().CopyPair(form.Handle,
                                    Path.Combine(testRoot, "source"), Path.Combine(testRoot, "target"), "原生覆盖测试");
                                File.WriteAllText(args[1], "Completed=" + result + "\r\nEqual="
                                    + ConfigFiles.Equal(Path.Combine(testRoot, "source"), Path.Combine(testRoot, "target")));
                            }
                            catch (Exception ex) { File.WriteAllText(args[1], ex.ToString()); }
                            form.Close();
                        });
                    };
                    Application.Run(form);
                }
                return 0;
            }
            bool created;
            string activationName = "Local\\ChatGPTManager_Activate_" + Environment.UserName;
            using (var activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, activationName))
            using (Mutex instance = new Mutex(true, "Local\\ChatGPTManager_" + Environment.UserName, out created))
            {
                if (!created)
                {
                    // A second shortcut launch is also the recovery path when the
                    // background ball has been disabled from its context menu.
                    activationEvent.Set();
                    return 0;
                }
                RegisteredWaitHandle activationRegistration = null;
                MainForm mainForm = null;
                try
                {
                    mainForm = new MainForm(AppPaths.Default());
                    activationRegistration = ThreadPool.RegisterWaitForSingleObject(activationEvent,
                        delegate
                        {
                            try
                            {
                                if (mainForm != null && !mainForm.IsDisposed && mainForm.IsHandleCreated)
                                    mainForm.BeginInvoke((Action)mainForm.RestoreFromExternalRequest);
                            }
                            catch (ObjectDisposedException) { }
                            catch (InvalidOperationException) { }
                        }, null, Timeout.Infinite, false);
                    Application.Run(mainForm);
                }
                catch (Exception ex) { MessageBox.Show(ex.Message, "ChatGPT 管理器", MessageBoxButtons.OK, MessageBoxIcon.Error); return 1; }
                finally { if (activationRegistration != null) activationRegistration.Unregister(null); }
            }
            return 0;
        }
    }
}
