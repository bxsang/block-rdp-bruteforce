using System.IO.Pipes;
using System.Net;
using System.Runtime.Versioning;
using System.Security.Principal;
using BlockRdpBruteForce.Ipc;

namespace BlockRdpBruteForce.Tray;

[SupportedOSPlatform("windows")]
public sealed class PipeClient
{
    private readonly string _pipeName;

    public PipeClient(string pipeName)
    {
        _pipeName = string.IsNullOrWhiteSpace(pipeName) ? "BlockRdpBruteForce" : pipeName;
    }

    public Task<StatusPayload> StatusAsync(CancellationToken ct = default) =>
        InvokeAsync(new PipeRequest { Op = PipeOps.Status }, r => r.Status!, ct);

    public Task<IReadOnlyList<IpEntry>> ListAsync(CancellationToken ct = default) =>
        InvokeAsync(new PipeRequest { Op = PipeOps.List }, r => (IReadOnlyList<IpEntry>)(r.Items ?? new()), ct);

    public Task<UnblockPayload> UnblockAsync(IPAddress ip, CancellationToken ct = default) =>
        InvokeAsync(
            new PipeRequest { Op = PipeOps.Unblock, Ip = ip.ToString() },
            r => r.Unblock!, ct);

    public Task<PausePayload> PauseAsync(int minutes, CancellationToken ct = default) =>
        InvokeAsync(
            new PipeRequest { Op = PipeOps.Pause, PauseMinutes = minutes },
            r => r.Pause!, ct);

    public Task<PausePayload> ResumeAsync(CancellationToken ct = default) =>
        InvokeAsync(
            new PipeRequest { Op = PipeOps.Resume },
            r => r.Pause!, ct);

    public Task<ConfigPayload> ConfigGetAsync(CancellationToken ct = default) =>
        InvokeAsync(
            new PipeRequest { Op = PipeOps.ConfigGet },
            r => r.ConfigEffective!, ct);

    public Task<ConfigSetResult> ConfigSetAsync(ConfigPayload payload, CancellationToken ct = default) =>
        InvokeAsync(
            new PipeRequest { Op = PipeOps.ConfigSet, Config = payload },
            r => r.ConfigSet!, ct);

    public Task<ConfigSetResult> WhitelistAddAsync(string cidr, CancellationToken ct = default) =>
        InvokeAsync(
            new PipeRequest { Op = PipeOps.WhitelistAdd, Cidr = cidr },
            r => r.ConfigSet!, ct);

    public Task<ConfigSetResult> WhitelistRemoveAsync(string cidr, CancellationToken ct = default) =>
        InvokeAsync(
            new PipeRequest { Op = PipeOps.WhitelistRemove, Cidr = cidr },
            r => r.ConfigSet!, ct);

    public Task<GeoStatusPayload> GeoStatusAsync(CancellationToken ct = default) =>
        InvokeAsync(
            new PipeRequest { Op = PipeOps.GeoStatus },
            r => r.GeoStatus!, ct);

    public Task<GeoStatusPayload> GeoRefreshAsync(CancellationToken ct = default) =>
        InvokeAsync(
            new PipeRequest { Op = PipeOps.GeoRefresh },
            r => r.GeoStatus!, ct);

    public Task<UpdateStatusPayload> UpdateStatusAsync(CancellationToken ct = default) =>
        InvokeAsync(
            new PipeRequest { Op = PipeOps.UpdateStatus },
            r => r.UpdateStatus!, ct);

    public Task<UpdateStatusPayload> UpdateCheckNowAsync(CancellationToken ct = default) =>
        InvokeAsync(
            new PipeRequest { Op = PipeOps.UpdateCheckNow },
            r => r.UpdateStatus!, ct);

    public Task<UpdateApplyPayload> UpdateApplyAsync(string version, CancellationToken ct = default) =>
        InvokeAsync(
            new PipeRequest { Op = PipeOps.UpdateApply, Version = version },
            r => r.UpdateApply!, ct);

    private async Task<T> InvokeAsync<T>(PipeRequest request, Func<PipeResponse, T> select, CancellationToken ct)
    {
        await using var client = new NamedPipeClientStream(
            ".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
        await client.ConnectAsync(5000, ct).ConfigureAwait(false);

        await client.WriteAsync(PipeProtocol.Encode(request), ct).ConfigureAwait(false);
        await client.FlushAsync(ct).ConfigureAwait(false);

        var responseBytes = await PipeProtocol.ReadLineAsync(client, ct).ConfigureAwait(false)
            ?? throw new IOException("no response from service");

        var response = PipeProtocol.Decode<PipeResponse>(responseBytes)
            ?? throw new IOException("invalid response from service");

        if (!response.Ok)
        {
            var message = response.Error ?? "service returned error";
            throw response.ErrorCode switch
            {
                ErrorCodes.Forbidden => new PipeForbiddenException(message),
                ErrorCodes.Validation => new PipeValidationException(message),
                _ => new InvalidOperationException(message),
            };
        }

        return select(response);
    }

}

[SupportedOSPlatform("windows")]
public sealed class PipeForbiddenException : InvalidOperationException
{
    public PipeForbiddenException(string message) : base(message) { }
}

[SupportedOSPlatform("windows")]
public sealed class PipeValidationException : InvalidOperationException
{
    public PipeValidationException(string message) : base(message) { }
}
