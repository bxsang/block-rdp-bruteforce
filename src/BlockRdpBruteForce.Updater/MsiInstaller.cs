using System.Diagnostics;
using System.Runtime.Versioning;

namespace BlockRdpBruteForce.Updater;

[SupportedOSPlatform("windows")]
internal static class MsiInstaller
{
    public static async Task<InstallResult> RunAsync(
        string msiPath,
        string logPath,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(msiPath);
        ArgumentException.ThrowIfNullOrEmpty(logPath);

        var systemDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var msiexec = Path.Combine(systemDir, "msiexec.exe");
        var args = $"/i \"{msiPath}\" /quiet /norestart /L*v \"{logPath}\"";

        var psi = new ProcessStartInfo
        {
            FileName = msiexec,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        Process? proc;
        try
        {
            proc = Process.Start(psi);
        }
        catch (Exception ex)
        {
            return InstallResult.Failed(-1, $"Failed to launch msiexec: {ex.Message}");
        }
        if (proc is null)
            return InstallResult.Failed(-1, "Process.Start returned null");

        try
        {
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // We don't kill msiexec — /quiet has no UI to cancel; let it finish.
            // The updater UI just stops waiting.
            throw;
        }

        return Map(proc.ExitCode);
    }

    internal static InstallResult Map(int exitCode) => exitCode switch
    {
        0     => InstallResult.Success(0, rebootRequired: false),
        3010  => InstallResult.Success(3010, rebootRequired: true),
        1602  => InstallResult.Cancelled(1602),
        1603  => InstallResult.Failed(1603, "Fatal error during installation (1603). See log for details."),
        1618  => InstallResult.Failed(1618, "Another installation is in progress (1618). Try again shortly."),
        1619  => InstallResult.Failed(1619, "Installation package could not be opened (1619)."),
        1625  => InstallResult.Failed(1625, "Installation forbidden by system policy (1625)."),
        _     => InstallResult.Failed(exitCode, $"msiexec returned exit code {exitCode}."),
    };
}

internal sealed class InstallResult
{
    public bool Ok { get; init; }
    public bool WasCancelled { get; init; }
    public bool RebootRequired { get; init; }
    public int ExitCode { get; init; }
    public string? Error { get; init; }

    public static InstallResult Success(int exitCode, bool rebootRequired) =>
        new() { Ok = true, ExitCode = exitCode, RebootRequired = rebootRequired };

    public static InstallResult Cancelled(int exitCode) =>
        new() { Ok = false, WasCancelled = true, ExitCode = exitCode, Error = "Installation was cancelled." };

    public static InstallResult Failed(int exitCode, string error) =>
        new() { Ok = false, ExitCode = exitCode, Error = error };
}
