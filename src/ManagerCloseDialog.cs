using System;
using System.Drawing;
using System.Windows.Forms;

namespace ChatGPTManager
{
    internal enum ManagerCloseAction
    {
        Cancel,
        Background,
        Exit
    }

    internal sealed class ManagerCloseDialog : Form
    {
        private ManagerCloseDialog()
        {
            Text = "关闭 ChatGPT Manager";
            ClientSize = new Size(500, 242);
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Color.FromArgb(247, 250, 254);
            Font = new Font("Microsoft YaHei UI", 9F);

            var title = new Label {
                Text = "关闭窗口后要做什么？",
                Location = new Point(28, 22), Size = new Size(444, 34),
                Font = new Font("Microsoft YaHei UI", 14F, FontStyle.Bold),
                ForeColor = Color.FromArgb(26, 55, 91)
            };
            var detail = new Label {
                Text = "后台运行：隐藏主窗口，并按主页设置显示悬浮球。\r\n完全关闭：只退出管理器，不会关闭 ChatGPT。",
                Location = new Point(29, 62), Size = new Size(442, 52),
                Font = new Font("Microsoft YaHei UI", 9F),
                ForeColor = Color.FromArgb(90, 112, 138),
                AutoEllipsis = false
            };
            var background = MakeButton("后台运行", 28, 128, 210, 48,
                Color.FromArgb(225, 244, 255), Color.FromArgb(20, 112, 183));
            background.DialogResult = DialogResult.Yes;
            background.AccessibleName = "关闭窗口并在后台运行";
            var exit = MakeButton("完全关闭", 262, 128, 210, 48,
                Color.FromArgb(255, 237, 237), Color.FromArgb(177, 45, 45));
            exit.DialogResult = DialogResult.No;
            exit.AccessibleName = "完全关闭 ChatGPT Manager";
            var cancel = new Button {
                Text = "取消", DialogResult = DialogResult.Cancel,
                Location = new Point(205, 194), Size = new Size(90, 32),
                FlatStyle = FlatStyle.Flat, BackColor = Color.Transparent,
                ForeColor = Color.FromArgb(90, 112, 138), Cursor = Cursors.Hand
            };
            cancel.FlatAppearance.BorderSize = 0;

            Controls.Add(title);
            Controls.Add(detail);
            Controls.Add(background);
            Controls.Add(exit);
            Controls.Add(cancel);
            AcceptButton = background;
            CancelButton = cancel;
        }

        internal static ManagerCloseAction ShowChoice(IWin32Window owner)
        {
            using (var dialog = new ManagerCloseDialog())
            {
                DialogResult result = dialog.ShowDialog(owner);
                if (result == DialogResult.Yes) return ManagerCloseAction.Background;
                if (result == DialogResult.No) return ManagerCloseAction.Exit;
                return ManagerCloseAction.Cancel;
            }
        }

        private static Button MakeButton(string text, int x, int y, int width, int height,
            Color background, Color foreground)
        {
            var button = new Button {
                Text = text, Location = new Point(x, y), Size = new Size(width, height),
                FlatStyle = FlatStyle.Flat, BackColor = background, ForeColor = foreground,
                Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold),
                Cursor = Cursors.Hand, UseVisualStyleBackColor = false
            };
            button.FlatAppearance.BorderSize = 0;
            return button;
        }
    }
}
