using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace ChatGPTManager
{
    internal enum ConfigMode { Unknown, Official, Transit }

    internal sealed class AppPaths
    {
        public readonly string Root, Target, Official, Transit;
        public AppPaths(string root, string target)
        {
            Root = Path.GetFullPath(root);
            Target = Path.GetFullPath(target);
            Official = Path.Combine(Root, "Official");
            Transit = Path.Combine(Root, "Transit");
        }
        public static AppPaths Default()
        {
            return new AppPaths(Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..")),
                ResolveCurrentTarget());
        }
        public static string ResolveCurrentTarget()
        {
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string detected;
            try { detected = ResolveTarget(profile, Environment.GetEnvironmentVariable("CODEX_HOME")); }
            catch (IOException) { detected = ResolveTarget(profile, null); }
            return detected;
        }
        internal static string ResolveTarget(string userProfile, string codexHome)
        {
            if (!String.IsNullOrWhiteSpace(codexHome))
            {
                if (!Path.IsPathRooted(codexHome))
                    throw new IOException("CODEX_HOME 必须是绝对路径，当前值无法安全识别。");
                string fullPath = Path.GetFullPath(codexHome);
                if (IsVolumeRoot(fullPath))
                    throw new IOException("CODEX_HOME 不能指向磁盘或网络共享根目录。");
                return fullPath;
            }
            if (String.IsNullOrWhiteSpace(userProfile))
                throw new IOException("Windows 未能确定当前用户目录，无法定位 .codex 配置。");
            return Path.GetFullPath(Path.Combine(userProfile, ".codex"));
        }
        internal static bool IsVolumeRoot(string path)
        {
            try
            {
                string fullPath = Path.GetFullPath(path);
                string root = Path.GetPathRoot(fullPath);
                return !String.IsNullOrEmpty(root)
                    && String.Equals(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                        root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                        StringComparison.OrdinalIgnoreCase);
            }
            catch { return true; }
        }
    }

    internal static class AppSettings
    {
        private static string SettingsPath
        {
            get
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (String.IsNullOrWhiteSpace(local)) throw new IOException("无法确定当前用户的本机应用数据目录。");
                return Path.Combine(local, "ChatGPTManager", "settings.json");
            }
        }

        public static void Clear()
        {
            string settingsPath = SettingsPath;
            string floatingBallPath = Path.Combine(Path.GetDirectoryName(settingsPath), "floating-ball.json");
            if (File.Exists(floatingBallPath))
            {
                if ((File.GetAttributes(floatingBallPath) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("拒绝删除符号链接形式的悬浮球设置文件。");
                File.Delete(floatingBallPath);
            }
            Clear(settingsPath);
        }
        internal static void Clear(string settingsPath)
        {
            string directory = Path.GetDirectoryName(settingsPath);
            if (File.Exists(settingsPath))
            {
                if ((File.GetAttributes(settingsPath) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("拒绝删除符号链接形式的应用设置文件。");
                File.Delete(settingsPath);
            }
            if (Directory.Exists(directory))
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("拒绝删除符号链接形式的应用设置目录。");
                if (Directory.GetFileSystemEntries(directory).Length == 0)
                    Directory.Delete(directory, false);
            }
        }

    }

    // Source files are copied byte-for-byte; mode detection parses them read-only and never re-saves them.
    internal static class ConfigFiles
    {
        public static readonly string[] Names = { "config.toml", "auth.json" };
        public static void RequirePair(string folder)
        {
            if (!Directory.Exists(folder)) throw new IOException("找不到文件夹：\n" + folder);
            if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("配置文件夹不能是符号链接：\n" + folder);
            foreach (string name in Names)
            {
                string file = Path.Combine(folder, name);
                if (!File.Exists(file)) throw new IOException("缺少文件：\n" + file);
                if (new FileInfo(file).Length == 0) throw new IOException("文件为空：\n" + file);
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("请使用真实配置文件，而不是符号链接：\n" + file);
            }
        }
        public static string Hash(string file)
        {
            using (SHA256 hash = SHA256.Create())
            using (FileStream stream = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "");
        }
        public static string[] Fingerprint(string folder)
        {
            RequirePair(folder);
            return Names.Select(name => Hash(Path.Combine(folder, name))).ToArray();
        }
        public static bool Matches(string folder, string[] hashes)
        {
            try { return Fingerprint(folder).SequenceEqual(hashes); }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
        public static bool Equal(string first, string second)
        {
            try { return Matches(first, Fingerprint(second)); }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
        public static bool LooksOfficial(string folder)
        {
            try
            {
                RequirePair(folder);
                return LooksOfficialFiles(Path.Combine(folder, Names[0]), Path.Combine(folder, Names[1]));
            }
            catch { return false; }
        }
        public static bool LooksOfficialFiles(string configPath, string authPath)
        {
            try
            {
                RequireFile(configPath, Names[0]);
                RequireFile(authPath, Names[1]);
                string config = File.ReadAllText(configPath);
                string root = Regex.Split(config, @"(?m)^\s*\[", RegexOptions.None)[0];
                Match providerLine = Regex.Match(root, @"(?m)^\s*model_provider\s*=\s*(.+)$");
                if (providerLine.Success && !Regex.IsMatch(providerLine.Groups[1].Value,
                    "^\\s*(?:\"openai\"|'openai')\\s*(?:#.*)?$")) return false;
                // Redirected endpoints are not recognized as an official connection.
                if (Regex.IsMatch(config, @"(?m)^\s*(?:base_url|openai_base_url)\s*=")) return false;
                var auth = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(authPath))
                    as Dictionary<string, object>;
                object mode;
                object rawTokens;
                if (auth == null || !auth.TryGetValue("auth_mode", out mode) || Convert.ToString(mode) != "chatgpt"
                    || !auth.TryGetValue("tokens", out rawTokens)) return false;
                var tokens = rawTokens as Dictionary<string, object>;
                object token;
                return tokens != null && tokens.TryGetValue("access_token", out token)
                    && !String.IsNullOrWhiteSpace(Convert.ToString(token));
            }
            catch { return false; }
        }
        private static void RequireFile(string path, string expectedName)
        {
            if (!String.Equals(Path.GetFileName(path), expectedName, StringComparison.OrdinalIgnoreCase))
                throw new IOException("配置文件名不匹配。");
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
                throw new IOException("配置文件不存在或为空。");
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("配置文件不能是符号链接。");
        }
        private static string TomlString(string section, string key)
        {
            Match match = Regex.Match(section, "(?m)^\\s*" + Regex.Escape(key)
                + "\\s*=\\s*(\"(?:[^\"\\\\\\r\\n]|\\\\.)*\"|'[^'\\r\\n]*')\\s*(?:#[^\\r\\n]*)?\\r?$" );
            if (!match.Success) return null;
            string literal = match.Groups[1].Value;
            if (literal[0] == '\'') return literal.Substring(1, literal.Length - 2);
            return new JavaScriptSerializer().Deserialize<string>(literal);
        }
        private static string SelectedProvider(string config)
        {
            string root = Regex.Split(config, @"(?m)^\s*\[", RegexOptions.None)[0];
            return TomlString(root, "model_provider");
        }
        private static string ProviderEndpoint(string config, string provider)
        {
            string key = Regex.Escape(provider);
            string header = @"(?m)^\s*\[\s*model_providers\s*\.\s*(?:" + key + "|\"" + key
                + "\"|'" + key + @"')\s*\]\s*(?:#[^\r\n]*)?\r?$";
            Match table = Regex.Match(config, header);
            if (!table.Success) return null;
            string body = Regex.Split(config.Substring(table.Index + table.Length), @"(?m)^\s*\[")[0];
            return TomlString(body, "base_url");
        }
        private static bool HasApiKeyLogin(string folder)
        {
            var auth = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(Path.Combine(folder, "auth.json")))
                as Dictionary<string, object>;
            if (auth == null) return false;
            object key, mode, tokens;
            if (!auth.TryGetValue("OPENAI_API_KEY", out key) || !(key is string)
                || String.IsNullOrWhiteSpace((string)key)) return false;
            if (auth.TryGetValue("auth_mode", out mode) && Convert.ToString(mode) == "chatgpt") return false;
            // A partial copy with official login tokens is not a verified transit configuration.
            if (auth.TryGetValue("tokens", out tokens) && tokens != null) return false;
            return true;
        }
        private static bool LooksLikeTransit(string target, string source)
        {
            try
            {
                RequirePair(target); RequirePair(source);
                string expected = File.ReadAllText(Path.Combine(source, "config.toml"));
                string current = File.ReadAllText(Path.Combine(target, "config.toml"));
                string provider = SelectedProvider(expected);
                if (String.IsNullOrWhiteSpace(provider) || provider == "openai" || SelectedProvider(current) != provider) return false;
                string endpoint = ProviderEndpoint(expected, provider);
                if (String.IsNullOrWhiteSpace(endpoint) || ProviderEndpoint(current, provider) != endpoint) return false;
                return HasApiKeyLogin(source) && HasApiKeyLogin(target);
            }
            catch { return false; }
        }
        public static ConfigMode Detect(AppPaths paths)
        {
            bool official = Equal(paths.Target, paths.Official);
            bool transit = Equal(paths.Target, paths.Transit);
            if (official && transit) return ConfigMode.Unknown;
            if (transit) return ConfigMode.Transit;
            if (official) return ConfigMode.Official;
            // Codex can append settings or rewrite auth metadata without changing the relay in use.
            // Match the selected provider, its endpoint, and API-key login rather than all bytes.
            if (LooksLikeTransit(paths.Target, paths.Transit)) return ConfigMode.Transit;
            // Official tokens and settings may have changed since the last switch.
            if (LooksOfficial(paths.Target) && LooksOfficial(paths.Official)) return ConfigMode.Official;
            return ConfigMode.Unknown;
        }
        public static string ModeName(ConfigMode mode)
        {
            return mode == ConfigMode.Official ? "官方" : mode == ConfigMode.Transit ? "中转" : "未识别 / 文件不完整";
        }
    }

    internal static class TransitCredentialStore
    {
        private const string ApiKeyProperty = "OPENAI_API_KEY";
        private const int MaximumApiKeyLength = 4096;

        public static bool IsConfigured(string folder)
        {
            try
            {
                string authPath = Path.Combine(folder, "auth.json");
                if (!File.Exists(authPath) || (File.GetAttributes(authPath) & FileAttributes.ReparsePoint) != 0)
                    return false;
                var auth = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(authPath))
                    as Dictionary<string, object>;
                object value;
                return auth != null && auth.TryGetValue(ApiKeyProperty, out value)
                    && value is string && !String.IsNullOrWhiteSpace((string)value);
            }
            catch { return false; }
        }

        public static bool SaveApiKey(string folder, string apiKey)
        {
            string value = (apiKey ?? String.Empty).Trim();
            if (value.Length == 0) throw new IOException("请输入 API 密钥后再保存。");
            if (value.Length > MaximumApiKeyLength || value.Any(Char.IsControl) || value.Any(Char.IsWhiteSpace))
                throw new IOException("API 密钥格式无效，请检查输入内容后重试。");

            string fullFolder = Path.GetFullPath(folder);
            if (!Directory.Exists(fullFolder)
                || (File.GetAttributes(fullFolder) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Transit 目录不存在或不是本地真实目录。");

            string configPath = Path.Combine(fullFolder, "config.toml");
            if (!File.Exists(configPath) || new FileInfo(configPath).Length == 0
                || (File.GetAttributes(configPath) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Transit 默认配置缺失或无效，请重新安装软件。");

            string authPath = Path.Combine(fullFolder, "auth.json");
            Dictionary<string, object> auth = new Dictionary<string, object>();
            if (File.Exists(authPath))
            {
                if ((File.GetAttributes(authPath) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Transit 的 auth.json 不是普通文件，未保存密钥。");
                try
                {
                    auth = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(authPath))
                        as Dictionary<string, object>;
                }
                catch { throw new IOException("Transit 的 auth.json 格式无效，未保存密钥。"); }
                if (auth == null) throw new IOException("Transit 的 auth.json 格式无效，未保存密钥。");
            }

            object existing;
            if (auth.TryGetValue(ApiKeyProperty, out existing)
                && existing is string && String.Equals((string)existing, value, StringComparison.Ordinal))
                return false;

            auth[ApiKeyProperty] = value;
            string temporaryPath = Path.Combine(fullFolder, ".auth-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                string json = new JavaScriptSerializer().Serialize(auth);
                File.WriteAllText(temporaryPath, json, new UTF8Encoding(false));
                if (File.Exists(authPath)) File.Replace(temporaryPath, authPath, null);
                else File.Move(temporaryPath, authPath);
                return true;
            }
            finally
            {
                try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
            }
        }
        private static string ReadApiKey(string folder)
        {
            string authPath = Path.Combine(Path.GetFullPath(folder), "auth.json");
            if (!File.Exists(authPath) || (File.GetAttributes(authPath) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("API 密钥文件缺失或不是普通文件。");
            Dictionary<string, object> auth;
            try
            {
                auth = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(authPath))
                    as Dictionary<string, object>;
            }
            catch { throw new IOException("API 密钥文件格式无效。"); }
            object key;
            if (auth == null || !auth.TryGetValue(ApiKeyProperty, out key)
                || !(key is string) || String.IsNullOrWhiteSpace((string)key))
                throw new IOException("Transit 尚未设置有效的 API 密钥。");
            return (string)key;
        }
        public static bool SyncApiKey(string sourceFolder, string destinationFolder)
        {
            return SaveApiKey(destinationFolder, ReadApiKey(sourceFolder));
        }
    }

    internal sealed class ProfileSnapshotResult
    {
        public readonly bool SavedToOfficial;
        public readonly bool SavedToHistory;
        public readonly string Message;

        public ProfileSnapshotResult(bool savedToOfficial, bool savedToHistory, string message)
        {
            SavedToOfficial = savedToOfficial;
            SavedToHistory = savedToHistory;
            Message = message;
        }
    }

    internal static class InitialProfileSnapshotService
    {
        public static ProfileSnapshotResult SaveBeforeTransit(AppPaths paths, Func<bool> isRunning)
        {
            if (isRunning()) throw new IOException("检测到 ChatGPT 正在运行，未备份或切换配置。");
            if (IsReparsePoint(paths.Target) || IsReparsePoint(paths.Root)
                || IsReparsePoint(Path.Combine(paths.Root, "APP")) || IsReparsePoint(paths.Official))
                throw new IOException("配置目录或软件目录是符号链接，已停止操作。");

            bool hasConfig = File.Exists(Path.Combine(paths.Target, ConfigFiles.Names[0]));
            bool hasAuth = File.Exists(Path.Combine(paths.Target, ConfigFiles.Names[1]));
            if (!hasConfig && !hasAuth)
                return new ProfileSnapshotResult(false, false, "当前没有可备份的配置文件；首次切换会创建目标目录。");
            if (hasConfig != hasAuth)
                throw new IOException("当前 ChatGPT 配置目录只找到 config.toml 或 auth.json 其中一个。请检查这两个文件后重试，未覆盖现有配置。");

            ConfigFiles.RequirePair(paths.Target);
            string[] expected = ConfigFiles.Fingerprint(paths.Target);
            bool officialEmpty = !ConfigFiles.Names.Any(name => File.Exists(Path.Combine(paths.Official, name)));
            string destination;
            if (officialEmpty)
            {
                destination = paths.Official;
                Directory.CreateDirectory(destination);
            }
            else
            {
                string backupRoot = Path.Combine(paths.Official, "SafetyBackup");
                if (IsReparsePoint(backupRoot))
                    throw new IOException("Official 安全备份目录是符号链接，未写入任何配置。");
                Directory.CreateDirectory(backupRoot);
                destination = Path.Combine(backupRoot, "BeforeTransit-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")
                    + "-" + Guid.NewGuid().ToString("N").Substring(0, 6));
                Directory.CreateDirectory(destination);
            }

            var written = new System.Collections.Generic.List<string>();
            try
            {
                foreach (string name in ConfigFiles.Names)
                {
                    if (isRunning()) throw new IOException("检测到 ChatGPT 已启动，已停止配置备份。");
                    string target = Path.Combine(destination, name);
                    File.Copy(Path.Combine(paths.Target, name), target, false);
                    written.Add(target);
                }
                if (!ConfigFiles.Matches(paths.Target, expected) || !ConfigFiles.Matches(destination, expected))
                    throw new IOException("当前配置备份校验失败，已停止切换。");
            }
            catch
            {
                foreach (string path in written)
                {
                    try { if (File.Exists(path)) File.Delete(path); } catch { }
                }
                throw;
            }

            return new ProfileSnapshotResult(officialEmpty, !officialEmpty,
                officialEmpty ? "首次切换前已将当前两个配置文件保存到 Official。"
                    : "当前两个配置文件已另存到 Official\\SafetyBackup，原有 Official 未覆盖。");
        }

        private static bool IsReparsePoint(string path)
        {
            if (!Directory.Exists(path) && !File.Exists(path)) return false;
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
    }

    internal static class TransitActivationState
    {
        private static string MarkerPath(AppPaths paths)
        {
            return Path.Combine(paths.Root, "APP", "transit-active.state");
        }

        public static void SetActive(AppPaths paths, bool active)
        {
            string appFolder = Path.Combine(paths.Root, "APP");
            string marker = MarkerPath(paths);
            if (Directory.Exists(appFolder) && (File.GetAttributes(appFolder) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("APP 目录是符号链接，未保存当前模式状态。");
            if (File.Exists(marker) && (File.GetAttributes(marker) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("当前模式标记是符号链接，未修改该文件。");

            if (active)
            {
                Directory.CreateDirectory(appFolder);
                if (!File.Exists(marker)) File.WriteAllText(marker, "transit", new UTF8Encoding(false));
            }
            else if (File.Exists(marker)) File.Delete(marker);
        }

        public static bool IsActiveOrDetected(AppPaths paths, ConfigMode current)
        {
            string marker = MarkerPath(paths);
            if (current == ConfigMode.Transit) return true;
            return File.Exists(marker) || Directory.Exists(marker);
        }

        public static void SyncWithCurrentMode(AppPaths paths, ConfigMode current)
        {
            if (current == ConfigMode.Transit && TransitCredentialStore.IsConfigured(paths.Transit))
                SetActive(paths, true);
            else if (current == ConfigMode.Official)
                SetActive(paths, false);
        }
    }

    internal static class CodexProcesses
    {
        public static bool IsCodex(string name, string executablePath)
        {
            string lower = name.ToLowerInvariant();
            if (lower == "codex" || lower == "codex-code-mode-host" || lower == "codex-desktop") return true;
            if (lower != "chatgpt") return false;
            // On this Windows installation the Codex desktop app is named ChatGPT.exe.
            if (String.IsNullOrEmpty(executablePath)) return true;
            string path = executablePath.Replace('/', '\\').ToLowerInvariant();
            return path.Contains("openai.codex_") || path.Contains("\\openai\\codex\\")
                || path.Contains("\\codex\\app\\");
        }
        public static bool AnyRunning()
        {
            foreach (Process process in Process.GetProcesses())
            {
                using (process)
                {
                    try
                    {
                        string name = process.ProcessName;
                        if (!name.Equals("ChatGPT", StringComparison.OrdinalIgnoreCase))
                        {
                            if (IsCodex(name, null)) return true;
                            continue;
                        }
                        string path = null;
                        try { path = process.MainModule.FileName; } catch { }
                        if (IsCodex(name, path)) return true;
                    }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception) { }
                }
            }
            return false;
        }
    }

    internal interface IConfirmedCopy
    {
        // false includes cancellation. Implementations must never approve overwrite dialogs automatically.
        bool CopyPair(IntPtr owner, string from, string to, string title);
    }

    internal sealed class WindowsConfirmedCopy : IConfirmedCopy
    {
        // No FOF_NOCONFIRMATION, FOF_NO_UI, FOF_SILENT, or FOF_RENAMEONCOLLISION.
        // Existing filenames are replaced only through Windows' native conflict UI.
        internal const ushort OperationFlags = 0;
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            public IntPtr pFrom;
            public IntPtr pTo;
            public ushort fFlags;
            [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public IntPtr lpszProgressTitle;
        }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
        private static extern int SHFileOperation(ref SHFILEOPSTRUCT operation);

        public bool CopyPair(IntPtr owner, string from, string to, string title)
        {
            ConfigFiles.RequirePair(from);
            return CopyFiles(owner, Path.Combine(from, ConfigFiles.Names[0]),
                Path.Combine(from, ConfigFiles.Names[1]), to, title);
        }

        public bool CopyFiles(IntPtr owner, string configFile, string authFile, string to, string title)
        {
            string[] files = { Path.GetFullPath(configFile), Path.GetFullPath(authFile) };
            for (int i = 0; i < files.Length; i++)
            {
                if (!String.Equals(Path.GetFileName(files[i]), ConfigFiles.Names[i], StringComparison.OrdinalIgnoreCase))
                    throw new IOException("请选择名为 " + ConfigFiles.Names[i] + " 的文件：\n" + files[i]);
                if (!File.Exists(files[i])) throw new IOException("找不到文件：\n" + files[i]);
                FileInfo info = new FileInfo(files[i]);
                if (info.Length == 0) throw new IOException("文件为空：\n" + files[i]);
                if ((File.GetAttributes(files[i]) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("请使用真实配置文件，而不是符号链接：\n" + files[i]);
            }
            if (!Directory.Exists(to)) throw new IOException("目标文件夹不存在：\n" + to);
            string sources = String.Join("\0", files) + "\0\0";
            IntPtr sourceBuffer = Marshal.StringToHGlobalUni(sources);
            IntPtr targetBuffer = Marshal.StringToHGlobalUni(to + "\0\0");
            try
            {
                var operation = new SHFILEOPSTRUCT {
                    hwnd = owner, wFunc = 2, pFrom = sourceBuffer, pTo = targetBuffer, fFlags = OperationFlags
                };
                int result = SHFileOperation(ref operation);
                if (operation.fAnyOperationsAborted || result == 0x75 || result == 1223) return false;
                if (result != 0) throw new IOException("Windows 未能完成复制。错误代码：0x" + result.ToString("X")
                    + "\n请检查文件权限，并根据 Windows 提示操作。");
                return true;
            }
            finally { Marshal.FreeHGlobal(sourceBuffer); Marshal.FreeHGlobal(targetBuffer); }
        }
    }

    internal sealed class SwitchResult
    {
        public readonly bool Success;
        public readonly string Message;
        public SwitchResult(bool success, string message) { Success = success; Message = message; }
    }

    internal sealed class SwitchService
    {
        private readonly AppPaths paths;
        private readonly IConfirmedCopy copier;
        private readonly Func<bool> isRunning;
        public SwitchService(AppPaths paths, IConfirmedCopy copier, Func<bool> isRunning)
        { this.paths = paths; this.copier = copier; this.isRunning = isRunning; }

        private void RequireStopped()
        {
            if (isRunning()) throw new IOException("检测到 ChatGPT 正在运行。请完全退出 ChatGPT，再执行切换。");
        }

        public SwitchResult SwitchTo(ConfigMode destination, IntPtr owner, Action<string> progress)
        {
            RequireStopped();
            if (destination == ConfigMode.Unknown) throw new ArgumentException("请选择官方或中转。");
            string source = destination == ConfigMode.Official ? paths.Official : paths.Transit;
            ConfigFiles.RequirePair(source);
            if (destination == ConfigMode.Transit && ConfigFiles.Equal(paths.Official, paths.Transit))
                throw new IOException("Official 与 Transit 的文件完全相同，请检查并保存中转 API 密钥后重试。");
            if (destination == ConfigMode.Transit && !TransitCredentialStore.IsConfigured(source))
                throw new IOException("请先在软件界面输入并保存 API 密钥，再切换到中转。");
            string installRoot = Path.GetFullPath(paths.Root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string fullTarget = Path.GetFullPath(paths.Target).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (String.Equals(fullTarget, installRoot, StringComparison.OrdinalIgnoreCase)
                || fullTarget.StartsWith(installRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("ChatGPT 配置路径不能位于软件安装目录中。");
            if (Directory.Exists(paths.Target) && (File.GetAttributes(paths.Target) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("目标配置目录是符号链接，已停止切换：\n" + paths.Target);
            if (!Directory.Exists(paths.Target)) Directory.CreateDirectory(paths.Target);
            ConfigMode current = ConfigFiles.Detect(paths);
            if (current == destination && destination == ConfigMode.Transit)
            {
                RequireStopped();
                bool credentialUpdated = TransitCredentialStore.SyncApiKey(paths.Transit, paths.Target);
                TransitActivationState.SetActive(paths, true);
                return new SwitchResult(true, credentialUpdated
                    ? "当前已是中转配置；已将新密钥同步到当前 ChatGPT 配置，其他配置内容保持不变。"
                    : "当前已经是中转配置，且 API 密钥已同步。未重复覆盖配置文件。");
            }
            if (current == destination)
            {
                TransitActivationState.SetActive(paths, false);
                return new SwitchResult(true, "当前已经是" + ConfigFiles.ModeName(destination) + "配置，无需重复覆盖。\n未复制或改动任何配置文件。");
            }

            bool firstProfileSavedAsOfficial = false;
            if (destination == ConfigMode.Transit && current == ConfigMode.Unknown)
            {
                ProfileSnapshotResult snapshot = InitialProfileSnapshotService.SaveBeforeTransit(paths, isRunning);
                firstProfileSavedAsOfficial = snapshot.SavedToOfficial;
                if (firstProfileSavedAsOfficial) current = ConfigMode.Official;
                progress(snapshot.Message);
            }

            if (destination == ConfigMode.Transit && current == ConfigMode.Official && !firstProfileSavedAsOfficial)
            {
                ConfigFiles.RequirePair(paths.Target);
                if (!Directory.Exists(paths.Official)) throw new IOException("Official 文件夹不存在：\n" + paths.Official);
                string[] latestOfficial = ConfigFiles.Fingerprint(paths.Target);
                // A unique historical copy survives partial/cancelled overwrites of Official itself.
                // The Windows shell copies these bytes; the application does not rewrite the files.
                string history = Path.Combine(paths.Official, "SafetyBackup",
                    "Official-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6));
                Directory.CreateDirectory(history);
                progress("先保留独立的官方安全副本，完成核对后才会继续。");
                RequireStopped();
                bool secured = copier.CopyPair(owner, paths.Target, history, "保留官方安全副本");
                if (!secured || !ConfigFiles.Matches(history, latestOfficial) || !ConfigFiles.Matches(paths.Target, latestOfficial))
                    return new SwitchResult(false, "官方安全副本未完整完成，已停止切换。\n未覆盖 Official 中的文件，也未开始替换当前配置。");
                progress("第 1 步 / 共 2 步：保存最新官方配置，请在 Windows 提示中确认覆盖。");
                RequireStopped();
                bool backedUp = copier.CopyPair(owner, paths.Target, paths.Official, "保存最新官方配置");
                if (!backedUp)
                    return new SwitchResult(false, "官方备份已取消，未开始替换当前配置。\n如有个别备份文件已复制，它们将保留在 Official 中。");
                if (!ConfigFiles.Matches(paths.Official, latestOfficial)
                    || !ConfigFiles.Matches(paths.Target, latestOfficial))
                    return new SwitchResult(false, "官方备份未完整完成，或源文件发生了变化。\n已停止后续切换，未开始替换当前配置。请重试并确认覆盖两个文件。");
            }

            RequireStopped();
            // Re-read after the user has completed the first native dialog.
            string[] desired = ConfigFiles.Fingerprint(source);
            progress(destination == ConfigMode.Transit && current == ConfigMode.Official
                ? "第 2 步 / 共 2 步：应用中转配置，请在 Windows 提示中确认覆盖。"
                : "应用" + ConfigFiles.ModeName(destination) + "配置，请在 Windows 提示中确认覆盖。");
            bool copied = copier.CopyPair(owner, source, paths.Target, "切换到" + ConfigFiles.ModeName(destination));
            if (!copied)
                return new SwitchResult(false, "切换已取消，未确认切换成功。\nWindows 可能已复制部分文件，请查看当前状态，并重新切换以完成两个文件的覆盖。");
            if (!ConfigFiles.Matches(paths.Target, desired) || !ConfigFiles.Matches(source, desired))
                return new SwitchResult(false, "切换未完整完成：两个目标文件与来源不完全一致。\n请重新切换，并在 Windows 提示中确认覆盖全部文件。");
            TransitActivationState.SetActive(paths, destination == ConfigMode.Transit);
            return new SwitchResult(true, "已成功切换到" + ConfigFiles.ModeName(destination) + "配置。\n两个文件均已核对一致，现在可以重新打开 ChatGPT。");
        }
    }
}
