using System.IO.Pipes;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using BlockRdpBruteForce.Configuration;
using Microsoft.Extensions.Options;

namespace BlockRdpBruteForce.Ipc;

[SupportedOSPlatform("windows")]
public sealed class PipeServer : BackgroundService
{
    private readonly AppOptions _options;
    private readonly IPipeOps _ops;
    private readonly ILogger<PipeServer> _log;

    public PipeServer(IOptions<AppOptions> options, IPipeOps ops, ILogger<PipeServer> log)
    {
        _options = options.Value;
        _ops = ops;
        _log = log;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        RunAsync(_ops, stoppingToken);

    public async Task RunAsync(IPipeOps ops, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ops);
        var security = BuildSecurity();
        _log.LogInformation("Pipe server listening on \\\\.\\pipe\\{Pipe}", _options.PipeName);

        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = CreatePipe(security);

                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                var connection = server;
                server = null;
                _ = Task.Run(() => HandleClientAsync(connection, ops, ct), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                server?.Dispose();
                return;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Pipe accept loop error");
                server?.Dispose();
                try { await Task.Delay(1000, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private bool _aclApplied = true;

    private NamedPipeServerStream CreatePipe(PipeSecurity security)
    {
        if (_aclApplied)
        {
            try
            {
                return NamedPipeServerStreamAcl.Create(
                    _options.PipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    inBufferSize: 4096,
                    outBufferSize: 4096,
                    pipeSecurity: security);
            }
            catch (UnauthorizedAccessException)
            {
                _log.LogWarning(
                    "Could not apply pipe ACL (process is not running as LocalSystem/Administrator). " +
                    "Falling back to default ACL — only this user/SYSTEM can connect. " +
                    "This is expected for `dotnet run`; the installed service will use the full ACL.");
                _aclApplied = false;
            }
        }

        return new NamedPipeServerStream(
            _options.PipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 4096,
            outBufferSize: 4096);
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, IPipeOps ops, CancellationToken ct)
    {
        try
        {
            var requestBytes = await ReadLineAsync(pipe, ct).ConfigureAwait(false);
            if (requestBytes is null)
            {
                await WriteAsync(pipe, PipeResponse.Failure("empty request"), ct).ConfigureAwait(false);
                return;
            }

            PipeRequest? request;
            try { request = PipeProtocol.Decode<PipeRequest>(requestBytes); }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Malformed pipe request");
                await WriteAsync(pipe, PipeResponse.Failure("malformed JSON"), ct).ConfigureAwait(false);
                return;
            }

            if (request is null || string.IsNullOrEmpty(request.Op))
            {
                await WriteAsync(pipe, PipeResponse.Failure("missing op"), ct).ConfigureAwait(false);
                return;
            }

            var response = await DispatchAsync(pipe, request, ops, ct).ConfigureAwait(false);
            await WriteAsync(pipe, response, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (Exception ex)
        {
            _log.LogError(ex, "Pipe client handler error");
        }
        finally
        {
            try { pipe.Disconnect(); } catch { }
            pipe.Dispose();
        }
    }

    private async Task<PipeResponse> DispatchAsync(
        NamedPipeServerStream pipe, PipeRequest request, IPipeOps ops, CancellationToken ct)
    {
        switch (request.Op)
        {
            case PipeOps.Status:
                return new PipeResponse { Ok = true, Status = ops.GetStatus() };

            case PipeOps.List:
                return new PipeResponse { Ok = true, Items = ops.GetList().ToList() };

            case PipeOps.Unblock:
            {
                if (string.IsNullOrWhiteSpace(request.Ip))
                    return PipeResponse.Failure("ip required");
                if (!IPAddress.TryParse(request.Ip.Trim(), out var ip))
                    return PipeResponse.Failure($"invalid ip: {request.Ip}");
                if (!RequireAdmin(pipe)) return PipeResponse.Failure("administrator required");
                var payload = await ops.UnblockAsync(ip, ct).ConfigureAwait(false);
                return new PipeResponse { Ok = true, Unblock = payload };
            }

            case PipeOps.Pause:
            {
                if (!RequireAdmin(pipe)) return PipeResponse.Failure("administrator required");
                var minutes = request.PauseMinutes ?? 60;
                if (minutes <= 0) return PipeResponse.Failure("pauseMinutes must be > 0");
                var payload = ops.Pause(TimeSpan.FromMinutes(minutes));
                return new PipeResponse { Ok = true, Pause = payload };
            }

            case PipeOps.Resume:
            {
                if (!RequireAdmin(pipe)) return PipeResponse.Failure("administrator required");
                var payload = ops.Resume();
                return new PipeResponse { Ok = true, Pause = payload };
            }

            case PipeOps.ConfigGet:
            {
                if (!RequireAdmin(pipe)) return PipeResponse.Failure("administrator required");
                return new PipeResponse { Ok = true, ConfigEffective = ops.GetConfig() };
            }

            case PipeOps.ConfigSet:
            {
                if (!RequireAdmin(pipe)) return PipeResponse.Failure("administrator required");
                if (request.Config is null) return PipeResponse.Failure("config payload required");
                try
                {
                    var caller = SafeGetClientName(pipe);
                    var result = ops.SetConfig(request.Config, caller);
                    return new PipeResponse { Ok = true, ConfigSet = result };
                }
                catch (ConfigValidationException ex)
                {
                    return PipeResponse.Failure(ex.Message);
                }
            }

            case PipeOps.WhitelistAdd:
            case PipeOps.WhitelistRemove:
            {
                if (!RequireAdmin(pipe)) return PipeResponse.Failure("administrator required");
                if (string.IsNullOrWhiteSpace(request.Cidr))
                    return PipeResponse.Failure("cidr required");

                var entry = request.Cidr.Trim();
                var current = ops.GetConfig();
                var list = current.Whitelist?.ToList() ?? new List<string>();

                if (request.Op == PipeOps.WhitelistAdd)
                {
                    if (!list.Any(e => string.Equals(e, entry, StringComparison.OrdinalIgnoreCase)))
                        list.Add(entry);
                }
                else
                {
                    list.RemoveAll(e => string.Equals(e, entry, StringComparison.OrdinalIgnoreCase));
                }

                try
                {
                    var caller = SafeGetClientName(pipe);
                    var result = ops.SetConfig(new ConfigPayload { Whitelist = list }, caller);
                    return new PipeResponse { Ok = true, ConfigSet = result };
                }
                catch (ConfigValidationException ex)
                {
                    return PipeResponse.Failure(ex.Message);
                }
            }

            case PipeOps.GeoStatus:
                return new PipeResponse { Ok = true, GeoStatus = ops.GetGeoStatus() };

            case PipeOps.GeoRefresh:
            {
                if (!RequireAdmin(pipe)) return PipeResponse.Failure("administrator required");
                try
                {
                    var status = await ops.RefreshGeoAsync(ct).ConfigureAwait(false);
                    return new PipeResponse { Ok = true, GeoStatus = status };
                }
                catch (InvalidOperationException ex)
                {
                    return PipeResponse.Failure(ex.Message);
                }
            }

            default:
                return PipeResponse.Failure($"unknown op: {request.Op}");
        }
    }

    private bool RequireAdmin(NamedPipeServerStream pipe)
    {
        try
        {
            var isAdmin = false;
            pipe.RunAsClient(() =>
            {
                // CheckTokenMembership with a null handle uses the thread's current
                // impersonation token (set by RunAsClient). It checks that the
                // Administrators SID is both present AND enabled, so non-elevated admin
                // processes (UAC filtered token) correctly return false.
                // We avoid WindowsPrincipal/ClaimsPrincipal entirely because constructing
                // WindowsPrincipal loads System.Security.Claims, which fails inside
                // RunAsClient on Windows Server 2019 with a single-file publish.
                isAdmin = NativeMethods.IsAdminToken(IntPtr.Zero);
            });
            if (!isAdmin)
            {
                var caller = SafeGetClientName(pipe);
                _log.LogWarning("Rejected pipe mutation from non-admin caller: {Caller}", caller);
            }
            return isAdmin;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Admin check failed; rejecting request");
            return false;
        }
    }

    private static class NativeMethods
    {
        private static readonly byte[] AdminSidBytes = GetAdminSidBytes();

        private static byte[] GetAdminSidBytes()
        {
            var sid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var bytes = new byte[sid.BinaryLength];
            sid.GetBinaryForm(bytes, 0);
            return bytes;
        }

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool CheckTokenMembership(
            IntPtr tokenHandle, byte[] sidToCheck, out bool isMember);

        internal static bool IsAdminToken(IntPtr token)
        {
            return CheckTokenMembership(token, AdminSidBytes, out var isMember) && isMember;
        }
    }

    private static string SafeGetClientName(NamedPipeServerStream pipe)
    {
        try { return pipe.GetImpersonationUserName(); }
        catch { return "<unknown>"; }
    }

    private static async Task<byte[]?> ReadLineAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        var buf = new byte[1024];
        using var ms = new MemoryStream(1024);
        while (true)
        {
            var read = await pipe.ReadAsync(buf.AsMemory(), ct).ConfigureAwait(false);
            if (read == 0)
                return ms.Length == 0 ? null : ms.ToArray();

            for (var i = 0; i < read; i++)
            {
                if (buf[i] == (byte)'\n')
                {
                    ms.Write(buf, 0, i);
                    return ms.ToArray();
                }
            }
            ms.Write(buf, 0, read);
            if (ms.Length > PipeProtocol.MaxRequestBytes)
                throw new IOException("request exceeds maximum size");
        }
    }

    private static async Task WriteAsync(NamedPipeServerStream pipe, PipeResponse response, CancellationToken ct)
    {
        var bytes = PipeProtocol.Encode(response);
        await pipe.WriteAsync(bytes, ct).ConfigureAwait(false);
        await pipe.FlushAsync(ct).ConfigureAwait(false);
        try { pipe.WaitForPipeDrain(); } catch { }
    }

    private static PipeSecurity BuildSecurity()
    {
        var security = new PipeSecurity();

        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        // AuthenticatedUsers (S-1-5-11) is broader than INTERACTIVE (S-1-5-4); it matches
        // both interactive console sessions and remote/SSH sessions where the tray app
        // or CLI may be invoked. The admin gate inside PipeServer.RequireAdmin is the
        // real security boundary for mutating ops; the ACL here only filters who can
        // open the pipe at all (status/list are read-only and safe for any auth user).
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize,
            AccessControlType.Allow));

        return security;
    }
}
