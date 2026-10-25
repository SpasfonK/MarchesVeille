using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using MarchesVeille.Core.Cli;
using MarchesVeille.Core.Util;

namespace MarchesVeille.Desktop;

public partial class App : Application
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (CliRunner.IsCliInvocation(e.Args))
        {
            // Mode batch : pas de fenêtre. On se rattache à la console parente (si elle existe)
            // et on journalise aussi dans marches-scrap.log à côté de la base.
            AttachConsole(-1);
            var dataDir = AppPaths.DataDir;
            var utf8 = new UTF8Encoding(false);
            using var stdout = new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true };
            using var log = new StreamWriter(Path.Combine(dataDir, "marches-scrap.log"), true, utf8) { AutoFlush = true };
            var tee = new TeeWriter(stdout, log);
            var args = e.Args;
            int code;
            try { code = Task.Run(() => CliRunner.RunAsync(args, tee, dataDir)).GetAwaiter().GetResult(); }
            catch (Exception ex)
            {
                tee.WriteLine("Erreur fatale : " + ex);
                code = 1;
            }
            Shutdown(code);
            return;
        }

        new MainWindow().Show();
    }
}
