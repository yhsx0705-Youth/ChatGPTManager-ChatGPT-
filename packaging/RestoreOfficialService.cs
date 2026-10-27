using System;
using System.IO;
using System.Linq;

namespace ChatGPTManager
{
    internal sealed class RestoreResult
    {
        public readonly bool Success;
        public readonly string Message;

        public RestoreResult(bool success, string message)
        {
            Success = success;
            Message = message;
        }
    }

    internal sealed class DirectUninstallCopy : IConfirmedCopy
    {
        public bool CopyPair(IntPtr owner, string from, string to, string title)
        {
            Directory.CreateDirectory(to);
            foreach (string name in ConfigFiles.Names)
                File.Copy(Path.Combine(from, name), Path.Combine(to, name), true);
            return true;
        }
    }

    internal static class RestoreOfficialService
    {
        public static RestoreResult BeforeUninstall(AppPaths paths, IConfirmedCopy copier, Func<bool> isCodexRunning)
        {
            try
            {
                if (isCodexRunning())
                    return new RestoreResult(false, "ChatGPT or ChatGPT Manager is running. Close it completely and retry. No files were removed.");

                if (IsReparsePoint(paths.Root) || IsReparsePoint(Path.Combine(paths.Root, "APP"))
                    || IsReparsePoint(paths.Official)
                    || IsReparsePoint(paths.Transit) || IsReparsePoint(paths.Target))
                    return new RestoreResult(false, "A managed directory is a junction or symbolic link. Uninstall stopped to avoid touching files outside the selected locations.");

                ConfigMode current = ConfigFiles.Detect(paths);
                if (current == ConfigMode.Official)
                    TransitActivationState.SetActive(paths, false);
                bool activeTransit = TransitActivationState.IsActiveOrDetected(paths, current);
                if (TransitCredentialStore.IsConfigured(paths.Transit) && activeTransit)
                    return new RestoreResult(false, "检测到 Transit 已保存 API 密钥，且当前 ChatGPT 配置正在使用中转。按设置要求，当前禁止卸载；没有还原或删除任何文件。请先在软件中切回官方配置，再重新卸载。");

                if (current == ConfigMode.Official)
                    return new RestoreResult(true, "当前配置与 Official 中保存的文件一致，保持不变。");

                string source = FindOfficialSource(paths.Official);
                if (source == null)
                    return new RestoreResult(true, "未找到可恢复的 Official 配置；当前 .codex 文件保持原样。未在中转中使用已保存密钥，可以继续卸载。");

                string[] expected = ConfigFiles.Fingerprint(source);
                Directory.CreateDirectory(paths.Target);
                if (!copier.CopyPair(IntPtr.Zero, source, paths.Target, "Restore official configuration before uninstall"))
                    return new RestoreResult(false, "Official configuration restore was cancelled. Uninstall stopped and all files were kept.");

                if (!ConfigFiles.Matches(source, expected) || !ConfigFiles.Matches(paths.Target, expected))
                    return new RestoreResult(false, "The restored official configuration failed verification. Uninstall stopped and all files were kept.");
                if (isCodexRunning())
                    return new RestoreResult(false, "ChatGPT or ChatGPT Manager started during restore. The configuration was restored, but no files were removed. Close it and retry.");

                return new RestoreResult(true, "The verified official configuration was restored to the current user's .codex folder and both files were checked.");
            }
            catch (Exception ex)
            {
                return new RestoreResult(false, "The official configuration could not be restored safely:\n" + ex.Message + "\nUninstall stopped and all files were kept.");
            }
        }

        private static string FindOfficialSource(string official)
        {
            if (ConfigFiles.LooksOfficial(official)) return official;

            string backupRoot = Path.Combine(official, "SafetyBackup");
            string[] backups = Directory.Exists(backupRoot)
                ? Directory.GetDirectories(backupRoot)
                .Select(path => new DirectoryInfo(path))
                .Where(info => (info.Attributes & FileAttributes.ReparsePoint) == 0)
                .OrderByDescending(info => info.CreationTimeUtc)
                .ThenByDescending(info => info.Name, StringComparer.Ordinal)
                .Select(info => info.FullName)
                .ToArray()
                : new string[0];

            string verifiedBackup = backups.FirstOrDefault(ConfigFiles.LooksOfficial);
            if (verifiedBackup != null) return verifiedBackup;
            if (HasCompletePair(official)) return official;
            return backups.FirstOrDefault(HasCompletePair);
        }

        private static bool HasCompletePair(string folder)
        {
            try { ConfigFiles.RequirePair(folder); return true; }
            catch { return false; }
        }

        private static bool IsReparsePoint(string path)
        {
            if (!Directory.Exists(path) && !File.Exists(path)) return false;
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
    }

