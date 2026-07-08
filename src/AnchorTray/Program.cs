using SolixBle;

namespace Anchor;

internal static class Program
{
    /// <summary>Single-instance mutex name; the installer keys on this exact string.</summary>
    private const string MutexName = "Anchor.SingleInstance";

    [STAThread]
    private static void Main(string[] args)
    {
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
            return;

        var log = new FileLog();
        log.Info("Anchor starting.");
        // DEBUG-level BLE messages (per-packet hex dumps) would churn the rotated
        // log within hours and destroy the shutdown-decision audit trail, so they
        // are dropped unless --verbose (or -v) was passed.
        var verbose = args.Any(arg => arg is "--verbose" or "-v");
        SolixLog.Sink = (level, message) =>
        {
            if (verbose || level != "DEBUG")
                log.Log(level, "[SolixBle] " + message);
        };
        if (verbose)
            log.Info("Verbose logging enabled.");

        // First run (fresh install, no saved config yet): register autostart so
        // Anchor comes back after a reboot. The installer never writes this — the
        // app owns the per-user Run key — so an admin install touches no user area.
        var firstRun = !AppConfig.Exists;
        var config = AppConfig.Load(log);
        if (firstRun && !StartupRegistration.IsEnabled())
        {
            StartupRegistration.SetEnabled(true, log);
            log.Info("First run: registered Anchor to start with Windows (toggle in Settings).");
        }

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => log.Error($"Unhandled UI exception: {e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => log.Error($"Unhandled exception: {e.ExceptionObject}");
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());

        using var context = new TrayAppContext(config, log);
        Application.Run(context);
    }
}
