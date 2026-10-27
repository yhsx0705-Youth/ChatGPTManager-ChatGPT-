using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace ChatGPTManager
{
    internal sealed class FloatingBallState
    {
        public int Size { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public bool? Enabled { get; set; }
    }

    internal static class FloatingBallPreferences
    {
        private const int DefaultSize = 104;

        private static string StatePath
        {
            get
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return Path.Combine(local, "ChatGPTManager", "floating-ball.json");
            }
        }

        public static FloatingBallState Load()
        {
            try
            {
                string path = StatePath;
                string legacy = null;
                if (!File.Exists(path))
                {
                    string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    legacy = Path.Combine(local, "CodexSwitch", "floating-ball.json");
                    if (File.Exists(legacy)) path = legacy;
                }
                if (!File.Exists(path)) return new FloatingBallState { Size = DefaultSize, X = Int32.MinValue, Y = Int32.MinValue };
                FloatingBallState state = new JavaScriptSerializer().Deserialize<FloatingBallState>(File.ReadAllText(path));
                if (state == null) throw new IOException();
                state.Size = NormalizeSize(state.Size);
                if (legacy != null && String.Equals(path, legacy, StringComparison.OrdinalIgnoreCase))
                {
                    Save(state);
                    try
                    {
                        if ((File.GetAttributes(legacy) & FileAttributes.ReparsePoint) == 0) File.Delete(legacy);
                        string legacyDirectory = Path.GetDirectoryName(legacy);
                        if (Directory.Exists(legacyDirectory)
                            && (File.GetAttributes(legacyDirectory) & FileAttributes.ReparsePoint) == 0
                            && Directory.GetFileSystemEntries(legacyDirectory).Length == 0)
                            Directory.Delete(legacyDirectory, false);
                    }
                    catch { }
                }
                return state;
            }
            catch
            {
                return new FloatingBallState { Size = DefaultSize, X = Int32.MinValue, Y = Int32.MinValue };
            }
        }

        public static void Save(FloatingBallState state)
        {
            try
            {
                string path = StatePath;
                string directory = Path.GetDirectoryName(path);
                Directory.CreateDirectory(directory);
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return;
                state.Size = NormalizeSize(state.Size);
                File.WriteAllText(path, new JavaScriptSerializer().Serialize(state), new UTF8Encoding(false));
            }
            catch { }
        }

        internal static int NormalizeSize(int size)
        {
            // Transparently migrate the three sizes stored by versions 1.7.0-1.7.2.
            if (size == 40) return 80;
            if (size == 52) return 104;
            if (size == 64) return 128;
            return size == 80 || size == 104 || size == 128 ? size : DefaultSize;
        }

        internal static bool NormalizeEnabled(bool? enabled)
        {
            // Versions through 1.8.0 did not persist this preference and always
            // displayed the ball in background mode. Preserve that behavior once.
            return enabled ?? true;
        }
    }

    internal sealed class FloatingBallForm : Form
    {
        private readonly Action restoreMainWindow;
        private readonly Action startChatGpt;
        private readonly Action closeChatGpt;
        private readonly Action disableFloatingBall;
        private readonly Action exitManager;
        private readonly FloatingBallState state;
        private Image ballMark;
        private Point mouseDownScreen;
        private Point formDownLocation;
        private bool dragging;

        public FloatingBallForm(Action restoreMainWindow, Action startChatGpt,
            Action closeChatGpt, Action disableFloatingBall, Action exitManager)
        {
            this.restoreMainWindow = restoreMainWindow;
            this.startChatGpt = startChatGpt;
            this.closeChatGpt = closeChatGpt;
            this.disableFloatingBall = disableFloatingBall;
            this.exitManager = exitManager;
            state = FloatingBallPreferences.Load();

            Text = "ChatGPT 管理器悬浮球";
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            AutoSize = false;
            Padding = Padding.Empty;
            Margin = Padding.Empty;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.Magenta;
            TransparencyKey = Color.Magenta;
            DoubleBuffered = true;
            Cursor = Cursors.Hand;
            try
            {
                using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ChatGPTManager.FloatingBallMark.png"))
                {
                    if (stream != null)
                    {
                        using (Image source = Image.FromStream(stream)) ballMark = new Bitmap(source);
                    }
                }
            }
            catch { }
            ApplySize(state.Size);
            Location = new Point(state.X, state.Y);
            ContextMenuStrip = BuildMenu();
            MouseDown += OnBallMouseDown;
            MouseMove += OnBallMouseMove;
            MouseUp += OnBallMouseUp;
            Shown += delegate { ApplySize(state.Size); EnsureVisible(); };
        }

        private ContextMenuStrip BuildMenu()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("打开 ChatGPT 管理器", null, delegate { restoreMainWindow(); });
            menu.Items.Add("启动 ChatGPT", null, delegate { startChatGpt(); });
            menu.Items.Add("后台关闭 ChatGPT", null, delegate { closeChatGpt(); });
            menu.Items.Add(new ToolStripSeparator());
            var sizeMenu = new ToolStripMenuItem("悬浮球大小");
            sizeMenu.DropDownItems.Add("小（80）", null, delegate { ApplySize(80); SaveState(); });
            sizeMenu.DropDownItems.Add("中（104）", null, delegate { ApplySize(104); SaveState(); });
            sizeMenu.DropDownItems.Add("大（128）", null, delegate { ApplySize(128); SaveState(); });
            menu.Items.Add(sizeMenu);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("关闭悬浮球", null, delegate { disableFloatingBall(); });
            menu.Items.Add("退出管理器", null, delegate { exitManager(); });
            return menu;
        }

        internal bool IsEnabled
        {
            get { return FloatingBallPreferences.NormalizeEnabled(state.Enabled); }
        }

        internal void SetEnabled(bool enabled)
        {
            state.Enabled = enabled;
            if (!enabled) Hide();
            SaveState();
        }

        private void ApplySize(int size)
        {
            state.Size = FloatingBallPreferences.NormalizeSize(size);
            Size square = new Size(state.Size, state.Size);
            MinimumSize = Size.Empty;
            MaximumSize = Size.Empty;
            ClientSize = square;
            SetBounds(Left, Top, square.Width, square.Height, BoundsSpecified.Size);
            MinimumSize = square;
            MaximumSize = square;
            using (var path = new GraphicsPath())
            {
                path.AddEllipse(0, 0, Width - 1, Height - 1);
                Region oldRegion = Region;
                Region = new Region(path);
                if (oldRegion != null) oldRegion.Dispose();
            }
            EnsureVisible();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.CompositingQuality = CompositingQuality.HighQuality;
            using (var fill = new LinearGradientBrush(ClientRectangle,
                Color.FromArgb(224, 244, 255), Color.FromArgb(255, 255, 255), LinearGradientMode.Vertical))
                e.Graphics.FillEllipse(fill, 1, 1, Width - 3, Height - 3);
            using (var border = new Pen(Color.FromArgb(66, 153, 218), Math.Max(2F, Width / 42F)))
                e.Graphics.DrawEllipse(border, 2, 2, Width - 5, Height - 5);
            if (ballMark != null)
            {
                e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                int inset = Math.Max(12, Width / 6);
                e.Graphics.DrawImage(ballMark, new Rectangle(inset, inset, Width - inset * 2, Height - inset * 2));
            }
        }

        private void OnBallMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            mouseDownScreen = Cursor.Position;
            formDownLocation = Location;
            dragging = false;
        }

        private void OnBallMouseMove(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            Point current = Cursor.Position;
            int dx = current.X - mouseDownScreen.X;
            int dy = current.Y - mouseDownScreen.Y;
            if (Math.Abs(dx) > 3 || Math.Abs(dy) > 3) dragging = true;
            if (dragging) Location = new Point(formDownLocation.X + dx, formDownLocation.Y + dy);
        }

        private void OnBallMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            if (dragging)
            {
                SnapToCurrentScreen();
                SaveState();
            }
            else restoreMainWindow();
            dragging = false;
        }

        internal void ShowBall()
        {
            EnsureVisible();
            if (!Visible) Show();
            BringToFront();
        }

        private void EnsureVisible()
        {
            Rectangle ball = new Rectangle(Location, Size);
            bool visible = false;
            foreach (Screen screen in Screen.AllScreens)
            {
                if (screen.WorkingArea.IntersectsWith(ball)) { visible = true; break; }
            }
            if (!visible || state.X == Int32.MinValue || state.Y == Int32.MinValue)
            {
                Rectangle work = Screen.PrimaryScreen.WorkingArea;
                Location = new Point(work.Right - Width - 24, work.Bottom - Height - 24);
            }
            else
            {
                SnapToCurrentScreen();
            }
        }

        private void SnapToCurrentScreen()
        {
            Point center = new Point(Left + Width / 2, Top + Height / 2);
            Rectangle work = Screen.FromPoint(center).WorkingArea;
            Location = SnapToEdges(Location, Size, work, Math.Max(32, Width / 3));
        }

        internal static Point SnapToEdges(Point proposed, Size ballSize, Rectangle workArea, int threshold)
        {
            int right = Math.Max(workArea.Left, workArea.Right - ballSize.Width);
            int bottom = Math.Max(workArea.Top, workArea.Bottom - ballSize.Height);
            int x = Math.Max(workArea.Left, Math.Min(proposed.X, right));
            int y = Math.Max(workArea.Top, Math.Min(proposed.Y, bottom));
            int snap = Math.Max(0, threshold);

            if (Math.Abs(x - workArea.Left) <= snap) x = workArea.Left;
            else if (Math.Abs(right - x) <= snap) x = right;
            if (Math.Abs(y - workArea.Top) <= snap) y = workArea.Top;
            else if (Math.Abs(bottom - y) <= snap) y = bottom;
            return new Point(x, y);
        }

        private void SaveState()
        {
            state.X = Location.X;
            state.Y = Location.Y;
            FloatingBallPreferences.Save(state);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (ContextMenuStrip != null) ContextMenuStrip.Dispose();
                if (ballMark != null) { ballMark.Dispose(); ballMark = null; }
            }
            base.Dispose(disposing);
        }
    }
}
