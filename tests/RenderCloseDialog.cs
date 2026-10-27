using System;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

internal static class RenderCloseDialog
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 2) return 2;
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Assembly assembly = Assembly.LoadFrom(args[0]);
        Type type = assembly.GetType("ChatGPTManager.ManagerCloseDialog", true);
        using (Form dialog = (Form)Activator.CreateInstance(type, true))
        {
            dialog.StartPosition = FormStartPosition.Manual;
            dialog.Location = new Point(-10000, -10000);
            dialog.Show();
            Application.DoEvents();
            using (var image = new Bitmap(dialog.Width, dialog.Height))
            {
                dialog.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
                image.Save(args[1], System.Drawing.Imaging.ImageFormat.Png);
            }
            dialog.Hide();
        }
        return 0;
    }
}
