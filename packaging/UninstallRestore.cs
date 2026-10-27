using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("ChatGPT Manager Uninstaller")]
[assembly: AssemblyProduct("ChatGPT Manager")]
[assembly: AssemblyDescription("Restores the Official ChatGPT configuration and removes ChatGPT Manager")]
[assembly: AssemblyVersion("1.8.1.0")]
[assembly: AssemblyFileVersion("1.8.1.0")]

namespace ChatGPTManager
{
    internal static class UninstallRestore
    {
        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            bool silent = Array.Exists(args, value => String.Equals(value, "--silent", StringComparison.OrdinalIgnoreCase));
            if (Array.Exists(args, value => String.Equals(value, "--check-codex", StringComparison.OrdinalIgnoreCase)))
                return AnyConfigurationAppRunning() ? 1 : 0;
            if (args.Length == 2 && String.Equals(args[0], "--validate-install-path", StringComparison.OrdinalIgnoreCase))
                return InstallerPathPolicy.IsValid(args[1]) ? 0 : 2;
            if (args.Length >= 6 && String.Equals(args[0], "--run-uninstall", StringComparison.OrdinalIgnoreCase))
            {
                int parentId;
                if (!Int32.TryParse(args[3], out parentId)) return 2;
                return RunUninstallWorker(args[1], args[2], parentId, args[4], args[5]);
            }

            return StartUninstallWorker(silent);
        }

