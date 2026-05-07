using System.Runtime.Versioning;
using System.Security.Principal;

namespace BlockRdpBruteForce.Updater;

[SupportedOSPlatform("windows")]
internal static class Program
{
    [STAThread]
    private static int Main(string[] argv)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("BlockRdpBruteForce.Updater requires Windows.");
            return 1;
        }

        var parse = UpdaterArgs.Parse(argv);
        if (!parse.Ok || parse.Args is null)
        {
            ShowFatal($"Invalid arguments: {parse.Error}");
            return 2;
        }
        var args = parse.Args;

        if (!IsRunningAsAdmin())
        {
            ShowFatal(
                "BlockRdpBruteForce.Updater must be launched with administrator privileges. " +
                "It is normally started by the BlockRdpBruteForce service via the user's " +
                "elevated token; running it directly is not supported.");
            return 3;
        }

        // Single-instance lock: a second updater would race on the MSI path / marker file.
        using var mutex = new Mutex(initiallyOwned: false, name: @"Global\BlockRdpBruteForce.Updater");
        bool acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired)
            {
                ShowFatal("Another BlockRdpBruteForce update is already running.");
                return 4;
            }

            ApplicationConfiguration.Initialize();

            var updatesDir = Path.GetDirectoryName(args.MsiPath)!;
            var stageWriter = new StageWriter(updatesDir, args.Version, args.MsiPath, DateTime.UtcNow);

            using var form = new MainForm(args, stageWriter);
            Application.Run(form);
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

    private static bool IsRunningAsAdmin()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static void ShowFatal(string message)
    {
        try
        {
            MessageBox.Show(message, "BlockRdpBruteForce Updater",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch
        {
            Console.Error.WriteLine(message);
        }
    }
}