    internal static class InstallerPathPolicy
    {
        public static bool IsValid(string path)
        {
            if (String.IsNullOrWhiteSpace(path))
                return false;
            if (path.Any(character => character < 0x20 || character > 0x7e))
                return false;
            if (path.IndexOfAny(new[] { '<', '>', '"', '|', '?', '*' }) >= 0) return false;

            try
            {
                string normalized = path.Replace('/', '\\');
                bool driveQualified = normalized.Length >= 3
                    && Char.IsLetter(normalized[0]) && normalized[1] == ':'
                    && normalized[2] == Path.DirectorySeparatorChar;
                bool uncQualified = normalized.StartsWith(@"\\", StringComparison.Ordinal);
                if (!driveQualified && !uncQualified) return false;
                for (int index = 2; index < normalized.Length; index++)
                    if (normalized[index] == ':') return false;

                string fullPath = Path.GetFullPath(path);
                string root = Path.GetPathRoot(fullPath);
                return !String.IsNullOrEmpty(root)
                    && !String.Equals(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                        root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                        StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
    }

    internal static class UninstallInvocationPolicy
    {
        public static bool IsExpectedInstallRoot(string installRoot, string parentExecutable)
        {
            try
            {
                if (!String.Equals(Path.GetFileName(parentExecutable), "Uninstall.exe", StringComparison.OrdinalIgnoreCase))
                    return false;
                string expectedRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(parentExecutable), ".."));
                return String.Equals(Path.GetFullPath(installRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    expectedRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
    }

    internal static class UninstallProfileCleanup
    {
        public static void Clear(AppPaths paths, string installerPath, string uninstallerPath)
        {
            string root = Path.GetFullPath(paths.Root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string volumeRoot = Path.GetPathRoot(root);
            if (String.IsNullOrEmpty(volumeRoot)
                || String.Equals(root, volumeRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
                throw new IOException("Refusing to clear a drive or share root.");

            string fullInstallerPath = Path.GetFullPath(installerPath);
            string expectedInstallerPath = Path.GetFullPath(Path.Combine(paths.Root, "APP", "ChatGPTManagerSetup.exe"));
            if (!String.Equals(fullInstallerPath, expectedInstallerPath, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Refusing to preserve an unexpected installer path.");
            string fullUninstallerPath = Path.GetFullPath(uninstallerPath);
            string expectedUninstallerPath = Path.GetFullPath(Path.Combine(paths.Root, "APP", "Uninstall.exe"));
            if (!String.Equals(fullUninstallerPath, expectedUninstallerPath, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Refusing to preserve an unexpected uninstaller path.");

            foreach (string entry in Directory.EnumerateFileSystemEntries(root))
                DeleteEntry(root, entry, fullInstallerPath, fullUninstallerPath);

            foreach (string entry in Directory.EnumerateFileSystemEntries(root))
                VerifyOnlyTemporaryExecutablesRemain(root, entry, fullInstallerPath, fullUninstallerPath);
        }

        public static void RemoveUninstallerAfterExit(AppPaths paths, string uninstallerPath)
        {
            string root = Path.GetFullPath(paths.Root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string fullPath = Path.GetFullPath(uninstallerPath);
            string expected = Path.GetFullPath(Path.Combine(paths.Root, "APP", "Uninstall.exe"));
            if (!String.Equals(fullPath, expected, StringComparison.OrdinalIgnoreCase)
                || !fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Refusing to remove an unexpected uninstaller path.");

            if (File.Exists(fullPath)) File.Delete(fullPath);
            string appFolder = Path.GetDirectoryName(fullPath);
            if (Directory.Exists(appFolder) && !Directory.EnumerateFileSystemEntries(appFolder).Any())
                Directory.Delete(appFolder, false);
            if (Directory.Exists(root) && !Directory.EnumerateFileSystemEntries(root).Any())
                Directory.Delete(root, false);
        }

        private static void DeleteEntry(string root, string path, string installerPath, string uninstallerPath)
        {
            string fullPath = Path.GetFullPath(path);
            if (!fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Refusing to remove a path outside the installation folder.");
            if (String.Equals(fullPath, installerPath, StringComparison.OrdinalIgnoreCase)
                || String.Equals(fullPath, uninstallerPath, StringComparison.OrdinalIgnoreCase)) return;
            if (!Directory.Exists(fullPath) && !File.Exists(fullPath)) return;

            FileAttributes attributes = File.GetAttributes(fullPath);
            bool isDirectory = (attributes & FileAttributes.Directory) != 0;

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                ClearReadOnly(fullPath, attributes);
                if (isDirectory) Directory.Delete(fullPath, false);
                else File.Delete(fullPath);
                return;
            }
            if (!isDirectory)
            {
                ClearReadOnly(fullPath, attributes);
                File.Delete(fullPath);
                return;
            }

            foreach (string entry in Directory.EnumerateFileSystemEntries(fullPath))
                DeleteEntry(root, entry, installerPath, uninstallerPath);

            if (!Directory.EnumerateFileSystemEntries(fullPath).Any())
            {
                ClearReadOnly(fullPath, attributes);
                Directory.Delete(fullPath, false);
            }
        }

        private static void VerifyOnlyTemporaryExecutablesRemain(string root, string path,
            string installerPath, string uninstallerPath)
        {
            string fullPath = Path.GetFullPath(path);
            if (!fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Unexpected path escaped the installation folder.");
            if (String.Equals(fullPath, installerPath, StringComparison.OrdinalIgnoreCase)
                || String.Equals(fullPath, uninstallerPath, StringComparison.OrdinalIgnoreCase)) return;
            if (!Directory.Exists(fullPath) && !File.Exists(fullPath)) return;

            FileAttributes attributes = File.GetAttributes(fullPath);
            bool isDirectory = (attributes & FileAttributes.Directory) != 0;
            if (!isDirectory || (attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The installation folder still contains files that could not be removed.");
            foreach (string entry in Directory.EnumerateFileSystemEntries(fullPath))
                VerifyOnlyTemporaryExecutablesRemain(root, entry, installerPath, uninstallerPath);
        }

        private static void ClearReadOnly(string path, FileAttributes attributes)
        {
            if ((attributes & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }
    }
}
