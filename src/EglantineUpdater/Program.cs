using EglantineUpdater.Core;
using Microsoft.Toolkit.Uwp.Notifications;

namespace EglantineUpdater;

internal static class Program
{
    public const string AppName = "Eglantine Updater";
    private const string MutexName = @"Local\EglantineUpdater.Instance";
    private const string ShowSignalName = @"Local\EglantineUpdater.Show";

    /// <summary>
    /// Command line:
    ///   (none)         start; on first run asks for the Eglantine folder
    ///   --background   start quietly (used by "Start with Windows")
    ///   --uninstall    remove startup entry, toast registration and settings
    /// </summary>
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Any(a => a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase)))
        {
            Uninstall();
            return 0;
        }

        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            // Already running: toast clicks are routed to that instance by Windows.
            // A normal launch just asks it to show its status.
            if (!ToastNotificationManagerCompat.WasCurrentProcessToastActivated())
            {
                try { using var signal = EventWaitHandle.OpenExisting(ShowSignalName); signal.Set(); }
                catch { /* the other instance is exiting */ }
            }
            return 0;
        }

        ApplicationConfiguration.Initialize();
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());

        Application.ThreadException += (_, e) => Log.Write($"UI error: {e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write($"Fatal: {e.ExceptionObject}");

        var background = args.Any(a => a.Equals("--background", StringComparison.OrdinalIgnoreCase))
                         || ToastNotificationManagerCompat.WasCurrentProcessToastActivated();

        using var app = new TrayApp(background);

        using var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName);
        var wait = ThreadPool.RegisterWaitForSingleObject(showSignal,
            (_, _) => app.Post(app.ShowStatus), null, Timeout.Infinite, executeOnlyOnce: false);

        ToastNotificationManagerCompat.OnActivated += e =>
        {
            var toastArgs = ToastArguments.Parse(e.Argument);
            app.Post(() => app.HandleToast(toastArgs));
        };

        Log.Write($"Started {AppName} {Application.ProductVersion}");
        Application.Run(app);

        wait.Unregister(null);
        Log.Write("Exited");
        return 0;
    }

    private static void Uninstall()
    {
        try { StartupManager.SetEnabled(false); } catch { }
        try { ToastNotificationManagerCompat.Uninstall(); } catch { }
        try { Directory.Delete(AppConfig.DataDirectory, recursive: true); } catch { }
        MessageBox.Show($"{AppName} settings, startup entry and notifications were removed.\n\nYou can now delete the program folder.",
            AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}