        private static int StartUninstallWorker(bool silent)
        {
            string sourceExe = Assembly.GetExecutingAssembly().Location;
            string root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceExe), ".."));
            if (!InstallerPathPolicy.IsValid(root)) return 2;

            string workerExe = Path.Combine(Path.GetTempPath(),
                "ChatGPTManagerUninstall-" + Guid.NewGuid().ToString("N") + ".exe");
            string token = Path.GetFileNameWithoutExtension(workerExe);
            string completionName = "Local\\" + token + "-Complete";
            string resultPath = Path.Combine(Path.GetTempPath(), token + ".result");
            try
            {
                File.Copy(sourceExe, workerExe, false);
                using (EventWaitHandle completed = new EventWaitHandle(false, EventResetMode.ManualReset, completionName))
                {
                    ProcessStartInfo start = new ProcessStartInfo
                    {
                        FileName = workerExe,
                        Arguments = "--run-uninstall " + Quote(root) + " " + Quote(workerExe) + " "
                            + Process.GetCurrentProcess().Id + " " + Quote(completionName) + " " + Quote(resultPath)
                            + (silent ? " --silent" : ""),
                        WorkingDirectory = Path.GetTempPath(),
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden
                    };
                    using (Process worker = Process.Start(start))
                    {
                        if (worker == null) throw new IOException("Could not start the uninstall worker.");
                        while (!completed.WaitOne(200))
                        {
                            if (worker.WaitForExit(0))
                                throw new IOException("The uninstall worker stopped before reporting a result.");
                        }

                        string[] result = File.Exists(resultPath)
                            ? File.ReadAllLines(resultPath, Encoding.UTF8)
                            : new string[0];
                        bool success = result.Length > 0 && result[0] == "0";
                        if (!success && !silent)
                        {
                            string message = result.Length > 1
                                ? String.Join(Environment.NewLine, result, 1, result.Length - 1)
                                : "Uninstall could not finish. The installation folder was kept for a retry.";
                            MessageBox.Show(message, "Uninstall could not continue",
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        }
                        return success ? 0 : 1;
                    }
                }
            }
            catch (Exception ex)
            {
                try { if (File.Exists(workerExe)) File.Delete(workerExe); } catch { }
                try { if (File.Exists(resultPath)) File.Delete(resultPath); } catch { }
                if (!silent)
                    MessageBox.Show("Uninstall could not start.\n" + ex.Message, "ChatGPT Manager Uninstaller",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 1;
            }
        }

        private static string Quote(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private static int RunUninstallWorker(string root, string workerExe, int parentId,
            string completionName, string resultPath)
        {
            if (!IsTrustedWorkerInvocation(root, workerExe, parentId, completionName, resultPath)) return 2;

            AppPaths paths = new AppPaths(root, AppPaths.ResolveCurrentTarget());
            RestoreResult result = RestoreOfficialService.BeforeUninstall(paths,
                new DirectUninstallCopy(), AnyConfigurationAppRunning);
            if (result.Success)
            {
                try
                {
                    if (AnyConfigurationAppRunning())
                        result = new RestoreResult(false, "ChatGPT or ChatGPT Manager started during restore. The official configuration is restored; close it and retry uninstall.");
                    else
                    {
                        string installerPath = Path.Combine(paths.Root, "APP", "ChatGPTManagerSetup.exe");
                        string uninstallerPath = Path.Combine(paths.Root, "APP", "Uninstall.exe");
                        UninstallProfileCleanup.Clear(paths, installerPath, uninstallerPath);
                        AppSettings.Clear();
                        RemoveShortcuts();
                        RemoveUninstallRegistration();
                    }
                }
                catch (Exception ex)
                {
                    result = new RestoreResult(false, "The official configuration was restored, but cleanup did not finish. You can retry uninstall.\n" + ex.Message);
                }
            }

            try
            {
                File.WriteAllText(resultPath, (result.Success ? "0" : "1") + Environment.NewLine + result.Message,
                    new UTF8Encoding(false));
            }
            catch { }
            using (EventWaitHandle completed = new EventWaitHandle(false, EventResetMode.ManualReset, completionName))
                completed.Set();

            WaitForParentExit(parentId);
            if (result.Success)
            {
                try
                {
                    UninstallProfileCleanup.RemoveUninstallerAfterExit(paths,
                        Path.Combine(paths.Root, "APP", "Uninstall.exe"));
                }
                catch { }
            }
            ScheduleWorkerCleanup(workerExe, resultPath);
            return result.Success ? 0 : 1;
        }

        private static bool IsTrustedWorkerInvocation(string root, string workerExe, int parentId,
            string completionName, string resultPath)
        {
            try
            {
                string actualWorker = Path.GetFullPath(Assembly.GetExecutingAssembly().Location);
                string requestedWorker = Path.GetFullPath(workerExe);
                string workerName = Path.GetFileNameWithoutExtension(actualWorker);
                string tempRoot = Path.GetFullPath(Path.GetTempPath())
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!String.Equals(actualWorker, requestedWorker, StringComparison.OrdinalIgnoreCase)
                    || !actualWorker.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)
                    || !workerName.StartsWith("ChatGPTManagerUninstall-", StringComparison.OrdinalIgnoreCase)
                    || !String.Equals(completionName, "Local\\" + workerName + "-Complete", StringComparison.OrdinalIgnoreCase)
                    || !String.Equals(Path.GetFullPath(resultPath),
                        Path.GetFullPath(Path.Combine(Path.GetTempPath(), workerName + ".result")),
                        StringComparison.OrdinalIgnoreCase))
                    return false;

                using (Process parent = Process.GetProcessById(parentId))
                    return UninstallInvocationPolicy.IsExpectedInstallRoot(root, parent.MainModule.FileName);
            }
            catch { return false; }
        }

        private static void WaitForParentExit(int parentId)
        {
            try
            {
                using (Process parent = Process.GetProcessById(parentId))
                    parent.WaitForExit();
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }

        private static void ScheduleWorkerCleanup(string workerExe, string resultPath)
        {
            try
            {
                string command = Path.Combine(Environment.SystemDirectory, "cmd.exe");
                var start = new ProcessStartInfo
                {
                    FileName = command,
                    Arguments = "/d /c timeout /t 2 /nobreak > nul & del /f /q \"" + workerExe
                        + "\" \"" + resultPath + "\"",
                    WorkingDirectory = Environment.SystemDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                Process cleanup = Process.Start(start);
                if (cleanup != null) cleanup.Dispose();
            }
            catch { }
        }

        private static void RemoveShortcuts()
        {
            string oldDesktopShortcut = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "Codex Switch.lnk");
            string oldStartMenuFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs", "Codex Switch");
            string oldStartMenuShortcut = Path.Combine(oldStartMenuFolder, "Codex Switch.lnk");
            string desktopShortcut = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "ChatGPT Manager.lnk");
            string startMenuFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs", "ChatGPT Manager");
            string startMenuShortcut = Path.Combine(startMenuFolder, "ChatGPT Manager.lnk");

            foreach (string shortcut in new[] { oldDesktopShortcut, oldStartMenuShortcut, desktopShortcut, startMenuShortcut })
                if (File.Exists(shortcut)) File.Delete(shortcut);
            foreach (string folder in new[] { oldStartMenuFolder, startMenuFolder })
                if (Directory.Exists(folder) && Directory.GetFileSystemEntries(folder).Length == 0)
                    Directory.Delete(folder);
        }

        private static void RemoveUninstallRegistration()
        {
            using (RegistryKey uninstallKey = Registry.LocalMachine.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall", true))
            {
                if (uninstallKey != null)
                {
                    uninstallKey.DeleteSubKeyTree("ChatGPTManager", false);
                    uninstallKey.DeleteSubKeyTree("CodexSwitch", false);
                }
            }
        }

        private static bool AnyConfigurationAppRunning()
        {
            if (CodexProcesses.AnyRunning()) return true;
            foreach (string processName in new[] { "ChatGPTManager", "CodexSwitch" })
            {
                foreach (Process process in Process.GetProcessesByName(processName))
                {
                    using (process)
                    {
                        try { if (!process.HasExited) return true; }
                        catch (InvalidOperationException) { }
                    }
                }
            }
            return false;
        }
    }
}
