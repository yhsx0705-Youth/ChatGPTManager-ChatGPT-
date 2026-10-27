using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ChatGPTManager
{
    // Additive desktop control. Existing configuration-switching code remains unchanged.
    internal static class CodexDesktopControl
    {
        internal const string AppUserModelId = "OpenAI.Codex_2p2nqsd0c76g0!App";
        private const uint WmClose = 0x0010;
        private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr window, StringBuilder className, int maximumCount);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        internal static string LaunchArguments
        {
            get { return "shell:AppsFolder\\" + AppUserModelId; }
        }

        public static void Start()
        {
            Process.Start(new ProcessStartInfo("explorer.exe", LaunchArguments) {
                UseShellExecute = true
            });
        }

        // Send the normal Windows close request only. Deliberately never call Kill(), so Codex
        // keeps the opportunity to finish saving its session and configuration files.
        public static int RequestGracefulClose()
        {
            var desktopProcessIds = new HashSet<uint>();
            foreach (Process process in Process.GetProcesses())
            {
                using (process)
                {
                    try
                    {
                        if (IsDesktopCodex(process)) desktopProcessIds.Add((uint)process.Id);
                    }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception) { }
                }
            }

            if (desktopProcessIds.Count == 0) return 0;
            int requested = 0;
            EnumWindowsProc callback = delegate(IntPtr window, IntPtr parameter)
            {
                uint processId;
                GetWindowThreadProcessId(window, out processId);
                if (!desktopProcessIds.Contains(processId) || !IsWindowVisible(window)) return true;
                var className = new StringBuilder(128);
                GetClassName(window, className, className.Capacity);
                if (!IsClosableDesktopWindow(true, className.ToString())) return true;
                if (PostMessage(window, WmClose, IntPtr.Zero, IntPtr.Zero)) requested++;
                return true;
            };
            EnumWindows(callback, IntPtr.Zero);
            return requested;
        }

        // Explicit fallback for the user-facing background-close action. Configuration
        // switching itself never calls this method.
        public static int TerminateRemainingProcesses()
        {
            int terminated = 0;
            foreach (Process process in Process.GetProcesses())
            {
                using (process)
                {
                    try
                    {
                        string name = process.ProcessName;
                        string path = null;
                        if (name.Equals("ChatGPT", StringComparison.OrdinalIgnoreCase))
                        {
                            try { path = process.MainModule.FileName; } catch { }
                        }
                        if (!IsBackgroundCodex(name, path)) continue;
                        process.Kill();
                        terminated++;
                    }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception) { }
                    catch (NotSupportedException) { }
                }
            }
            return terminated;
        }

        internal static bool IsBackgroundCodex(string processName, string executablePath)
        {
            return CodexProcesses.IsCodex(processName, executablePath);
        }

        internal static bool IsClosableDesktopWindow(bool visible, string className)
        {
            return visible && String.Equals(className, "Chrome_WidgetWin_1", StringComparison.Ordinal);
        }

        private static bool IsDesktopCodex(Process process)
        {
            string name = process.ProcessName;
            if (!name.Equals("ChatGPT", StringComparison.OrdinalIgnoreCase)
                && !name.Equals("codex-desktop", StringComparison.OrdinalIgnoreCase)) return false;
            string path = null;
            try { path = process.MainModule.FileName; } catch { }
            return CodexProcesses.IsCodex(name, path);
        }
    }
}
