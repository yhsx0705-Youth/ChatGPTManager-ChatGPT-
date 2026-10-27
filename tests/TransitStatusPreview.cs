using System;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

internal static class TransitStatusPreview
{
    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "runs", "Transit-UI-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        string official = Path.Combine(root, "Official"), transit = Path.Combine(root, "Transit"), target = Path.Combine(root, "FakeCodex");
        foreach (string folder in new[] { official, transit, target }) Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(official, "config.toml"), "model_provider = 'openai'\n");
        File.WriteAllText(Path.Combine(official, "auth.json"), "{\"auth_mode\":\"chatgpt\",\"tokens\":{\"access_token\":\"FAKE\"}}");
        File.WriteAllText(Path.Combine(transit, "config.toml"), "model_provider = 'custom'\n[model_providers.custom]\nbase_url = 'https://example.invalid/v1'\n");
        File.WriteAllText(Path.Combine(transit, "auth.json"), "{\"OPENAI_API_KEY\":\"FAKE-ONLY\"}");
        foreach (string name in new[] { "config.toml", "auth.json" }) File.Copy(Path.Combine(transit, name), Path.Combine(target, name));
        File.AppendAllText(Path.Combine(target, "config.toml"), "\n[desktop]\nexample = true\n");
        File.WriteAllText(Path.Combine(target, "auth.json"), "{\"auth_mode\":\"apikey\",\"tokens\":null,\"OPENAI_API_KEY\":\"FAKE-ONLY\"}");
        Assembly app = Assembly.LoadFrom(Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "ChatGPTManager.exe")));
        object paths = Activator.CreateInstance(app.GetType("ChatGPTManager.AppPaths", true), root, target);
        Form form = (Form)Activator.CreateInstance(app.GetType("ChatGPTManager.MainForm", true), paths);
        form.Text = "ChatGPT Manager · 中转状态验证（仅模拟文件）";
        Application.Run(form);
    }
}
