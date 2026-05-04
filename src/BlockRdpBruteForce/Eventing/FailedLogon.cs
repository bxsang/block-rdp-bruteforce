using System.Net;

namespace BlockRdpBruteForce.Eventing;

public sealed record FailedLogon(IPAddress Ip, string User, DateTime UtcTime, string Source);
