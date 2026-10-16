using System;
using System.IO;
using System.Windows;

namespace CueGlass;

public sealed class App : Application
{
    [STAThread]
    public static void Main(string[] args)
    {
        var app = new App();
        bool diagnostic = args.Length > 0 && (args[0] == "--self-test" || args[0] == "--visual-check");
        string? diagnosticDestination = diagnostic ? Path.GetFullPath(args.Length > 1 ? args[1] : Path.Combine(AppContext.BaseDirectory, "self-test.json")) : null;
        app.DispatcherUnhandledException += (_, e) =>
        {
            if (diagnostic)
            {
                File.WriteAllText(diagnosticDestination + ".fatal.txt", e.Exception.ToString());
                e.Handled = true; app.Shutdown(1); return;
            }
            MessageBox.Show(e.Exception.Message, "CueGlass", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };
        if (diagnostic)
        {
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var destination = diagnosticDestination!;
            app.Startup += async (_, _) =>
            {
                int code = args[0] == "--visual-check" ? await VisualCheck.Run(destination) : await SelfTest.Run(destination);
                app.Shutdown(code);
            };
            app.Run();
            return;
        }
        app.Run(new MainWindow());
    }
}
