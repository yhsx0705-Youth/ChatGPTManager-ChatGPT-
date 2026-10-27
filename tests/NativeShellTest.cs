using System;
using System.IO;
using System.Reflection;

// UI-only harness. The target program's --shell-test creates fake files under this directory.
internal static class NativeShellTest
{
    [STAThread]
    private static int Main()
    {
        string app = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "ChatGPTManager.exe"));
        string report = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "native-shell-result.txt");
        Assembly assembly = Assembly.LoadFrom(app);
        return (int)assembly.EntryPoint.Invoke(null, new object[] { new string[] { "--shell-test", report } });
    }
}
