using System.Diagnostics;
using System.Threading;
using System.Windows.Forms;
using System.IO;
using System;

// Disposable test process; never used by the application.
internal static class GameFixture
{
    [STAThread]
    static void Main(string[] args)
    {
        try
        {
        if (args.Length > 1)
            Process.Start(new ProcessStartInfo(args[1], "--fixture") { UseShellExecute = false, CreateNoWindow = true });
        var form = new Form { ShowInTaskbar = true, Opacity = 0.01, Text = "Disposable Game Fixture", StartPosition = FormStartPosition.Manual, Location = new System.Drawing.Point(-32000, -32000), Size = new System.Drawing.Size(20, 20) };
        form.Shown += delegate { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ready-" + Process.GetCurrentProcess().Id + ".txt"), "Ready"); };
        form.FormClosing += delegate(object sender, FormClosingEventArgs e) {
            if (File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "hold-" + Process.GetCurrentProcess().Id + ".txt"))) { e.Cancel = true; return; }
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "saved-" + Process.GetCurrentProcess().Id + ".txt"), "Saved normally before exit");
        };
        Application.Run(form);
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "error-" + Process.GetCurrentProcess().Id + ".txt"), ex.ToString()); }
    }
}
