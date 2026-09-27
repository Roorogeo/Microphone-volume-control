using MicBoost.Audio;
using MicBoost.Core;

namespace MicBoost;

internal static class Program
{
    private const string MutexName = @"Local\MicBoost-SingleInstance-5E3A0C1D";
    private const string ShowEventName = @"Local\MicBoost-ShowPopup-5E3A0C1D";

    [STAThread]
    private static int Main(string[] args)
    {
        // Elevated helper mode: one-time Equalizer APO setup, then exit. Runs without the
        // single-instance mutex because the normal instance is waiting for it.
        if (args.Length == 2 && args[0] == EqualizerApo.SetupArgument)
            return EqualizerApo.PerformSetup(args[1]);

        // Only one instance: a second launch just asks the running one to open its popup.
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            try
            {
                using var show = EventWaitHandle.OpenExisting(ShowEventName);
                show.Set();
            }
            catch
            {
                // The first instance is still starting up; nothing else to do.
            }
            return 0;
        }

        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Error($"Unhandled UI exception: {e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error($"Fatal exception: {e.ExceptionObject}");

        Log.Info($"MicBoost {Application.ProductVersion} starting ({Environment.OSVersion}).");
        using var context = new TrayApplicationContext(showEvent);
        Application.Run(context);
        GC.KeepAlive(mutex);
        return 0;
    }
}
