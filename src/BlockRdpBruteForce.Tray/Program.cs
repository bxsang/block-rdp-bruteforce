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

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayContext());
        return 0;
    }
}
