using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;

// Verifies the actual released EXE via reflection, with entirely synthetic file paths.
internal static class NativeIntegrationTest
{
    private static readonly string TestBase = AppDomain.CurrentDomain.BaseDirectory;
    private static readonly List<string> Results = new List<string>();
    private static Assembly app;
    private static object paths, nativeCopy;
    private static Type modeType, serviceType, filesType;
    private static string root, official, transit, target;
    private static Form form;
    private static Label label;
    private static readonly string[] Names = { "config.toml", "auth.json" };

    private static string[] Hashes(string folder)
    {
        return Names.Select(name => {
            using (var sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(File.ReadAllBytes(Path.Combine(folder, name))));
        }).ToArray();
    }
    private static bool Matches(string folder, string[] hashes) { return Hashes(folder).SequenceEqual(hashes); }
    private static void Copy(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string name in Names) File.Copy(Path.Combine(from, name), Path.Combine(to, name), true);
    }
    private static void Check(bool okay, string description)
    {
        if (!okay) throw new Exception(description);
        Results.Add("PASS " + description);
        File.WriteAllLines(Path.Combine(TestBase, "native-integration-results.txt"), Results, new UTF8Encoding(true));
    }
    private static void Stage(string stage)
    {
        form.Text = "ChatGPT Manager 独立模拟验证 · " + stage;
        label.Text = stage + "\r\n\r\n全部为模拟文件，仅使用 APP\\tests\\runs 中的独立目录。\r\n" + root;
        label.Refresh();
        File.WriteAllText(Path.Combine(TestBase, "native-integration-progress.txt"), stage, Encoding.UTF8);
    }
    private static object Run(string mode, Func<bool> running)
    {
        object service = Activator.CreateInstance(serviceType, paths, nativeCopy, running);
        return serviceType.GetMethod("SwitchTo").Invoke(service, new object[] {
            Enum.Parse(modeType, mode), form.Handle, (Action<string>)(s => { label.Text = s + "\r\n\r\n模拟文件目录：\r\n" + root; label.Refresh(); })
        });
    }
    private static bool Success(object result) { return (bool)result.GetType().GetField("Success").GetValue(result); }
    private static string Mode() { return filesType.GetMethod("Detect").Invoke(null, new object[] { paths }).ToString(); }

    private static void Verify()
    {
        try
        {
            app = Assembly.LoadFrom(Path.GetFullPath(Path.Combine(TestBase, "..", "ChatGPTManager.exe")));
            modeType = app.GetType("ChatGPTManager.ConfigMode", true);
            serviceType = app.GetType("ChatGPTManager.SwitchService", true);
            filesType = app.GetType("ChatGPTManager.ConfigFiles", true);
            root = Path.Combine(TestBase, "runs", "Native-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            official = Path.Combine(root, "Official"); transit = Path.Combine(root, "Transit"); target = Path.Combine(root, "FakeCodex");
            foreach (string folder in new[] { official, transit, target }) Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(official, "config.toml"), "# fake original official\r\nmodel_provider = \"openai\"\r\n", new UTF8Encoding(true));
            File.WriteAllText(Path.Combine(official, "auth.json"), "{ \"auth_mode\":\"chatgpt\", \"tokens\":{\"access_token\":\"FAKE-INITIAL\"} }\r\n", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(transit, "config.toml"), "# fake transit\nmodel_provider = 'custom'\n[model_providers.custom]\nbase_url = 'https://example.invalid/v1'\n");
            File.WriteAllText(Path.Combine(transit, "auth.json"), "{\"OPENAI_API_KEY\":\"FAKE-ONLY\"}\n");
            Copy(official, target);
            string initial = Path.Combine(official, "SafetyBackup", "Initial-synthetic"); Copy(official, initial);
            string[] original = Hashes(official);
            string[] custom = Hashes(transit);
            File.AppendAllText(Path.Combine(target, "config.toml"), "# latest official settings\r\n");
            File.WriteAllText(Path.Combine(target, "auth.json"), "{\"auth_mode\":\"chatgpt\",\"tokens\":{\"access_token\":\"FAKE-LATEST\"}}\n");
            string[] latest = Hashes(target);
            paths = Activator.CreateInstance(app.GetType("ChatGPTManager.AppPaths", true), root, target);
            nativeCopy = Activator.CreateInstance(app.GetType("ChatGPTManager.WindowsConfirmedCopy", true));

            Check(Mode() == "Official", "Refreshed official mode recognized from actual EXE");
            Stage("1/4 官方到中转：确认模拟文件覆盖");
            Check(Success(Run("Transit", () => false)), "Native official-to-transit switch reports success");
            Check(Matches(target, custom) && Mode() == "Transit", "Both target bytes and current transit mode are correct");
            Check(Matches(official, latest), "Official retains the latest configuration and refreshed fake token");
            string[] history = Directory.GetDirectories(Path.Combine(official, "SafetyBackup"));
            Check(history.Any(p => Path.GetFileName(p).StartsWith("Official-") && Matches(p, latest)), "Independent latest official safety copy retained");
            Check(Matches(initial, original), "Initial official safety copy is intact");
            Check(Matches(transit, custom), "Transit source is unchanged");

            Stage("2/4 中转到官方：确认模拟文件覆盖");
            Check(Success(Run("Official", () => false)), "Native transit-to-official switch reports success");
            Check(Matches(target, latest) && Mode() == "Official", "Latest official bytes and official mode restored");
            Check(Matches(official, latest) && Matches(initial, original), "Restoration does not overwrite official backups");

            Copy(transit, target);
            Stage("3/4 跳过还原：在模拟提示中选择跳过");
            Check(!Success(Run("Official", () => false)), "Skipping native overwrite never reports success");
            Check(Matches(target, custom) && Mode() == "Transit", "Skip leaves the target transit configuration intact");
            Check(Matches(official, latest), "Skip leaves Official intact");

            Copy(official, target);
            Stage("4/4 取消备份：关闭模拟覆盖提示");
            Check(!Success(Run("Transit", () => false)), "Cancellation of native official backup stops the switch");
            Check(Matches(target, latest) && Mode() == "Official", "Cancellation leaves current official configuration intact");
            Check(Matches(official, latest) && Matches(initial, original), "Official and initial safety copy survive cancellation");
            Check(Matches(transit, custom), "Transit remains unchanged after all operations");

            MethodInfo processCheck = app.GetType("ChatGPTManager.CodexProcesses", true).GetMethod("AnyRunning");
            bool running = (bool)processCheck.Invoke(null, null);
            Check(running, "Actual running Codex detected on this computer");
            bool blocked = false;
            try { Run("Transit", () => (bool)processCheck.Invoke(null, null)); }
            catch (TargetInvocationException ex) { blocked = ex.InnerException is IOException; }
            Check(blocked && Matches(target, latest), "Actual running-process guard stops copying before any mutation");
            Stage("全部通过：" + Results.Count + " 项原生集成验证");
            label.Text = "全部通过：" + Results.Count + " 项原生集成验证\r\n\r\n已实测双向覆盖、当前模式识别、最新官方备份、跳过与取消。\r\n真实配置未被此测试访问。";
        }
        catch (Exception ex)
        {
            Results.Add("FAIL " + ex);
            File.WriteAllLines(Path.Combine(TestBase, "native-integration-results.txt"), Results, new UTF8Encoding(true));
            form.Text = "ChatGPT Manager 独立模拟验证 · 失败";
            label.Text = ex.ToString();
        }
    }

    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        form = new Form { Text = "ChatGPT Manager 独立模拟验证", ClientSize = new Size(830, 240), StartPosition = FormStartPosition.CenterScreen };
        label = new Label { Dock = DockStyle.Fill, Font = new Font("Microsoft YaHei UI", 11F), TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(18) };
        form.Controls.Add(label);
        form.Shown += delegate { form.BeginInvoke((Action)Verify); };
        Application.Run(form);
    }
}
