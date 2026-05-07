using System.Net;

namespace BlockRdpBruteForce.Ipc;

public interface IPipeOps
{
    StatusPayload GetStatus();
    IReadOnlyList<IpEntry> GetList();
    Task<UnblockPayload> UnblockAsync(IPAddress ip, CancellationToken ct);
    PausePayload Pause(TimeSpan duration);
    PausePayload Resume();
    ConfigPayload GetConfig();
    ConfigSetResult SetConfig(ConfigPayload payload, string callerName);
    GeoStatusPayload GetGeoStatus();
    Task<GeoStatusPayload> RefreshGeoAsync(CancellationToken ct);
    UpdateStatusPayload GetUpdateStatus();
    Task<UpdateStatusPayload> CheckForUpdateNowAsync(CancellationToken ct);
    Task<UpdateApplyPayload> ApplyUpdateAsync(string requestedVersion, CancellationToken ct);
}
