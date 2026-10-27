using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace ChatGPTManager
{
    internal sealed class Surface : Panel
    {
        public Surface() { DoubleBuffered = true; BackColor = Color.White; }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (Pen pen = new Pen(Color.FromArgb(225, 230, 239)))
                e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
    }

    internal sealed class MainForm : Form
    {
        private static readonly Color Ink = Color.FromArgb(24, 37, 56);
        private static readonly Color Muted = Color.FromArgb(100, 114, 136);
        private static readonly Color Teal = Color.FromArgb(0, 119, 108);
        private static readonly Color Purple = Color.FromArgb(99, 76, 192);
        private static readonly DateTimeOffset CompanionStart =
            new DateTimeOffset(2026, 10, 21, 22, 0, 0, TimeSpan.FromHours(8));
        private const string TransitWebsiteUrl = "https://www.acampus.cloud/";
        private const string TransitWalletUrl = "https://www.acampus.cloud/wallet";
        private const string TransitSignupUrl = "https://www.acampus.cloud/sign-up?aff=S9ag";
        private static readonly Font ProcessNormalFont = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular);
        private static readonly Font ProcessWarningFont = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
        private AppPaths paths;
        private readonly Label modeLabel, processLabel, officialFiles, transitKeyStatus, activityTitle, activityDetail;
        private readonly Button officialButton, transitButton, saveTransitKeyButton;
        private readonly Button startCodexButton, closeCodexButton;
        private readonly Button floatingBallButton;
        private readonly Button officialFolderButton, cancelButton;
        private readonly TextBox transitApiKeyInput;
        private Surface officialCard, transitCard, activityPanel;
        private readonly Label developerLabel, companionDurationLabel;
        private Label targetPathLabel, versionLabel;
        private Button backupFolderButton;
        private FloatingBallForm floatingBall;
        private readonly Timer timer;
        private ConfigMode pending = ConfigMode.Unknown;
        private DateTime? stoppedSince;
        private bool performing;
        private bool persistentMessage;
        private bool exiting;
        private float uiScale = 1F;
        private int tick;

        public MainForm(AppPaths paths)
        {
            this.paths = paths;
            Text = "ChatGPT 管理器 · 官方 / 中转配置切换";
            ClientSize = new Size(856, 664);
            // Scale once using this window's actual DPI. Framework auto-scaling can otherwise
            // apply a second inverse scale on the desktop's mixed-DPI configuration.
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(245, 247, 251);
            Font = new Font("Microsoft YaHei UI", 10F);
            ForeColor = Ink;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            AddLabel(this, "CHATGPT MANAGER", 30, 22, 650, 24, 10F, FontStyle.Bold, Teal);
            developerLabel = AddLabel(this, "开发者：云鹤", 690, 25, 148, 22, 8.5F, FontStyle.Regular, Muted);
            developerLabel.TextAlign = ContentAlignment.MiddleRight;
            developerLabel.AccessibleName = "开发者：云鹤";
            companionDurationLabel = AddLabel(this, FormatCompanionDuration(DateTimeOffset.Now),
                600, 42, 238, 48, 10.5F, FontStyle.Bold, Muted);
            companionDurationLabel.TextAlign = ContentAlignment.MiddleRight;
            companionDurationLabel.AccessibleName = "已陪伴用户时长";
            AddLabel(this, "轻量化 ChatGPT 管理器", 28, 55, 790, 46, 24F, FontStyle.Bold, Ink);
            AddLabel(this, "固定管理 config.toml 与 auth.json · 输入密钥保存后即可切换", 30, 108, 796, 26, 10F, FontStyle.Regular, Muted);

            Surface status = new Surface { Location = new Point(30, 151), Size = new Size(796, 82) };
            Controls.Add(status);
            AddLabel(status, "当前配置", 18, 13, 90, 24, 10F, FontStyle.Regular, Muted);
            modeLabel = AddLabel(status, "正在检查…", 112, 8, 520, 34, 16F, FontStyle.Bold, Teal);
            modeLabel.AccessibleName = "当前配置状态";
            processLabel = AddLabel(status, "正在检查 ChatGPT 运行状态…", 18, 46, 756, 24, 9F, FontStyle.Regular, Muted);
            Button refresh = MakeLink("刷新", 710, 12, 66, 28);
            refresh.AccessibleName = "刷新当前配置状态";
            refresh.Click += delegate { RefreshStatus(); };
            status.Controls.Add(refresh);

            officialCard = new Surface { Location = new Point(30, 248), Size = new Size(389, 220) };
            transitCard = new Surface { Location = new Point(437, 248), Size = new Size(389, 220) };
            Controls.Add(officialCard);
            Controls.Add(transitCard);
            AddLabel(officialCard, "01   官方", 22, 17, 300, 35, 18F, FontStyle.Bold, Teal);
            AddLabel(officialCard, "首次切换中转前自动保存当前配置", 22, 64, 343, 27, 10F, FontStyle.Regular, Ink);
            AddLabel(officialCard, "config.toml  +  auth.json", 22, 99, 343, 23, 10F, FontStyle.Regular, Muted);
            officialFiles = AddLabel(officialCard, "", 22, 128, 225, 24, 9F, FontStyle.Regular, Teal);
            officialFolderButton = MakeLink("打开目录", 263, 123, 103, 30);
            officialFolderButton.Click += delegate { OpenFolder(paths.Official); };
            officialCard.Controls.Add(officialFolderButton);
            officialButton = MakeButton("切换到官方", 22, 170, 345, 43, Teal, Color.White);
            officialButton.Click += delegate { QueueSwitch(ConfigMode.Official); };
            officialCard.Controls.Add(officialButton);

            AddLabel(transitCard, "02   中转", 22, 17, 150, 35, 18F, FontStyle.Bold, Purple);
            Button homeButton = MakeButton("进入主页", 22, 59, 88, 29,
                Color.FromArgb(231, 246, 243), Teal);
            homeButton.Font = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Bold);
            homeButton.AccessibleName = "进入中转服务主页";
            homeButton.Click += delegate { OpenTransitWebsite(); };
            transitCard.Controls.Add(homeButton);
            Button balanceButton = MakeButton("查看余额/充值", 118, 59, 112, 29,
                Color.FromArgb(243, 239, 255), Purple);
            balanceButton.AccessibleName = "查看余额或充值";
            balanceButton.Font = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Bold);
            balanceButton.Click += delegate { OpenTransitWallet(); };
            transitCard.Controls.Add(balanceButton);
            Button signupButton = MakeButton("点我注册（个人主页）", 238, 59, 129, 29,
                Color.FromArgb(0, 119, 108), Color.White);
            signupButton.Font = new Font("Microsoft YaHei UI", 8F, FontStyle.Bold);
            signupButton.AccessibleName = "注册中转服务并使用邀请链接";
            signupButton.Click += delegate { OpenTransitSignup(); };
            transitCard.Controls.Add(signupButton);
            AddLabel(transitCard, "请输入您的 API 密钥：", 22, 93, 150, 23, 10F, FontStyle.Regular, Ink);
            transitKeyStatus = AddLabel(transitCard, "", 180, 94, 187, 20, 8.5F, FontStyle.Regular, Purple);
            transitApiKeyInput = new TextBox {
                Location = new Point(22, 120), Size = new Size(235, 28),
                Font = new Font("Microsoft YaHei UI", 9F), UseSystemPasswordChar = true,
                MaxLength = 4096, AccessibleName = "请输入您的 API 密钥"
            };
            transitCard.Controls.Add(transitApiKeyInput);
            transitApiKeyInput.TextChanged += delegate { UpdateTransitKeyControls(); };
            saveTransitKeyButton = MakeButton("保存密钥", 267, 118, 100, 31, Color.FromArgb(243, 239, 255), Purple);
            saveTransitKeyButton.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
            saveTransitKeyButton.AccessibleName = "保存 API 密钥到本机 Transit 配置";
            saveTransitKeyButton.Click += delegate { SaveTransitApiKey(); };
            transitCard.Controls.Add(saveTransitKeyButton);
            transitButton = MakeButton("切换到中转", 22, 170, 345, 43, Purple, Color.White);
            transitButton.Click += delegate { QueueSwitch(ConfigMode.Transit); };
            transitCard.Controls.Add(transitButton);

            activityPanel = new Surface { Location = new Point(30, 480), Size = new Size(796, 91) };
            Controls.Add(activityPanel);
            activityTitle = AddLabel(activityPanel, "配置切换", 18, 11, 600, 26, 10F, FontStyle.Bold, Ink);
            activityDetail = AddLabel(activityPanel, "首次切换中转时自动保存当前 config.toml 与 auth.json。", 18, 44, 756, 28, 9F, FontStyle.Regular, Muted);
            cancelButton = MakeLink("取消切换", 682, 10, 96, 29);
            cancelButton.Visible = false;
            cancelButton.Click += delegate {
                pending = ConfigMode.Unknown; stoppedSince = null; SetEnabled(true);
                SetActivity("已取消等待", "未开始复制配置文件。", Muted); persistentMessage = true;
            };
            activityPanel.Controls.Add(cancelButton);
            targetPathLabel = AddLabel(this, "仅管理两个文件：" + paths.Target + "\\config.toml 与 auth.json", 30, 578, 796, 23, 9F, FontStyle.Regular, Muted);
            backupFolderButton = MakeLink("查看官方安全副本", 25, 620, 165, 28);
            backupFolderButton.Click += delegate { OpenFolder(Path.Combine(paths.Official, "SafetyBackup")); };
            Controls.Add(backupFolderButton);
            startCodexButton = MakeButton("启动 ChatGPT", 202, 618, 112, 31,
                Color.FromArgb(231, 246, 243), Teal);
            startCodexButton.Font = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Bold);
            startCodexButton.AccessibleName = "启动 ChatGPT 桌面应用";
            startCodexButton.Click += delegate { StartCodex(); };
            Controls.Add(startCodexButton);
            closeCodexButton = MakeButton("后台关闭 ChatGPT", 324, 618, 130, 31,
                Color.FromArgb(255, 236, 236), Color.FromArgb(176, 42, 42));
            closeCodexButton.Font = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Bold);
            closeCodexButton.AccessibleName = "关闭 ChatGPT 桌面窗口和后台进程";
            closeCodexButton.Click += delegate { CloseCodex(); };
            Controls.Add(closeCodexButton);
            floatingBallButton = MakeButton("开启悬浮球", 464, 618, 120, 31,
                Color.FromArgb(225, 244, 255), Color.FromArgb(20, 112, 183));
            floatingBallButton.Font = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Bold);
            floatingBallButton.AccessibleName = "开启后台悬浮球";
            floatingBallButton.Click += delegate { ToggleFloatingBall(); };
            Controls.Add(floatingBallButton);
            versionLabel = AddLabel(this, "本机保存  /  v1.8.1", 594, 624, 232, 23, 9F, FontStyle.Regular, Muted);
            versionLabel.TextAlign = ContentAlignment.MiddleRight;

            using (Graphics graphics = CreateGraphics())
            {
                uiScale = graphics.DpiX / 96F;
                if (Math.Abs(uiScale - 1F) > 0.01F) Scale(new SizeF(uiScale, uiScale));
            }
            timer = new Timer { Interval = 1000 };
            timer.Tick += OnTick;
            floatingBall = new FloatingBallForm(RestoreMainWindow, StartCodex, CloseCodex,
                DisableFloatingBall, ExitManager);
            ApplyFloatingBallVisibility(true);
            UpdateFloatingBallButton();
            Shown += delegate { RefreshStatus(); timer.Start(); };
            Resize += OnMainWindowResize;
            FormClosed += delegate {
                timer.Dispose();
                if (floatingBall != null) { floatingBall.Dispose(); floatingBall = null; }
            };
            FormClosing += OnMainFormClosing;
        }

        internal static bool ShouldOfferCloseChoice(CloseReason reason, bool exiting)
        {
            return !exiting && reason == CloseReason.UserClosing;
        }

        internal static bool ShouldShowFloatingBall(bool mainWindowVisible, bool floatingBallEnabled)
        {
            return !mainWindowVisible && floatingBallEnabled;
        }

        private void OnMainWindowResize(object sender, EventArgs e)
        {
            if (exiting || floatingBall == null) return;
            if (WindowState == FormWindowState.Minimized)
            {
                ShowInTaskbar = true;
                ApplyFloatingBallVisibility(false);
            }
            else if (Visible)
            {
                ApplyFloatingBallVisibility(true);
            }
        }

        private void OnMainFormClosing(object sender, FormClosingEventArgs e)
        {
            if (performing)
            {
                e.Cancel = true;
                return;
            }
            if (ShouldOfferCloseChoice(e.CloseReason, exiting))
            {
                ManagerCloseAction action = ManagerCloseDialog.ShowChoice(this);
                if (action == ManagerCloseAction.Background)
                {
                    e.Cancel = true;
                    HideToBackground();
                }
                else if (action == ManagerCloseAction.Exit)
                {
                    PrepareExit();
                }
                else
                {
                    e.Cancel = true;
                }
            }
        }

        private void HideToBackground()
        {
            ShowInTaskbar = false;
            Hide();
            ApplyFloatingBallVisibility(false);
        }

        private void RestoreMainWindow()
        {
            ShowInTaskbar = true;
            if (!Visible) Show();
            WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
            ApplyFloatingBallVisibility(true);
        }

        internal void RestoreFromExternalRequest()
        {
            if (exiting || IsDisposed) return;
            RestoreMainWindow();
        }

        private void ToggleFloatingBall()
        {
            if (floatingBall == null) return;
            floatingBall.SetEnabled(!floatingBall.IsEnabled);
            // The ball represents background mode only; it never overlays the main window.
            ApplyFloatingBallVisibility(true);
        }

        private void DisableFloatingBall()
        {
            if (floatingBall == null) return;
            floatingBall.SetEnabled(false);
            UpdateFloatingBallButton();
        }

        private void ApplyFloatingBallVisibility(bool mainWindowVisible)
        {
            if (floatingBall == null) return;
            if (ShouldShowFloatingBall(mainWindowVisible, floatingBall.IsEnabled)) floatingBall.ShowBall();
            else floatingBall.Hide();
            UpdateFloatingBallButton();
        }

        private void UpdateFloatingBallButton()
        {
            if (floatingBallButton == null || floatingBall == null) return;
            bool enabled = floatingBall.IsEnabled;
            floatingBallButton.Text = enabled ? "关闭悬浮球" : "开启悬浮球";
            floatingBallButton.AccessibleName = floatingBallButton.Text;
        }

        private void ExitManager()
        {
            IWin32Window owner = floatingBall != null && floatingBall.Visible
                ? (IWin32Window)floatingBall : this;
            DialogResult result = MessageBox.Show(owner,
                "确定退出 ChatGPT 管理器吗？\n\n退出管理器不会关闭 ChatGPT。",
                "退出管理器", MessageBoxButtons.OKCancel, MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2);
            if (result != DialogResult.OK) return;
            PrepareExit();
            Close();
        }

        private void PrepareExit()
        {
            exiting = true;
            timer.Stop();
            if (floatingBall != null) floatingBall.Hide();
        }

        private IWin32Window DialogOwner()
        {
            return Visible ? (IWin32Window)this : (IWin32Window)floatingBall;
        }

        private static Label AddLabel(Control parent, string text, int x, int y, int w, int h, float font, FontStyle style, Color color)
        {
            Label label = new Label { Text = text, Location = new Point(x, y), Size = new Size(w, h),
                ForeColor = color, BackColor = Color.Transparent, Font = new Font("Microsoft YaHei UI", font, style),
                TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
            parent.Controls.Add(label); return label;
        }
        private static Button MakeButton(string text, int x, int y, int w, int h, Color background, Color foreground)
        {
            Button button = new Button { Text = text, AccessibleName = text, Location = new Point(x, y), Size = new Size(w, h),
                FlatStyle = FlatStyle.Flat, BackColor = background, ForeColor = foreground,
                Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold), Cursor = Cursors.Hand,
                UseVisualStyleBackColor = false };
            button.FlatAppearance.BorderSize = 0; return button;
        }
        private static Button MakeLink(string text, int x, int y, int w, int h)
        {
            Button button = MakeButton(text, x, y, w, h, Color.Transparent, Muted);
            button.Font = new Font("Microsoft YaHei UI", 9F);
            return button;
        }
        private int Scaled(int value) { return (int)Math.Round(value * uiScale); }
        private void OpenFolder(string folder)
        {
            if (CodexProcesses.AnyRunning())
            {
                MessageBox.Show(this, "ChatGPT 正在运行，已阻止打开配置目录。请完全退出 ChatGPT 后重试。", "目录操作已阻止",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                RefreshStatus();
                return;
            }
            if (Directory.Exists(folder)) Process.Start("explorer.exe", "\"" + folder + "\"");
            else MessageBox.Show(this, "该目录尚不存在：\n" + folder, "ChatGPT 管理器", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        private void SaveTransitApiKey()
        {
            if (performing || pending != ConfigMode.Unknown) return;
            if (CodexProcesses.AnyRunning())
            {
                MessageBox.Show(this, "ChatGPT 正在运行，已阻止保存密钥。请完全退出 ChatGPT 后重试。", "保存已阻止",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                RefreshStatus();
                return;
            }

            bool transitSaved = false;
            try
            {
                ConfigMode current = ConfigFiles.Detect(paths);
                bool activeTransit = TransitActivationState.IsActiveOrDetected(paths, current);
                bool transitChanged = TransitCredentialStore.SaveApiKey(paths.Transit, transitApiKeyInput.Text);
                transitSaved = true;
                bool activeChanged = false;
                if (activeTransit)
                    activeChanged = TransitCredentialStore.SyncApiKey(paths.Transit, paths.Target);
                transitApiKeyInput.Clear();
                persistentMessage = true;
                string detail = activeTransit
                    ? (activeChanged
                        ? "Transit 和当前中转配置的密钥均已更新；密钥内容不会显示。"
                        : "新密钥已保存，并已核对当前中转配置；密钥内容不会显示。")
                    : "新密钥已保存到本机 Transit\\auth.json；不会显示密钥内容。";
                SetActivity(transitChanged || activeChanged ? "API 密钥已更新" : "API 密钥已核对", detail, Purple);
                RefreshStatus();
                MessageBox.Show(this, activeTransit
                    ? "新 API 密钥已保存到 Transit，并已同步到当前中转配置。"
                    : "新 API 密钥已保存到本机 Transit 配置文件。", "保存完成",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (IOException ex)
            {
                string message = transitSaved
                    ? "Transit 中的新密钥已保存，但同步到当前 ChatGPT 配置失败。请检查目标路径，然后点“切换到中转”重试。\n"
                    : String.Empty;
                MessageBox.Show(this, message + ex.Message, "无法完成 API 密钥更新",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                transitApiKeyInput.Focus();
            }
            catch (Exception)
            {
                MessageBox.Show(this, "保存失败。请检查 Transit 目录权限后重试。", "无法保存 API 密钥",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            RefreshStatus();
        }
        private void OpenTransitWebsite()
        {
            OpenExternalPage(TransitWebsiteUrl, "进入主页");
        }
        private void OpenTransitWallet()
        {
            OpenExternalPage(TransitWalletUrl, "查看余额/充值");
        }
        private void OpenTransitSignup()
        {
            OpenExternalPage(TransitSignupUrl, "点我注册（个人主页）");
        }
        private void OpenExternalPage(string url, string title)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception)
            {
                MessageBox.Show(this, "无法打开默认浏览器，请手动访问：\n" + url,
                    title, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        private void StartCodex()
        {
            if (performing || pending != ConfigMode.Unknown) return;
            if (CodexProcesses.AnyRunning())
            {
                RefreshStatus();
                SetActivity("ChatGPT 已在运行", "无需重复启动。", Teal);
                persistentMessage = true;
                return;
            }
            try
            {
                CodexDesktopControl.Start();
                SetActivity("已请求启动 ChatGPT", "Windows 正在打开 ChatGPT 桌面应用。", Teal);
                persistentMessage = true;
            }
            catch (Exception ex)
            {
                SetActivity("ChatGPT 启动失败", "请从开始菜单手动打开 ChatGPT。", Color.FromArgb(164, 101, 17));
                persistentMessage = true;
                MessageBox.Show(this, "无法启动 ChatGPT：\n" + ex.Message + "\n\n请从 Windows 开始菜单手动打开 ChatGPT。",
                    "启动失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            RefreshStatus();
        }
        private void CloseCodex()
        {
            if (performing || pending != ConfigMode.Unknown) return;
            if (!CodexProcesses.AnyRunning())
            {
                RefreshStatus();
                SetActivity("ChatGPT 已关闭", "现在可以安全保存密钥或切换配置。", Teal);
                persistentMessage = true;
                return;
            }
            DialogResult confirmation = MessageBox.Show(DialogOwner(),
                "后台关闭会先请求 ChatGPT 正常退出，然后结束仍未退出的 ChatGPT 后台进程。\n\n未保存的会话状态可能丢失。确定继续吗？",
                "确认后台关闭 ChatGPT", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (confirmation != DialogResult.OK) return;
            try
            {
                int requested = CodexDesktopControl.RequestGracefulClose();
                if (requested > 0) System.Threading.Thread.Sleep(600);
                int terminated = CodexDesktopControl.TerminateRemainingProcesses();
                System.Threading.Thread.Sleep(300);
                bool stillRunning = CodexProcesses.AnyRunning();
                if (!stillRunning)
                {
                    SetActivity("ChatGPT 已在后台关闭",
                        terminated > 0 ? "正常退出后仍存在的后台进程已结束。" : "ChatGPT 已响应正常退出请求。", Teal);
                    persistentMessage = true;
                    MessageBox.Show(DialogOwner(), "ChatGPT 桌面窗口和后台进程均已关闭。",
                        "后台关闭完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    SetActivity("ChatGPT 尚未完全关闭", "仍有无法结束的进程，请检查权限或在任务管理器中处理。",
                        Color.FromArgb(164, 101, 17));
                    persistentMessage = true;
                    MessageBox.Show(DialogOwner(),
                        "仍检测到 ChatGPT 进程。请检查是否有更高权限运行的 ChatGPT，或在任务管理器中处理。",
                        "后台关闭未完全完成", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            catch (Exception ex)
            {
                SetActivity("无法关闭 ChatGPT", "请在 ChatGPT 中正常退出。", Color.FromArgb(164, 101, 17));
                persistentMessage = true;
                MessageBox.Show(DialogOwner(), "后台关闭失败：\n" + ex.Message,
                    "无法关闭 ChatGPT", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            RefreshStatus();
        }
        private void RefreshStatus()
        {
            if (performing) return;
            ConfigMode mode = ConfigFiles.Detect(paths);
            try { TransitActivationState.SyncWithCurrentMode(paths, mode); } catch { }
            modeLabel.Text = DisplayMode(mode);
            modeLabel.AccessibleName = "当前配置：" + modeLabel.Text;
            modeLabel.ForeColor = mode == ConfigMode.Official ? Teal : mode == ConfigMode.Transit ? Purple : Color.FromArgb(164, 101, 17);
            bool running = CodexProcesses.AnyRunning();
            processLabel.Text = running ? "●  ChatGPT 正在运行，密钥保存和配置切换均已禁用。" : "●  ChatGPT 已关闭，可以保存密钥或切换配置。";
            processLabel.ForeColor = running ? Color.FromArgb(190, 45, 45) : Muted;
            processLabel.Font = running ? ProcessWarningFont : ProcessNormalFont;
            bool canChange = !running && pending == ConfigMode.Unknown && !performing;
            officialButton.Enabled = canChange;
            transitButton.Enabled = canChange;
            transitApiKeyInput.Enabled = canChange;
            saveTransitKeyButton.Enabled = canChange;
            officialFolderButton.Enabled = canChange;
            backupFolderButton.Enabled = canChange;
            bool canManageCodex = pending == ConfigMode.Unknown && !performing;
            startCodexButton.Enabled = canManageCodex && !running;
            closeCodexButton.Enabled = canManageCodex && running;
            officialFiles.Text = PairDescription(paths.Official);
            UpdateTransitKeyControls();
        }
        private string DisplayMode(ConfigMode mode)
        {
            if (mode != ConfigMode.Unknown) return ConfigFiles.ModeName(mode);
            try
            {
                ConfigFiles.RequirePair(paths.Target);
                return "待首次切换";
            }
            catch { return "等待配置文件"; }
        }
        private void UpdateTransitKeyControls()
        {
            bool configured = TransitCredentialStore.IsConfigured(paths.Transit);
            transitKeyStatus.Text = configured
                ? "已设置，可输入新密钥替换" : "尚未设置 API 密钥";
            transitApiKeyInput.AccessibleName = configured
                ? "输入新的 API 密钥以替换现有中转密钥"
                : "请输入您的 API 密钥";
            saveTransitKeyButton.Text = configured
                ? (transitApiKeyInput.TextLength > 0 ? "保存新密钥" : "替换密钥")
                : "保存密钥";
            saveTransitKeyButton.AccessibleName = configured
                ? "保存并替换本机 Transit API 密钥"
                : "保存 API 密钥到本机 Transit 配置";
        }
        private static string PairDescription(string folder)
        {
            try { ConfigFiles.RequirePair(folder); return "当前配置已安全保存"; }
            catch { }
            bool hasFiles = ConfigFiles.Names.Any(name => File.Exists(Path.Combine(folder, name)));
            return hasFiles ? "已有文件不完整" : "首次切换中转时自动保存";
        }
        private void SetEnabled(bool enabled)
        {
            bool canChange = enabled && !CodexProcesses.AnyRunning();
            officialButton.Enabled = canChange;
            transitButton.Enabled = canChange;
            transitApiKeyInput.Enabled = canChange;
            saveTransitKeyButton.Enabled = canChange;
            officialFolderButton.Enabled = canChange;
            backupFolderButton.Enabled = canChange;
            startCodexButton.Enabled = enabled && !CodexProcesses.AnyRunning();
            closeCodexButton.Enabled = enabled && CodexProcesses.AnyRunning();
            cancelButton.Visible = !enabled && !performing;
        }
        private void SetActivity(string title, string detail, Color color)
        {
            activityTitle.Text = title; activityTitle.ForeColor = color; activityDetail.Text = detail;
            activityTitle.Refresh(); activityDetail.Refresh();
        }
        private void QueueSwitch(ConfigMode mode)
        {
            if (performing || pending != ConfigMode.Unknown) return;
            if (CodexProcesses.AnyRunning())
            {
                MessageBox.Show(this, "ChatGPT 正在运行，已阻止配置切换。请完全退出 ChatGPT 后重试。", "切换已阻止",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                RefreshStatus();
                return;
            }
            try
            {
                ConfigFiles.RequirePair(mode == ConfigMode.Official ? paths.Official : paths.Transit);
                if (mode == ConfigMode.Transit && ConfigFiles.Equal(paths.Official, paths.Transit))
                {
                    const string reminder = "Transit 与 Official 内容相同。请检查已保存的 API 密钥后重试。";
                    SetActivity("中转配置尚未生效", "Transit 与 Official 当前相同。", Color.FromArgb(164, 101, 17));
                    persistentMessage = true;
                    MessageBox.Show(this, reminder, "请检查中转配置", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                if (mode == ConfigMode.Transit && !TransitCredentialStore.IsConfigured(paths.Transit))
                {
                    const string reminder = "请先在中转区域输入并保存您的 API 密钥。";
                    SetActivity("尚未设置 API 密钥", "保存密钥后即可切换到中转。", Color.FromArgb(164, 101, 17));
                    persistentMessage = true;
                    transitApiKeyInput.Focus();
                    MessageBox.Show(this, reminder, "请先设置 API 密钥", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                pending = mode; stoppedSince = null; persistentMessage = false;
                SetEnabled(false);
                SetActivity("准备切换到" + ConfigFiles.ModeName(mode), "请保持 ChatGPT 完全关闭。确认关闭后才会复制配置文件。", Ink);
                OnTick(this, EventArgs.Empty);
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "无法开始切换", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }
        private void OnTick(object sender, EventArgs e)
        {
            UpdateCompanionDuration();
            if (performing) return;
            tick++;
            if (pending == ConfigMode.Unknown)
            {
                if (tick % 2 == 0) RefreshStatus();
                if (!persistentMessage) cancelButton.Visible = false;
                return;
            }
            if (CodexProcesses.AnyRunning())
            {
                pending = ConfigMode.Unknown; stoppedSince = null; SetEnabled(true);
                SetActivity("切换已阻止", "检测到 ChatGPT 正在运行，没有开始复制配置文件。", Color.FromArgb(164, 101, 17));
                persistentMessage = true;
                MessageBox.Show(this, "ChatGPT 已在切换前启动。没有复制配置文件，请退出 ChatGPT 后重新操作。",
                    "切换已阻止", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (!stoppedSince.HasValue) stoppedSince = DateTime.UtcNow;
            SetActivity("ChatGPT 已关闭 · 正在准备复制", "等待退出完成，然后显示 Windows 原生覆盖确认框。", Ink);
            if ((DateTime.UtcNow - stoppedSince.Value).TotalSeconds >= 2) ExecuteSwitch();
        }
        internal static string FormatCompanionDuration(DateTimeOffset currentTime)
        {
            TimeSpan elapsed = currentTime - CompanionStart;
            if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
            return String.Format("已陪伴用户 {0} 天 {1} 小时 {2} 分钟",
                elapsed.Days, elapsed.Hours, elapsed.Minutes);
        }
        private void UpdateCompanionDuration()
        {
            companionDurationLabel.Text = FormatCompanionDuration(DateTimeOffset.Now);
        }
        private void ExecuteSwitch()
        {
            ConfigMode destination = pending;
            pending = ConfigMode.Unknown; performing = true; timer.Stop(); cancelButton.Visible = false;
            SwitchResult result;
            try
            {
                var service = new SwitchService(paths, new WindowsConfirmedCopy(), CodexProcesses.AnyRunning);
                result = service.SwitchTo(destination, Handle, delegate(string message) {
                    SetActivity("正在切换到" + ConfigFiles.ModeName(destination), message, Ink);
                });
            }
            catch (Exception ex) { result = new SwitchResult(false, ex.Message); }
            finally { performing = false; SetEnabled(true); timer.Start(); }
            RefreshStatus(); persistentMessage = true;
            SetActivity(result.Success ? "切换成功 · 当前为" + ConfigFiles.ModeName(destination) : "切换未完成",
                result.Success ? result.Message.Split('\n')[0] : "请查看提示。确认两个文件都完成覆盖后，再打开 ChatGPT。",
                result.Success ? Teal : Color.FromArgb(164, 101, 17));
            MessageBox.Show(this, result.Message, result.Success ? "切换成功" : "切换未完成",
                MessageBoxButtons.OK, result.Success ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
    }
}
