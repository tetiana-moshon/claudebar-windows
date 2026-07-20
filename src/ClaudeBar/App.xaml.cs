using System.Windows;
using ClaudeBar.Services;
using ClaudeBar.ViewModels;
using ClaudeBar.Views;

namespace ClaudeBar;

public partial class App : Application
{
    private static System.Threading.Mutex? _singleInstanceMutex;

    public UsageStore Store { get; private set; } = null!;
    public AutoUpdater Updater { get; private set; } = null!;
    private TrayIconManager? _tray;

    public static new App Current => (App)Application.Current;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // A tray app has no console; funnel unhandled exceptions to a log so failures are
        // diagnosable instead of vanishing.
        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash(args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogCrash(args.ExceptionObject as Exception);

        // Single instance: a menubar/tray app should never run twice.
        _singleInstanceMutex = new System.Threading.Mutex(initiallyOwned: true, "ClaudeBar.SingleInstance", out var isNew);
        if (!isNew)
        {
            Shutdown();
            return;
        }

        Store = new UsageStore();
        Updater = new AutoUpdater();
        _tray = new TrayIconManager(Store, Updater);
    }

    private static void LogCrash(Exception? ex)
    {
        if (ex is null) return;
        try
        {
            var path = System.IO.Path.Combine(Services.AppPaths.DataDir, "error.log");
            System.IO.File.AppendAllText(path, $"[{DateTime.Now:o}] {ex}\n\n");
        }
        catch { /* nothing more we can do */ }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        Store?.Dispose();
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
