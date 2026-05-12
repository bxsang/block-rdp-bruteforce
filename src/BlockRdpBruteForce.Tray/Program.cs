using System.Runtime.Versioning;

namespace BlockRdpBruteForce.Tray;

[SupportedOSPlatform("windows")]
static class Program
{
    [STAThread]
    static int Main()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("BlockRdpBruteForce.Tray requires Windows.");
            return 1;
        }

        // Per-session single-instance guard. Both the updater (post-msiexec) and
        // the service's UpdateApplyCompletionService relaunch the tray after an
        // upgrade — without this, the user can end up with two tray icons.
        // The "Local\" prefix scopes the mutex to the user's session so different
        // logged-on users still each get their own tray.
        using var mutex = new Mutex(initiallyOwned: false, name: @"Local\BlockRdpBruteForce.Tray");
        bool acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) return 0;

            ApplicationConfiguration.Initialize();
            Application.Run(new TrayContext());
            return 0;
        }
        finally
        {
            if (acquired)
            {
                try { mutex.ReleaseMutex(); } catch { }
            }
        }
    }
}
