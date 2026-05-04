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

    private async Task<T> InvokeAsync<T>(PipeRequest request, Func<PipeResponse, T> select, CancellationToken ct)
    {
        await using var client = new NamedPipeClientStream(
            ".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
        await client.ConnectAsync(5000, ct).ConfigureAwait(false);

        await client.WriteAsync(PipeProtocol.Encode(request), ct).ConfigureAwait(false);
        await client.FlushAsync(ct).ConfigureAwait(false);

        var responseBytes = await ReadLineAsync(client, ct).ConfigureAwait(false)
            ?? throw new IOException("no response from service");

        var response = PipeProtocol.Decode<PipeResponse>(responseBytes)
            ?? throw new IOException("invalid response from service");

        if (!response.Ok)
            throw new InvalidOperationException(response.Error ?? "service returned error");

        return select(response);
    }

    private static async Task<byte[]?> ReadLineAsync(NamedPipeClientStream client, CancellationToken ct)
    {
        var buf = new byte[1024];
        using var ms = new MemoryStream(1024);
        while (true)
        {
            var read = await client.ReadAsync(buf.AsMemory(), ct).ConfigureAwait(false);
            if (read <= 0) return ms.Length == 0 ? null : ms.ToArray();
            for (var i = 0; i < read; i++)
            {
                if (buf[i] == (byte)'\n')
                {
                    ms.Write(buf, 0, i);
                    return ms.ToArray();
                }
            }
            ms.Write(buf, 0, read);
            if (ms.Length > 256 * 1024) return ms.ToArray();
        }
    }
}
