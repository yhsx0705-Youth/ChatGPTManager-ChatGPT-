using System;
using System.IO;

namespace ChatGPTManager
{
    internal static class UninstallRestoreTests
    {
        private sealed class FakeCopy : IConfirmedCopy
        {
            public int Calls;
            public bool Cancel;
            public bool Corrupt;

            public bool CopyPair(IntPtr owner, string from, string to, string title)
            {
                Calls++;
                if (Cancel) return false;
                Directory.CreateDirectory(to);
                foreach (string name in ConfigFiles.Names)
                    File.Copy(Path.Combine(from, name), Path.Combine(to, name), true);
                if (Corrupt) File.WriteAllText(Path.Combine(to, "config.toml"), "incomplete");
                return true;
            }
        }

        private static void WriteOfficial(string folder, string marker)
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "config.toml"), "model_provider = \"openai\"\n# " + marker + "\n");
            File.WriteAllText(Path.Combine(folder, "auth.json"), "{\"auth_mode\":\"chatgpt\",\"tokens\":{\"access_token\":\"TEST-ONLY\"}}");
        }

        private static void WriteTransit(string folder)
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "config.toml"), "model_provider = 'custom'\n[model_providers.custom]\nbase_url = 'https://relay.invalid/v1'\n");
            File.WriteAllText(Path.Combine(folder, "auth.json"), "{\"OPENAI_API_KEY\":\"TEST-ONLY\"}");
        }

        private static AppPaths CreateUnconfiguredInstallation(string fixture)
        {
            var paths = new AppPaths(Path.Combine(fixture, "install"), Path.Combine(fixture, "profile"));
            Directory.CreateDirectory(Path.Combine(paths.Root, "APP"));
            Directory.CreateDirectory(paths.Official);
            Directory.CreateDirectory(Path.Combine(paths.Official, "SafetyBackup"));
            Directory.CreateDirectory(paths.Transit);
            string packaging = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "packaging"));
            File.Copy(Path.Combine(packaging, "Transit-config.toml"), Path.Combine(paths.Transit, "config.toml"));
            File.Copy(Path.Combine(packaging, "Transit-auth.template.json"), Path.Combine(paths.Transit, "auth.json"));
            return paths;
        }

        private static void Assert(bool value, string message)
        {
            if (!value) throw new Exception(message);
        }

        private static int Run()
        {
            string root = Path.Combine(Path.GetTempPath(), "ChatGPTManagerRestoreTest-" + Guid.NewGuid().ToString("N"));
            int passed = 0;
            try
            {
                Directory.CreateDirectory(root);
                Action<string, Action> test = delegate(string name, Action action)
                {
                    action();
                    passed++;
                    Console.WriteLine("PASS " + name);
                };

                test("current latest official pair is left untouched", delegate {
                    string fixture = Path.Combine(root, "current-official");
                    var paths = new AppPaths(Path.Combine(fixture, "install"), Path.Combine(fixture, "profile"));
                    WriteOfficial(paths.Official, "old");
                    WriteOfficial(paths.Target, "latest");
                    var copier = new FakeCopy();
                    RestoreResult result = RestoreOfficialService.BeforeUninstall(paths, copier, delegate { return false; });
                    Assert(result.Success && copier.Calls == 0, "Current official profile was copied over.");
                    Assert(File.ReadAllText(Path.Combine(paths.Target, "config.toml")).Contains("latest"), "Latest profile changed.");
                });

                test("active Transit with a saved key blocks uninstall", delegate {
                    string fixture = Path.Combine(root, "transit-restore");
                    var paths = new AppPaths(Path.Combine(fixture, "install"), Path.Combine(fixture, "profile"));
                    WriteOfficial(paths.Official, "saved-official");
                    WriteTransit(paths.Target);
                    Directory.CreateDirectory(paths.Transit);
                    File.WriteAllText(Path.Combine(paths.Transit, "config.toml"), File.ReadAllText(Path.Combine(paths.Target, "config.toml")));
                    File.WriteAllText(Path.Combine(paths.Transit, "auth.json"), File.ReadAllText(Path.Combine(paths.Target, "auth.json")));
                    string[] activeTransit = ConfigFiles.Fingerprint(paths.Target);
                    var copier = new FakeCopy();
                    RestoreResult result = RestoreOfficialService.BeforeUninstall(paths, copier, delegate { return false; });
                    Assert(!result.Success && copier.Calls == 0 && ConfigFiles.Matches(paths.Target, activeTransit)
                        && result.Message.Contains("禁止卸载"), "Active Transit uninstall was not blocked with a reminder.");
                });

                test("direct uninstall copy restores official files byte-for-byte", delegate {
                    string fixture = Path.Combine(root, "direct-uninstall-copy");
                    var paths = new AppPaths(Path.Combine(fixture, "install"), Path.Combine(fixture, "profile"));
                    WriteOfficial(paths.Official, "saved-official");
                    WriteTransit(paths.Target);
                    string[] expected = ConfigFiles.Fingerprint(paths.Official);
                    bool copied = new DirectUninstallCopy().CopyPair(IntPtr.Zero, paths.Official, paths.Target, "test");
                    Assert(copied && ConfigFiles.Matches(paths.Target, expected), "Direct uninstall restore changed official files.");
                });

                test("valid safety backup is used when Official is invalid", delegate {
                    string fixture = Path.Combine(root, "backup-fallback");
                    var paths = new AppPaths(Path.Combine(fixture, "install"), Path.Combine(fixture, "profile"));
                    Directory.CreateDirectory(paths.Official);
                    File.WriteAllText(Path.Combine(paths.Official, "config.toml"), "not official");
                    File.WriteAllText(Path.Combine(paths.Official, "auth.json"), "{}");
                    string backup = Path.Combine(paths.Official, "SafetyBackup", "Official-test");
                    WriteOfficial(backup, "backup-official");
                    WriteTransit(paths.Target);
                    var copier = new FakeCopy();
                    RestoreResult result = RestoreOfficialService.BeforeUninstall(paths, copier, delegate { return false; });
                    Assert(result.Success && ConfigFiles.Equal(paths.Target, backup), "Safety backup was not restored.");
                });

                test("saved key without an active Transit switch can uninstall", delegate {
                    string fixture = Path.Combine(root, "missing-source");
                    var paths = new AppPaths(Path.Combine(fixture, "install"), Path.Combine(fixture, "profile"));
                    CreateUnconfiguredInstallation(fixture);
                    File.WriteAllText(Path.Combine(paths.Transit, "auth.json"), "{\"OPENAI_API_KEY\":\"TEST-ONLY\"}");
                    var copier = new FakeCopy();
                    RestoreResult result = RestoreOfficialService.BeforeUninstall(paths, copier, delegate { return false; });
                    Assert(result.Success && copier.Calls == 0, "A saved but unused key incorrectly blocked uninstall.");
                });

                test("active marker blocks uninstall when the current profile becomes unknown", delegate {
                    string fixture = Path.Combine(root, "active-marker");
                    var paths = CreateUnconfiguredInstallation(fixture);
                    File.WriteAllText(Path.Combine(paths.Transit, "auth.json"), "{\"OPENAI_API_KEY\":\"TEST-ONLY\"}");
                    Directory.CreateDirectory(paths.Target);
                    File.WriteAllText(Path.Combine(paths.Target, "config.toml"), "model_provider = 'unknown'\n");
                    File.WriteAllText(Path.Combine(paths.Target, "auth.json"), "{\"metadata\":{}}");
                    TransitActivationState.SetActive(paths, true);
                    File.WriteAllText(Path.Combine(paths.Root, "APP", "transit-active.state"), "corrupted marker");
                    RestoreResult result = RestoreOfficialService.BeforeUninstall(paths,
                        new FakeCopy(), delegate { return false; });
                    Assert(!result.Success && result.Message.Contains("禁止卸载"),
                        "The saved activation marker did not block an unknown active profile.");
                });

                test("switching back to Official permits uninstall even when a key remains saved", delegate {
                    string fixture = Path.Combine(root, "official-after-transit");
                    var paths = CreateUnconfiguredInstallation(fixture);
                    WriteOfficial(paths.Official, "saved-official");
                    WriteOfficial(paths.Target, "restored-official");
                    File.WriteAllText(Path.Combine(paths.Transit, "auth.json"), "{\"OPENAI_API_KEY\":\"TEST-ONLY\"}");
                    TransitActivationState.SetActive(paths, true);
                    RestoreResult result = RestoreOfficialService.BeforeUninstall(paths,
                        new FakeCopy(), delegate { return false; });
                    Assert(result.Success && !File.Exists(Path.Combine(paths.Root, "APP", "transit-active.state")),
                        "An official active profile remained blocked by a stale Transit marker.");
                });

                test("unconfigured install uninstalls without changing the current profile", delegate {
                    string fixture = Path.Combine(root, "unconfigured-install");
                    var paths = CreateUnconfiguredInstallation(fixture);
                    WriteTransit(paths.Target);
                    string[] original = ConfigFiles.Fingerprint(paths.Target);
                    var copier = new FakeCopy();
                    RestoreResult result = RestoreOfficialService.BeforeUninstall(paths, copier, delegate { return false; });
                    Assert(result.Success && copier.Calls == 0, "Incomplete installation did not allow direct cleanup.");
                    Assert(ConfigFiles.Matches(paths.Target, original), "Direct cleanup changed the current profile.");

                    string installer = Path.Combine(paths.Root, "APP", "ChatGPTManagerSetup.exe");
                    string uninstaller = Path.Combine(paths.Root, "APP", "Uninstall.exe");
                    File.WriteAllText(installer, "setup package");
                    File.WriteAllText(uninstaller, "uninstaller");
                    UninstallProfileCleanup.Clear(paths, installer, uninstaller);
                    UninstallProfileCleanup.RemoveUninstallerAfterExit(paths, uninstaller);
                    string[] remaining = Directory.GetFileSystemEntries(Path.Combine(paths.Root, "APP"));
                    Assert(remaining.Length == 1 && String.Equals(remaining[0], installer, StringComparison.OrdinalIgnoreCase),
                        "Incomplete installation cleanup did not leave only the setup package.");
                    Assert(ConfigFiles.Matches(paths.Target, original), "Cleanup changed the current profile.");
                });

                test("no-key uninstall is allowed without a verifiable Official source", delegate {
                    string fixture = Path.Combine(root, "unconfigured-partial-official");
                    var paths = CreateUnconfiguredInstallation(fixture);
                    File.WriteAllText(Path.Combine(paths.Official, "config.toml"), "partial import");
                    RestoreResult result = RestoreOfficialService.BeforeUninstall(paths,
                        new FakeCopy(), delegate { return false; });
                    Assert(result.Success, "No-key uninstall was blocked by an incomplete Official folder.");
                });

                test("saving a key without switching does not block uninstall", delegate {
                    string fixture = Path.Combine(root, "unconfigured-with-key");
                    var paths = CreateUnconfiguredInstallation(fixture);
                    File.WriteAllText(Path.Combine(paths.Transit, "auth.json"), "{\"OPENAI_API_KEY\":\"TEST-ONLY\"}");
                    RestoreResult result = RestoreOfficialService.BeforeUninstall(paths,
                        new FakeCopy(), delegate { return false; });
                    Assert(result.Success, "A saved key without a Transit switch blocked uninstall.");
                });

                test("no-key uninstall does not depend on the default Transit config hash", delegate {
                    string fixture = Path.Combine(root, "unconfigured-custom-transit");
                    var paths = CreateUnconfiguredInstallation(fixture);
                    File.AppendAllText(Path.Combine(paths.Transit, "config.toml"), "\n# local change\n");
                    RestoreResult result = RestoreOfficialService.BeforeUninstall(paths,
                        new FakeCopy(), delegate { return false; });
                    Assert(result.Success, "No-key uninstall was blocked by a local default config change.");
                });

                test("running Codex blocks restore", delegate {
                    string fixture = Path.Combine(root, "running-codex");
                    var paths = new AppPaths(Path.Combine(fixture, "install"), Path.Combine(fixture, "profile"));
                    WriteOfficial(paths.Official, "saved-official");
                    WriteTransit(paths.Target);
                    var copier = new FakeCopy();
                    RestoreResult result = RestoreOfficialService.BeforeUninstall(paths, copier, delegate { return true; });
                    Assert(!result.Success && copier.Calls == 0, "Restore ran while Codex was active.");
                });

                test("cancelled restore blocks uninstall", delegate {
                    string fixture = Path.Combine(root, "cancelled");
                    var paths = new AppPaths(Path.Combine(fixture, "install"), Path.Combine(fixture, "profile"));
                    WriteOfficial(paths.Official, "saved-official");
                    WriteTransit(paths.Target);
                    string[] original = ConfigFiles.Fingerprint(paths.Target);
                    var copier = new FakeCopy { Cancel = true };
                    RestoreResult result = RestoreOfficialService.BeforeUninstall(paths, copier, delegate { return false; });
                    Assert(!result.Success && copier.Calls == 1 && ConfigFiles.Matches(paths.Target, original), "Cancelled restore was not rejected.");
                });

                test("post-copy mismatch blocks uninstall", delegate {
                    string fixture = Path.Combine(root, "mismatch");
                    var paths = new AppPaths(Path.Combine(fixture, "install"), Path.Combine(fixture, "profile"));
                    WriteOfficial(paths.Official, "saved-official");
                    WriteTransit(paths.Target);
                    var copier = new FakeCopy { Corrupt = true };
                    RestoreResult result = RestoreOfficialService.BeforeUninstall(paths, copier, delegate { return false; });
                    Assert(!result.Success && copier.Calls == 1, "Mismatched restored data was accepted.");
                });

                test("missing target directory is recreated", delegate {
                    string fixture = Path.Combine(root, "missing-target");
                    var paths = new AppPaths(Path.Combine(fixture, "install"), Path.Combine(fixture, "profile"));
                    WriteOfficial(paths.Official, "saved-official");
                    var copier = new FakeCopy();
                    RestoreResult result = RestoreOfficialService.BeforeUninstall(paths, copier, delegate { return false; });
                    Assert(result.Success && Directory.Exists(paths.Target) && ConfigFiles.Equal(paths.Target, paths.Official), "Missing target was not restored.");
                });

                test("install path must be a non-root ASCII path", delegate {
                    Assert(InstallerPathPolicy.IsValid(@"C:\ChatGPT Manager"), "Default installation path was rejected.");
                    Assert(InstallerPathPolicy.IsValid(@"E:\ChatGPT Manager"), "ASCII installation path on E: was rejected.");
                    string firstInstallPath = @"E:\ChatGPT Manager - First Install - " + Guid.NewGuid().ToString("N");
                    Assert(!Directory.Exists(firstInstallPath) && InstallerPathPolicy.IsValid(firstInstallPath),
                        "A valid E: destination that does not exist yet was rejected.");
                    Assert(InstallerPathPolicy.IsValid(root), "An existing installation folder was rejected.");
                    Assert(InstallerPathPolicy.IsValid(@"C:\Program Files\ChatGPT Manager"), "ASCII path with spaces was rejected.");
                    Assert(!InstallerPathPolicy.IsValid(@"C:\Users\用户\ChatGPT Manager"), "Non-ASCII path was accepted.");
                    Assert(!InstallerPathPolicy.IsValid(@"C:\"), "Drive root was accepted.");
                    Assert(!InstallerPathPolicy.IsValid("relative path"), "Relative path was accepted.");
                    Assert(!InstallerPathPolicy.IsValid(@"C:ChatGPT Manager"), "Drive-relative path was accepted.");
                    Assert(!InstallerPathPolicy.IsValid(@"C:\Codex|Switch"), "Invalid path characters were accepted.");
                    string uninstallExe = Path.Combine(root, "APP", "Uninstall.exe");
                    Assert(UninstallInvocationPolicy.IsExpectedInstallRoot(root, uninstallExe),
                        "The uninstaller rejected its own installation folder.");
                    Assert(!UninstallInvocationPolicy.IsExpectedInstallRoot(Path.Combine(root, "other"), uninstallExe),
                        "The uninstaller accepted a different cleanup target.");
                    Assert(!UninstallInvocationPolicy.IsExpectedInstallRoot(root, Path.Combine(root, "other.exe")),
                        "A non-uninstaller parent executable was accepted.");
                });

                test("uninstall clears everything but a setup package", delegate {
                    string fixture = Path.Combine(root, "cleanup");
                    var paths = new AppPaths(Path.Combine(fixture, "install"), Path.Combine(fixture, "profile"));
                    WriteOfficial(paths.Official, "saved-official");
                    WriteTransit(paths.Transit);
                    WriteTransit(paths.Target);
                    File.WriteAllText(Path.Combine(paths.Target, "config.toml"), "model_provider = 'unknown'\n");
                    string[] expectedOfficial = ConfigFiles.Fingerprint(paths.Official);
                    RestoreResult restore = RestoreOfficialService.BeforeUninstall(paths,
                        new DirectUninstallCopy(), delegate { return false; });
                    Assert(restore.Success && ConfigFiles.Matches(paths.Target, expectedOfficial),
                        "Official configuration was not restored before cleanup.");

                    string appSource = Path.Combine(paths.Root, "APP", "src", "Program.cs");
                    Directory.CreateDirectory(Path.GetDirectoryName(appSource));
                    File.WriteAllText(appSource, "development source");
                    string appTool = Path.Combine(paths.Root, "APP", "Tools", "RemoveMe.EXE");
                    Directory.CreateDirectory(Path.GetDirectoryName(appTool));
                    File.WriteAllText(appTool, "tool executable");
                    File.WriteAllText(Path.Combine(paths.Root, "APP", "Tools", "README.txt"), "tool data");
                    string uninstaller = Path.Combine(paths.Root, "APP", "Uninstall.exe");
                    File.WriteAllText(uninstaller, "active uninstaller");
                    string appExecutable = Path.Combine(paths.Root, "APP", "ChatGPTManager.exe");
                    File.WriteAllText(appExecutable, "application executable");
                    string installer = Path.Combine(paths.Root, "APP", "ChatGPTManagerSetup.exe");
                    File.WriteAllText(installer, "setup executable");
                    File.WriteAllText(Path.Combine(paths.Root, "leftover.tmp"), "temporary");
                    UninstallProfileCleanup.Clear(paths, installer, uninstaller);
                    Assert(!File.Exists(appSource), "APP source was not removed.");
                    Assert(!File.Exists(Path.Combine(paths.Root, "APP", "Tools", "RemoveMe.EXE")), "Non-installer executable remains.");
                    Assert(!File.Exists(Path.Combine(paths.Root, "APP", "Tools", "README.txt")), "Nested non-EXE data remains.");
                    Assert(!File.Exists(Path.Combine(paths.Root, "leftover.tmp")), "Loose installation file remains.");
                    Assert(!Directory.Exists(paths.Official) && !Directory.Exists(paths.Transit), "Configuration folders remain.");
                    Assert(File.Exists(uninstaller), "Running uninstaller was removed before its process exited.");
                    Assert(!File.Exists(appExecutable), "Application executable remains.");
                    Assert(File.Exists(installer), "Setup package was removed.");
                    Assert(ConfigFiles.Matches(paths.Target, expectedOfficial), "Restored official files changed during cleanup.");

                    UninstallProfileCleanup.RemoveUninstallerAfterExit(paths, uninstaller);
                    string[] appEntries = Directory.GetFileSystemEntries(Path.Combine(paths.Root, "APP"));
                    string[] rootEntries = Directory.GetFileSystemEntries(paths.Root);
                    Assert(appEntries.Length == 1 && String.Equals(appEntries[0], installer, StringComparison.OrdinalIgnoreCase),
                        "The setup package was not the only item left in APP.");
                    Assert(rootEntries.Length == 1 && String.Equals(rootEntries[0], Path.Combine(paths.Root, "APP"), StringComparison.OrdinalIgnoreCase),
                        "Unexpected data remains in the installation folder.");
                });

                test("external setup package is untouched and installation folder is removed", delegate {
                    string fixture = Path.Combine(root, "external-installer");
                    var paths = new AppPaths(Path.Combine(fixture, "install"), Path.Combine(fixture, "profile"));
                    Directory.CreateDirectory(paths.Root);
                    string expectedInstaller = Path.Combine(paths.Root, "APP", "ChatGPTManagerSetup.exe");
                    string uninstaller = Path.Combine(paths.Root, "APP", "Uninstall.exe");
                    Directory.CreateDirectory(Path.GetDirectoryName(uninstaller));
                    File.WriteAllText(uninstaller, "active uninstaller");
                    File.WriteAllText(Path.Combine(paths.Root, "data.txt"), "remove me");
                    string externalInstaller = Path.Combine(fixture, "ChatGPTManagerSetup.exe");
                    File.WriteAllText(externalInstaller, "external setup package");

                    UninstallProfileCleanup.Clear(paths, expectedInstaller, uninstaller);
                    UninstallProfileCleanup.RemoveUninstallerAfterExit(paths, uninstaller);
                    Assert(File.Exists(externalInstaller), "External setup package was changed.");
                    Assert(!Directory.Exists(paths.Root), "Empty installation folder was not removed.");
                });

                Console.WriteLine("Passed " + passed + " uninstall regression tests.");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("FAIL " + ex);
                return 1;
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        private static int Main()
        {
            return Run();
        }
    }
}
