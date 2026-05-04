using System.Net;

namespace BlockRdpBruteForce.Ipc;

public interface IPipeOps
{
    StatusPayload GetStatus();
    IReadOnlyList<IpEntry> GetList();
    Task<UnblockPayload> UnblockAsync(IPAddress ip, CancellationToken ct);
    PausePayload Pause(TimeSpan duration);
    PausePayload Resume();
}
