using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;

namespace ChatGPTManager
{
    // All writes in this test harness are confined to APP/tests/runs and contain synthetic data.
    internal static class SelfTests
    {
        private sealed class FakeCopy : IConfirmedCopy
        {
            public int Calls;
            public Func<int, string, string, bool> OnCopy;
            public bool CopyPair(IntPtr owner, string from, string to, string title)
            {
                Calls++;
                if (OnCopy != null) return OnCopy(Calls, from, to);
                Copy(from, to); return true;
            }
        }
        private static void Copy(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (string name in ConfigFiles.Names) File.Copy(Path.Combine(from, name), Path.Combine(to, name), true);
        }
        private static AppPaths Fixture(string root)
        {
            var paths = new AppPaths(Path.Combine(root, "install"), Path.Combine(root, "FakeCodex"));
            Directory.CreateDirectory(paths.Official); Directory.CreateDirectory(paths.Transit); Directory.CreateDirectory(paths.Target);
            File.WriteAllText(Path.Combine(paths.Official, "config.toml"), "# keep BOM, comments and CRLF\r\nmodel_provider = \"openai\"\r\nmodel = 'fake'\r\n", new UTF8Encoding(true));
            File.WriteAllText(Path.Combine(paths.Official, "auth.json"), "{ \"auth_mode\": \"chatgpt\", \"tokens\": { \"access_token\": \"FAKE-NOT-A-REAL-TOKEN\" } }\r\n", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(paths.Transit, "config.toml"), "model_provider = 'custom'\n[model_providers.custom]\nbase_url = 'https://example.invalid/v1'\n");
            File.WriteAllText(Path.Combine(paths.Transit, "auth.json"), "{\"OPENAI_API_KEY\":\"FAKE-NOT-A-REAL-KEY\"}\n");
            Copy(paths.Official, paths.Target); return paths;
        }
        private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
        private static SwitchResult Switch(AppPaths paths, FakeCopy copier, ConfigMode mode)
        { return new SwitchService(paths, copier, delegate { return false; }).SwitchTo(mode, IntPtr.Zero, delegate { }); }
        private static bool Throws(Action action)
        { try { action(); return false; } catch (IOException) { return true; } }

