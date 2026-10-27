using System;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

internal static class FloatingBallWorkflowTest
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 1) return 2;
        Application.EnableVisualStyles();
        Assembly assembly = Assembly.LoadFrom(args[0]);
        string root = Path.Combine(Path.GetTempPath(), "ChatGPTManagerBallTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Official"));
        Directory.CreateDirectory(Path.Combine(root, "Transit"));
        string target = Path.Combine(root, "Target");
        Directory.CreateDirectory(target);

        Type pathsType = assembly.GetType("ChatGPTManager.AppPaths", true);
        object paths = Activator.CreateInstance(pathsType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, new object[] { root, target }, null);
        Type formType = assembly.GetType("ChatGPTManager.MainForm", true);
        using (Form form = (Form)Activator.CreateInstance(formType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, new[] { paths }, null))
        {
            form.StartPosition = FormStartPosition.Manual;
            form.Left = -10000;
            form.Top = -10000;
            BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            object ball = formType.GetField("floatingBall", flags).GetValue(form);
            Type ballType = ball.GetType();
            PropertyInfo enabled = ballType.GetProperty("IsEnabled", flags);
            MethodInfo hideToBackground = formType.GetMethod("HideToBackground", flags);
            MethodInfo restore = formType.GetMethod("RestoreMainWindow", flags);
            MethodInfo disable = formType.GetMethod("DisableFloatingBall", flags);
            MethodInfo toggle = formType.GetMethod("ToggleFloatingBall", flags);

            Assert(!(bool)ballType.GetProperty("Visible").GetValue(ball, null), "Ball appeared over main window.");
            Assert((bool)enabled.GetValue(ball, null), "Legacy/default background ball was not enabled.");
            hideToBackground.Invoke(form, null);
            Application.DoEvents();
            Assert((bool)ballType.GetProperty("Visible").GetValue(ball, null), "Enabled ball did not appear in background.");
            disable.Invoke(form, null);
            Application.DoEvents();
            Assert(!(bool)enabled.GetValue(ball, null), "Context-menu close did not disable the ball.");
            Assert(!(bool)ballType.GetProperty("Visible").GetValue(ball, null), "Disabled ball remained visible.");
            restore.Invoke(form, null);
            toggle.Invoke(form, null);
            Application.DoEvents();
            Assert((bool)enabled.GetValue(ball, null), "Homepage did not re-enable the ball.");
            Assert(!(bool)ballType.GetProperty("Visible").GetValue(ball, null), "Homepage showed the ball over the main window.");
            hideToBackground.Invoke(form, null);
            Application.DoEvents();
            Assert((bool)ballType.GetProperty("Visible").GetValue(ball, null), "Re-enabled ball did not appear in background.");
            restore.Invoke(form, null);
        }
        Console.WriteLine("PASS floating-ball homepage/background/context-menu workflow");
        return 0;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