        public static int Run(string reportPath)
        {
            string testBase = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tests");
            string fullReport = Path.GetFullPath(reportPath);
            if (!fullReport.StartsWith(Path.GetFullPath(testBase) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return 2;
            string runRoot = Path.Combine(testBase, "runs", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(runRoot);
            var report = new List<string>();
            int passed = 0, failed = 0;
            var tests = new List<KeyValuePair<string, Action<AppPaths>>>();
            tests.Add(new KeyValuePair<string, Action<AppPaths>>("Codex target resolves per-user and honors CODEX_HOME", p => {
                Assert(AppPaths.ResolveTarget(@"D:\Users\Example", null) == @"D:\Users\Example\.codex",
                    "The current user's profile path was not resolved");
                Assert(AppPaths.ResolveTarget(@"D:\Users\Example", @"E:\CodexProfile") == @"E:\CodexProfile",
                    "CODEX_HOME override was ignored");
                Assert(Throws(() => AppPaths.ResolveTarget(@"D:\Users\Example", "relative-codex")),
                    "A relative CODEX_HOME path was accepted");
                Assert(Throws(() => AppPaths.ResolveTarget(@"D:\Users\Example", @"E:\")),
                    "A drive root was accepted as CODEX_HOME");
            }));
            tests.Add(new KeyValuePair<string, Action<AppPaths>>("Read-only detection: official", p => {
                string[] original = ConfigFiles.Fingerprint(p.Target);
                Assert(ConfigFiles.Detect(p) == ConfigMode.Official, "Wrong mode");
                Assert(ConfigFiles.Matches(p.Target, original), "Detection modified files");
            }));
            tests.Add(new KeyValuePair<string, Action<AppPaths>>("Read-only detection: transit", p => {
                Copy(p.Transit, p.Target); Assert(ConfigFiles.Detect(p) == ConfigMode.Transit, "Wrong mode");
            }));
            tests.Add(new KeyValuePair<string, Action<AppPaths>>("Latest official refresh recognized and saved byte-for-byte", p => {
                File.AppendAllText(Path.Combine(p.Target, "config.toml"), "# latest settings\r\n");
                File.WriteAllText(Path.Combine(p.Target, "auth.json"), "{\"auth_mode\":\"chatgpt\",\"tokens\":{\"access_token\":\"FAKE-REFRESHED\"}}\n");
                string[] latest = ConfigFiles.Fingerprint(p.Target); string[] transit = ConfigFiles.Fingerprint(p.Transit);
                var fake = new FakeCopy();
                Assert(ConfigFiles.Detect(p) == ConfigMode.Official, "Refresh not recognized");
                Assert(Switch(p, fake, ConfigMode.Transit).Success, "Switch failed");
                Assert(fake.Calls == 3, "Expected safety copy, Official backup, then Transit copy");
                Assert(ConfigFiles.Matches(p.Official, latest), "Latest official not retained");
                string history = Directory.GetDirectories(Path.Combine(p.Official, "SafetyBackup")).Single();
                Assert(ConfigFiles.Matches(history, latest), "Independent official copy not retained");
                Assert(ConfigFiles.Matches(p.Target, transit) && ConfigFiles.Matches(p.Transit, transit), "Byte verification failed");
            }));
            tests.Add(new KeyValuePair<string, Action<AppPaths>>("Transit to official never overwrites Official or Transit", p => {
                Copy(p.Transit, p.Target); string[] official = ConfigFiles.Fingerprint(p.Official);
                string[] transit = ConfigFiles.Fingerprint(p.Transit); var fake = new FakeCopy();
                Assert(Switch(p, fake, ConfigMode.Official).Success && fake.Calls == 1, "Restore failed");
                Assert(ConfigFiles.Matches(p.Target, official) && ConfigFiles.Matches(p.Official, official), "Official changed");
                Assert(ConfigFiles.Matches(p.Transit, transit), "Transit changed");
            }));
            tests.Add(new KeyValuePair<string, Action<AppPaths>>("Cancel safety copy: stop before touching Official or active files", p => {
                string[] original = ConfigFiles.Fingerprint(p.Target);
                var fake = new FakeCopy { OnCopy = (n, from, to) => false };
                Assert(!Switch(p, fake, ConfigMode.Transit).Success && fake.Calls == 1, "Did not stop");
                Assert(ConfigFiles.Matches(p.Target, original) && ConfigFiles.Matches(p.Official, original), "Files changed");
            }));
            tests.Add(new KeyValuePair<string, Action<AppPaths>>("Skip a safety file: do not proceed", p => {
                var fake = new FakeCopy { OnCopy = (n, from, to) => { File.Copy(Path.Combine(from, "config.toml"), Path.Combine(to, "config.toml")); return true; } };
                Assert(!Switch(p, fake, ConfigMode.Transit).Success && fake.Calls == 1, "Incomplete safety copy allowed");
                Assert(ConfigFiles.Equal(p.Target, p.Official), "Active files changed");
            }));
            tests.Add(new KeyValuePair<string, Action<AppPaths>>("Cancel Official overwrite: keep active files and independent safety copy", p => {
                string[] latest = ConfigFiles.Fingerprint(p.Target);
                var fake = new FakeCopy { OnCopy = (n, from, to) => { if (n == 2) return false; Copy(from, to); return true; } };
                Assert(!Switch(p, fake, ConfigMode.Transit).Success && fake.Calls == 2, "Did not stop");
                Assert(ConfigFiles.Matches(p.Target, latest), "Current files overwritten after cancellation");
                Assert(ConfigFiles.Matches(Directory.GetDirectories(Path.Combine(p.Official, "SafetyBackup")).Single(), latest), "Safety copy missing");
            }));
            tests.Add(new KeyValuePair<string, Action<AppPaths>>("Skip latest Official backup: do not apply Transit", p => {
                File.AppendAllText(Path.Combine(p.Target, "config.toml"), "# latest\r\n");
                var fake = new FakeCopy { OnCopy = (n, from, to) => { if (n != 2) Copy(from, to); return true; } };
                Assert(!Switch(p, fake, ConfigMode.Transit).Success && fake.Calls == 2, "Skipped backup allowed");
                Assert(ConfigFiles.Detect(p) == ConfigMode.Official, "Transit was applied");
            }));
            tests.Add(new KeyValuePair<string, Action<AppPaths>>("Partial active overwrite is never reported as success", p => {
                var fake = new FakeCopy { OnCopy = (n, from, to) => { if (n == 3) File.Copy(Path.Combine(from, "config.toml"), Path.Combine(to, "config.toml"), true); else Copy(from, to); return true; } };
                Assert(!Switch(p, fake, ConfigMode.Transit).Success, "Partial copy reported success");
                Assert(ConfigFiles.Detect(p) == ConfigMode.Unknown, "Mixed configuration misidentified");
                Assert(ConfigFiles.LooksOfficial(p.Official), "Official backup was overwritten by Transit");
            }));
            tests.Add(new KeyValuePair<string, Action<AppPaths>>("Unknown complete profile can switch to Transit without overwriting Official", p => {
                File.WriteAllText(Path.Combine(p.Target, "config.toml"), "model_provider = 'unknown'\n");
                string[] original = ConfigFiles.Fingerprint(p.Official); string[] current = ConfigFiles.Fingerprint(p.Target);
                var fake = new FakeCopy();
                Assert(Switch(p, fake, ConfigMode.Transit).Success && fake.Calls == 1, "Unknown complete profile was rejected");
                string[] histories = Directory.GetDirectories(Path.Combine(p.Official, "SafetyBackup"));
                Assert(ConfigFiles.Matches(p.Official, original), "Official overwritten");
                Assert(histories.Length == 1 && ConfigFiles.Matches(histories[0], current), "Unknown profile was not retained");
                Assert(ConfigFiles.Equal(p.Target, p.Transit), "Transit was not applied");
            }));
            tests.Add(new KeyValuePair<string, Action<AppPaths>>("Unknown or mixed configuration can restore Official", p => {
                File.WriteAllText(Path.Combine(p.Target, "config.toml"), "model_provider = 'unknown'\n");
                Assert(Switch(p, new FakeCopy(), ConfigMode.Official).Success && ConfigFiles.Equal(p.Target, p.Official), "Restore failed");
            }));
            tests.Add(new KeyValuePair<string, Action<AppPaths>>("Running Codex: no copying, no backup mutation", p => {
                var fake = new FakeCopy();
                Assert(Throws(() => new SwitchService(p, fake, () => true).SwitchTo(ConfigMode.Transit, IntPtr.Zero, s => { })) && fake.Calls == 0, "Running guard failed");
            }));
            tests.Add(new KeyValuePair<string, Action<AppPaths>>("Codex restarting between stages stops the switch", p => {
                bool running = false;
                var fake = new FakeCopy { OnCopy = (n, from, to) => { Copy(from, to); running = true; return true; } };
                Assert(Throws(() => new SwitchService(p, fake, () => running).SwitchTo(ConfigMode.Transit, IntPtr.Zero, s => { })) && fake.Calls == 1, "Restart guard failed");
                Assert(ConfigFiles.Equal(p.Target, p.Official), "Active configuration changed");
            }));
            tests.Add(new KeyValuePair<string, Action<AppPaths>>("Missing Transit file: no backup or active mutations", p => {
                File.Delete(Path.Combine(p.Transit, "auth.json")); var fake = new FakeCopy();
                Assert(Throws(() => Switch(p, fake, ConfigMode.Transit)) && fake.Calls == 0, "Missing source allowed");
            }));
            tests.Add(new KeyValuePair<string, Action<AppPaths>>("Already official: refreshed tokens are never replaced by stale backup", p => {
                File.AppendAllText(Path.Combine(p.Target, "auth.json"), "\r\n");
                string[] latest = ConfigFiles.Fingerprint(p.Target); var fake = new FakeCopy();
                Assert(Switch(p, fake, ConfigMode.Official).Success && fake.Calls == 0, "Redundant restore ran");
                Assert(ConfigFiles.Matches(p.Target, latest), "Latest official tokens lost");
            }));
            tests.Add(new KeyValuePair<string, Action<AppPaths>>("Already transit: never copy Transit into Official", p => {
                Copy(p.Transit, p.Target); string[] original = ConfigFiles.Fingerprint(p.Official); var fake = new FakeCopy();
                Assert(Switch(p, fake, ConfigMode.Transit).Success && fake.Calls == 0 && ConfigFiles.Matches(p.Official, original), "Official changed");
            }));
            tests.Add(new KeyValuePair<string, Action<AppPaths>>("Identical Official and Transit folders are ambiguous", p => {
                Copy(p.Official, p.Transit); Assert(ConfigFiles.Detect(p) == ConfigMode.Unknown, "Ambiguity ignored");
            }));
            tests.Add(new KeyValuePair<string, Action<AppPaths>>("Identical Official and Transit files block a no-op relay switch", p => {
                Copy(p.Official, p.Transit); var fake = new FakeCopy();
                bool reminded = false;
                try { Switch(p, fake, ConfigMode.Transit); }
                catch (IOException ex) { reminded = ex.Message.Contains("完全相同"); }
                Assert(reminded && fake.Calls == 0, "Identical sources were not blocked with a reminder");
            }));
            tests.Add(new KeyValuePair<string, Action<AppPaths>>("Process names: actual Codex ChatGPT.exe is detected", p => {
                Assert(CodexProcesses.IsCodex("ChatGPT", @"C:\Program Files\WindowsApps\OpenAI.Codex_1\app\ChatGPT.exe"), "Desktop missed");
                Assert(CodexProcesses.IsCodex("codex", null), "CLI missed");
                Assert(!CodexProcesses.IsCodex("ChatGPTManager", null), "Tool detects itself");
                Assert(!CodexProcesses.IsCodex("ChatGPT", @"C:\Other\ChatGPT.exe"), "Other app blocked");
            }));
            tests.Add(new KeyValuePair<string, Action<AppPaths>>("Desktop launch uses the registered Codex app identity", p => {
                Assert(CodexDesktopControl.LaunchArguments ==
                    @"shell:AppsFolder\OpenAI.Codex_2p2nqsd0c76g0!App", "Unexpected desktop launch target");
            }));
            tests.Add(new KeyValuePair<string, Action<AppPaths>>("Desktop close targets only a visible Electron main window", p => {
                Assert(CodexDesktopControl.IsClosableDesktopWindow(true, "Chrome_WidgetWin_1"),
                    "Visible Codex window was not selected");
                Assert(!CodexDesktopControl.IsClosableDesktopWindow(false, "Chrome_WidgetWin_1"),
                    "Hidden helper window was selected");
                Assert(!CodexDesktopControl.IsClosableDesktopWindow(true, "Chrome_WidgetWin_0"),
                    "Non-main Electron window was selected");
            }));
            tests.Add(new KeyValuePair<string, Action<AppPaths>>("Background close targets Codex only", p => {
                Assert(CodexDesktopControl.IsBackgroundCodex("ChatGPT",
                    @"C:\Program Files\WindowsApps\OpenAI.Codex_1\app\ChatGPT.exe"), "Packaged desktop missed");
                Assert(CodexDesktopControl.IsBackgroundCodex("codex", null), "Codex CLI missed");
                Assert(CodexDesktopControl.IsBackgroundCodex("codex-code-mode-host", null), "Code mode host missed");
                Assert(!CodexDesktopControl.IsBackgroundCodex("ChatGPTManager", null), "Manager app targeted itself");
                Assert(!CodexDesktopControl.IsBackgroundCodex("ChatGPT", @"C:\Other\ChatGPT.exe"),
                    "Unrelated ChatGPT executable was targeted");
            }));
            tests.Add(new KeyValuePair<string, Action<AppPaths>>("Floating ball size and close behavior are constrained", p => {
                Assert(FloatingBallPreferences.NormalizeSize(80) == 80
                    && FloatingBallPreferences.NormalizeSize(104) == 104
                    && FloatingBallPreferences.NormalizeSize(128) == 128,
                    "Supported floating-ball size was changed");
                Assert(FloatingBallPreferences.NormalizeSize(40) == 80
                    && FloatingBallPreferences.NormalizeSize(52) == 104
                    && FloatingBallPreferences.NormalizeSize(64) == 128,
                    "Legacy floating-ball size was not doubled");
                Assert(FloatingBallPreferences.NormalizeSize(999) == 104,
                    "Invalid floating-ball size was not reset");
                Assert(FloatingBallPreferences.NormalizeEnabled(null),
                    "Legacy floating-ball preference was not preserved");
                Assert(!FloatingBallPreferences.NormalizeEnabled(false),
                    "Disabled floating-ball preference was not preserved");
                Assert(MainForm.ShouldShowFloatingBall(false, true)
                    && !MainForm.ShouldShowFloatingBall(true, true)
                    && !MainForm.ShouldShowFloatingBall(false, false),
                    "Floating ball was not limited to enabled background mode");
                Rectangle work = new Rectangle(100, 50, 1000, 700);
                Size ball = new Size(104, 104);
                Assert(FloatingBallForm.SnapToEdges(new Point(125, 300), ball, work, 32).X == 100,
                    "Floating ball did not snap to the left edge");
                Assert(FloatingBallForm.SnapToEdges(new Point(970, 300), ball, work, 32).X == 996,
                    "Floating ball did not snap to the right edge");
                Assert(FloatingBallForm.SnapToEdges(new Point(500, 70), ball, work, 32).Y == 50,
                    "Floating ball did not snap to the top edge");
                Assert(FloatingBallForm.SnapToEdges(new Point(500, 620), ball, work, 32).Y == 646,
                    "Floating ball did not snap to the bottom edge");
                Assert(FloatingBallForm.SnapToEdges(new Point(500, 300), ball, work, 32) == new Point(500, 300),
                    "Floating ball moved despite being outside the snap threshold");
                Assert(MainForm.ShouldOfferCloseChoice(System.Windows.Forms.CloseReason.UserClosing, false),
                    "Close button does not offer background or full exit choices");
                Assert(!MainForm.ShouldOfferCloseChoice(System.Windows.Forms.CloseReason.UserClosing, true),
                    "Explicit exit was redirected to the close-choice dialog");
                Assert(!MainForm.ShouldOfferCloseChoice(System.Windows.Forms.CloseReason.WindowsShutDown, false),
                    "Windows shutdown was incorrectly redirected to the close-choice dialog");
            }));
            tests.Add(new KeyValuePair<string, Action<AppPaths>>("Windows shell flags retain native overwrite confirmations", p => {
                Assert(WindowsConfirmedCopy.OperationFlags == 0, "Suppressed native dialogs");
            }));
            tests.Add(new KeyValuePair<string, Action<AppPaths>>("Actual Windows shell copies new files byte-for-byte", p => {
                string empty = Path.Combine(p.Root, "NativeCopyDestination"); Directory.CreateDirectory(empty);
                Assert(new WindowsConfirmedCopy().CopyPair(IntPtr.Zero, p.Official, empty, "Synthetic test"), "Native shell failed");
                Assert(ConfigFiles.Equal(p.Official, empty), "Native shell changed file bytes");
            }));
            tests.Add(new KeyValuePair<string, Action<AppPaths>>("Native Windows copy imports exact config and auth paths", p => {
                string configSource = Path.Combine(p.Root, "ExplicitConfigSource");
                string authSource = Path.Combine(p.Root, "ExplicitAuthSource");
                string destination = Path.Combine(p.Root, "ExplicitImportDestination");
                Directory.CreateDirectory(configSource); Directory.CreateDirectory(authSource); Directory.CreateDirectory(destination);
                File.Copy(Path.Combine(p.Official, "config.toml"), Path.Combine(configSource, "config.toml"));
                File.Copy(Path.Combine(p.Official, "auth.json"), Path.Combine(authSource, "auth.json"));
                Assert(new WindowsConfirmedCopy().CopyFiles(IntPtr.Zero,
                    Path.Combine(configSource, "config.toml"), Path.Combine(authSource, "auth.json"), destination, "Explicit path test"),
                    "Explicit path import failed");
                Assert(ConfigFiles.Equal(p.Official, destination), "Explicit path import changed file bytes");
            }));
            tests.Add(new KeyValuePair<string, Action<AppPaths>>("Unknown profile snapshots exact config and auth files without path confirmation", p => {
                var fresh = new AppPaths(Path.Combine(p.Root, "snapshot-unknown"), Path.Combine(p.Root, "profile-unknown"));
                Directory.CreateDirectory(fresh.Root);
                Directory.CreateDirectory(fresh.Target);
                File.WriteAllText(Path.Combine(fresh.Target, "config.toml"), "model_provider = 'custom'\n# unrecognized profile\n");
                File.WriteAllText(Path.Combine(fresh.Target, "auth.json"), "{\"auth_mode\":\"custom\"}");
                string[] original = ConfigFiles.Fingerprint(fresh.Target);
                ProfileSnapshotResult result = InitialProfileSnapshotService.SaveBeforeTransit(fresh, delegate { return false; });
                Assert(result.SavedToOfficial && ConfigFiles.Matches(fresh.Target, original)
                    && ConfigFiles.Matches(fresh.Official, original), "Unknown profile was not saved byte-for-byte");
            }));
            tests.Add(new KeyValuePair<string, Action<AppPaths>>("Existing Official pair is preserved while unknown profile is saved to history", p => {
                var fresh = new AppPaths(Path.Combine(p.Root, "snapshot-existing"), Path.Combine(p.Root, "profile-existing"));
                Directory.CreateDirectory(fresh.Root);
                Directory.CreateDirectory(fresh.Target);
                Copy(p.Official, fresh.Official);
                File.WriteAllText(Path.Combine(fresh.Target, "config.toml"), "model_provider = 'custom'\n");
                File.WriteAllText(Path.Combine(fresh.Target, "auth.json"), "{\"auth_mode\":\"custom\"}");
                string[] official = ConfigFiles.Fingerprint(fresh.Official);
                string[] current = ConfigFiles.Fingerprint(fresh.Target);
                ProfileSnapshotResult result = InitialProfileSnapshotService.SaveBeforeTransit(fresh, delegate { return false; });
                string history = Directory.GetDirectories(Path.Combine(fresh.Official, "SafetyBackup")).Single();
                Assert(result.SavedToHistory && ConfigFiles.Matches(fresh.Official, official)
                    && ConfigFiles.Matches(history, current), "Existing Official was overwritten or history is incomplete");
            }));
            tests.Add(new KeyValuePair<string, Action<AppPaths>>("Partial active profile is rejected without writing Official", p => {
                var fresh = new AppPaths(Path.Combine(p.Root, "snapshot-partial"), Path.Combine(p.Root, "profile-partial"));
                Directory.CreateDirectory(fresh.Root);
                Directory.CreateDirectory(fresh.Target);
                Directory.CreateDirectory(fresh.Official);
                File.WriteAllText(Path.Combine(fresh.Target, "config.toml"), "partial");
                Assert(Throws(() => InitialProfileSnapshotService.SaveBeforeTransit(fresh, delegate { return false; })),
                    "A partial active pair was accepted");
                Assert(Directory.GetFileSystemEntries(fresh.Official).Length == 0, "Partial profile wrote to Official");
            }));
            tests.Add(new KeyValuePair<string, Action<AppPaths>>("Missing active profile needs no import or confirmation", p => {
                var fresh = new AppPaths(Path.Combine(p.Root, "snapshot-empty"), Path.Combine(p.Root, "profile-empty"));
                Directory.CreateDirectory(fresh.Root);
                ProfileSnapshotResult result = InitialProfileSnapshotService.SaveBeforeTransit(fresh, delegate { return false; });
                Assert(!result.SavedToOfficial && !result.SavedToHistory, "An empty profile unexpectedly created an Official snapshot");
            }));
            tests.Add(new KeyValuePair<string, Action<AppPaths>>("First Transit switch accepts an unrecognized profile and preserves it", p => {
                var fresh = new AppPaths(Path.Combine(p.Root, "first-transit"), Path.Combine(p.Root, "first-profile"));
                Directory.CreateDirectory(fresh.Root);
                Directory.CreateDirectory(fresh.Transit);
                Copy(p.Transit, fresh.Transit);
                Directory.CreateDirectory(fresh.Target);
                File.WriteAllText(Path.Combine(fresh.Target, "config.toml"), "model_provider = 'custom'\n");
                File.WriteAllText(Path.Combine(fresh.Target, "auth.json"), "{\"auth_mode\":\"custom\"}");
                string[] original = ConfigFiles.Fingerprint(fresh.Target);
                var copier = new FakeCopy();
                SwitchResult result = Switch(fresh, copier, ConfigMode.Transit);
                Assert(result.Success && copier.Calls == 1 && ConfigFiles.Matches(fresh.Official, original)
                    && ConfigFiles.Equal(fresh.Target, fresh.Transit), "First Transit switch rejected or lost the active profile");
                Assert(TransitActivationState.IsActiveOrDetected(fresh, ConfigMode.Unknown), "Transit activation was not recorded");
            }));
            tests.Add(new KeyValuePair<string, Action<AppPaths>>("First Transit switch creates a missing target directory", p => {
                var fresh = new AppPaths(Path.Combine(p.Root, "first-empty"), Path.Combine(p.Root, "profile-empty-target"));
                Directory.CreateDirectory(fresh.Root);
                Directory.CreateDirectory(fresh.Transit);
                Copy(p.Transit, fresh.Transit);
                var copier = new FakeCopy();
                Assert(Switch(fresh, copier, ConfigMode.Transit).Success
                    && Directory.Exists(fresh.Target) && ConfigFiles.Equal(fresh.Target, fresh.Transit),
                    "Missing target was not created and initialized");
            }));

            tests.Add(new KeyValuePair<string, Action<AppPaths>>("Transit key save preserves default config byte-for-byte", p => {
                string configPath = Path.Combine(p.Transit, "config.toml");
                string[] expectedConfig = ConfigFiles.Fingerprint(p.Transit);
                File.WriteAllText(Path.Combine(p.Transit, "auth.json"), "{\"OPENAI_API_KEY\":\"\"}");
                Assert(!TransitCredentialStore.IsConfigured(p.Transit), "Blank key was reported as configured");
                TransitCredentialStore.SaveApiKey(p.Transit, "FAKE-TEST-API-KEY");
                Assert(TransitCredentialStore.IsConfigured(p.Transit), "Saved key was not recognized");
                Assert(String.Equals(ConfigFiles.Hash(configPath), expectedConfig[0], StringComparison.Ordinal),
                    "Saving a key modified the preconfigured TOML");
            }));
            tests.Add(new KeyValuePair<string, Action<AppPaths>>("Transit key save preserves other auth metadata", p => {
                File.WriteAllText(Path.Combine(p.Transit, "auth.json"),
                    "{\"auth_mode\":\"apikey\",\"metadata\":{\"keep\":true},\"OPENAI_API_KEY\":\"OLD-FAKE-KEY\"}");
                TransitCredentialStore.SaveApiKey(p.Transit, "NEW-FAKE-API-KEY");
                var auth = new System.Web.Script.Serialization.JavaScriptSerializer()
                    .DeserializeObject(File.ReadAllText(Path.Combine(p.Transit, "auth.json"))) as Dictionary<string, object>;
                object mode, metadata, key;
                Assert(auth != null && auth.TryGetValue("auth_mode", out mode) && Convert.ToString(mode) == "apikey",
                    "Existing auth mode was discarded");
                Assert(auth.TryGetValue("metadata", out metadata) && metadata != null,
                    "Existing auth metadata was discarded");
                Assert(auth.TryGetValue("OPENAI_API_KEY", out key) && Convert.ToString(key) == "NEW-FAKE-API-KEY",
                    "Key rotation did not replace the old value");
                string savedHash = ConfigFiles.Hash(Path.Combine(p.Transit, "auth.json"));
                Assert(!TransitCredentialStore.SaveApiKey(p.Transit, "NEW-FAKE-API-KEY")
                    && ConfigFiles.Hash(Path.Combine(p.Transit, "auth.json")) == savedHash,
                    "Saving the same API key unnecessarily rewrote auth.json");
            }));
            tests.Add(new KeyValuePair<string, Action<AppPaths>>("Transit key replacement synchronizes active Codex auth without copying config", p => {
                Copy(p.Transit, p.Target);
                TransitCredentialStore.SaveApiKey(p.Transit, "FAKE-ROTATED-ACTIVE-KEY");
                string[] official = ConfigFiles.Fingerprint(p.Official);
                string[] targetConfig = ConfigFiles.Fingerprint(p.Target);
                var copier = new FakeCopy();
                SwitchResult result = Switch(p, copier, ConfigMode.Transit);
                Assert(result.Success && result.Message.Contains("新密钥同步") && copier.Calls == 0,
                    "Switching to the already-active relay did not synchronize its new key");
                var transitAuth = new System.Web.Script.Serialization.JavaScriptSerializer()
                    .DeserializeObject(File.ReadAllText(Path.Combine(p.Transit, "auth.json"))) as Dictionary<string, object>;
                var activeAuth = new System.Web.Script.Serialization.JavaScriptSerializer()
                    .DeserializeObject(File.ReadAllText(Path.Combine(p.Target, "auth.json"))) as Dictionary<string, object>;
                object transitKey, activeKey;
                Assert(transitAuth != null && activeAuth != null
                    && transitAuth.TryGetValue("OPENAI_API_KEY", out transitKey)
                    && activeAuth.TryGetValue("OPENAI_API_KEY", out activeKey)
                    && Convert.ToString(transitKey) == Convert.ToString(activeKey),
                    "Active Codex auth did not receive the replacement key");
                Assert(ConfigFiles.Hash(Path.Combine(p.Target, "config.toml")) == targetConfig[0],
                    "Key synchronization rewrote active config.toml");
                Assert(ConfigFiles.Matches(p.Official, official), "Key synchronization changed Official");
            }));
            tests.Add(new KeyValuePair<string, Action<AppPaths>>("Blank or whitespace-containing Transit keys are rejected", p => {
                File.WriteAllText(Path.Combine(p.Transit, "auth.json"), "{\"OPENAI_API_KEY\":\"\"}");
                string original = ConfigFiles.Hash(Path.Combine(p.Transit, "auth.json"));
                Assert(Throws(() => TransitCredentialStore.SaveApiKey(p.Transit, "  ")), "Blank key was accepted");
                Assert(Throws(() => TransitCredentialStore.SaveApiKey(p.Transit, "FAKE KEY")), "Whitespace in key was accepted");
                var copier = new FakeCopy();
                Assert(Throws(() => Switch(p, copier, ConfigMode.Transit)) && copier.Calls == 0,
                    "Switch accepted an unset Transit key");
                Assert(String.Equals(original, ConfigFiles.Hash(Path.Combine(p.Transit, "auth.json")), StringComparison.Ordinal),
                    "Rejected key input modified auth.json");
            }));

            tests.Add(new KeyValuePair<string, Action<AppPaths>>("Transit remains recognized after Codex adds unrelated settings", p => {
                Copy(p.Transit, p.Target);
                File.AppendAllText(Path.Combine(p.Target, "config.toml"), "\n[desktop]\nexample = true\n");
                string[] original = ConfigFiles.Fingerprint(p.Target);
                Assert(ConfigFiles.Detect(p) == ConfigMode.Transit && ConfigFiles.ModeName(ConfigFiles.Detect(p)) == "中转", "Transit status lost");
                Assert(ConfigFiles.Matches(p.Target, original), "Detection edited configuration");
            }));
            tests.Add(new KeyValuePair<string, Action<AppPaths>>("Transit API-key metadata and key rotation do not lose mode", p => {
                Copy(p.Transit, p.Target);
                File.WriteAllText(Path.Combine(p.Target, "auth.json"), "{\n  \"auth_mode\": \"apikey\",\n  \"tokens\": null,\n  \"OPENAI_API_KEY\": \"FAKE-ROTATED-KEY\"\n}\n");
                Assert(ConfigFiles.Detect(p) == ConfigMode.Transit, "Auth metadata broke mode detection");
            }));
            tests.Add(new KeyValuePair<string, Action<AppPaths>>("Unrecognized relay endpoint remains unknown", p => {
                Copy(p.Transit, p.Target);
                string file = Path.Combine(p.Target, "config.toml");
                File.WriteAllText(file, File.ReadAllText(file).Replace("example.invalid", "different.invalid"));
                Assert(ConfigFiles.Detect(p) == ConfigMode.Unknown, "Unrecognized endpoint accepted");
            }));
            tests.Add(new KeyValuePair<string, Action<AppPaths>>("Different selected provider remains unknown", p => {
                Copy(p.Transit, p.Target);
                string file = Path.Combine(p.Target, "config.toml");
                File.WriteAllText(file, File.ReadAllText(file).Replace("model_provider = 'custom'", "model_provider = 'other'"));
                Assert(ConfigFiles.Detect(p) == ConfigMode.Unknown, "Different provider accepted");
            }));
            tests.Add(new KeyValuePair<string, Action<AppPaths>>("Mixed complete profile can switch after being preserved", p => {
                File.Copy(Path.Combine(p.Transit, "config.toml"), Path.Combine(p.Target, "config.toml"), true);
                Assert(ConfigFiles.Detect(p) == ConfigMode.Unknown, "Mixed official auth accepted");
                string[] original = ConfigFiles.Fingerprint(p.Target);
                string[] official = ConfigFiles.Fingerprint(p.Official);
                var fake = new FakeCopy();
                Assert(Switch(p, fake, ConfigMode.Transit).Success && fake.Calls == 1,
                    "Mixed complete profile was rejected");
                string history = Directory.GetDirectories(Path.Combine(p.Official, "SafetyBackup")).Single();
                Assert(ConfigFiles.Matches(history, original) && ConfigFiles.Matches(p.Official, official),
                    "Mixed state was not retained separately from Official");
            }));
            tests.Add(new KeyValuePair<string, Action<AppPaths>>("Transit with empty API key remains unknown", p => {
                Copy(p.Transit, p.Target);
                File.WriteAllText(Path.Combine(p.Target, "auth.json"), "{\"OPENAI_API_KEY\":\"\"}");
                Assert(ConfigFiles.Detect(p) == ConfigMode.Unknown, "Missing login accepted");
            }));
            tests.Add(new KeyValuePair<string, Action<AppPaths>>("Changed transit metadata never triggers an Official backup", p => {
                Copy(p.Transit, p.Target); string[] original = ConfigFiles.Fingerprint(p.Official);
                File.AppendAllText(Path.Combine(p.Target, "config.toml"), "\n# Written by Codex\n");
                var fake = new FakeCopy();
                Assert(Switch(p, fake, ConfigMode.Transit).Success && fake.Calls == 0, "Already-transit state copied files");
                Assert(ConfigFiles.Matches(p.Official, original), "Transit overwrote Official");
            }));
            tests.Add(new KeyValuePair<string, Action<AppPaths>>("Transit recognition supports quoted tables, comments and CRLF", p => {
                Copy(p.Transit, p.Target);
                File.WriteAllText(Path.Combine(p.Target, "config.toml"), "# fake\r\nmodel_provider = \"custom\" # same route\r\n[features]\r\nexample = true\r\n[model_providers.\"custom\"] # same provider\r\nbase_url = \"https://example.invalid/v1\" # same endpoint\r\n");
                Assert(ConfigFiles.Detect(p) == ConfigMode.Transit, "Equivalent TOML syntax lost transit mode");
            }));
            tests.Add(new KeyValuePair<string, Action<AppPaths>>("Companion duration uses the local clock and Beijing start time", p => {
                string expected = "已陪伴用户 2 天 11 小时 37 分钟";
                Assert(MainForm.FormatCompanionDuration(
                    new DateTimeOffset(2026, 10, 24, 9, 37, 42, TimeSpan.FromHours(8))) == expected,
                    "Elapsed days and hours were calculated incorrectly");
                Assert(MainForm.FormatCompanionDuration(
                    new DateTimeOffset(2026, 10, 24, 1, 37, 42, TimeSpan.Zero)) == expected,
                    "Equivalent local time zones produced different durations");
                Assert(MainForm.FormatCompanionDuration(
                    new DateTimeOffset(2026, 10, 21, 21, 59, 0, TimeSpan.FromHours(8)))
                    == "已陪伴用户 0 天 0 小时 0 分钟",
                    "A clock before the start time produced a negative duration");
            }));

            int index = 0;
            foreach (var test in tests)
            {
                index++;
                try { test.Value(Fixture(Path.Combine(runRoot, index.ToString("00")))); passed++; report.Add("PASS " + test.Key); }
                catch (Exception ex) { failed++; report.Add("FAIL " + test.Key + ": " + ex.Message); }
            }
            report.Insert(0, "Passed=" + passed + "; Failed=" + failed + "; Total=" + tests.Count);
            report.Insert(1, "Synthetic fixtures only. Run folder: " + runRoot);
            File.WriteAllLines(fullReport, report, new UTF8Encoding(true));
            return failed == 0 ? 0 : 1;
        }
    }
}
