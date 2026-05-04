using System.Globalization;
using System.Net;
using System.Xml.Linq;

namespace BlockRdpBruteForce.Eventing;

public static class EventXmlParser
{
    public static FailedLogon? TryParse(string xml, bool acceptNlaNtlm = false)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;

        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch
        {
            return null;
        }

        var root = doc.Root;
        if (root is null) return null;

        var system = ChildByLocalName(root, "System");
        if (system is null) return null;

        var eventIdEl = ChildByLocalName(system, "EventID");
        if (eventIdEl is null || !int.TryParse(eventIdEl.Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var eventId))
            return null;

        var time = ParseTime(system);

        return eventId switch
        {
            4625 => Parse4625(root, time, acceptNlaNtlm),
            140 => Parse140(root, time),
            _ => null,
        };
    }

    private static FailedLogon? Parse4625(XElement root, DateTime utcTime, bool acceptNlaNtlm)
    {
        var data = ChildByLocalName(root, "EventData");
        if (data is null) return null;

        var logonType = GetDataValue(data, "LogonType");
        var accepted = logonType == "10"
            || (acceptNlaNtlm
                && logonType == "3"
                && string.Equals(GetDataValue(data, "LogonProcessName")?.Trim(), "NtLmSsp", StringComparison.Ordinal));
        if (!accepted) return null;

        var ip = ParseIp(GetDataValue(data, "IpAddress"));
        if (ip is null) return null;

        var user = GetDataValue(data, "TargetUserName") ?? string.Empty;
        return new FailedLogon(ip, user, utcTime, "Security/4625");
    }

    private static FailedLogon? Parse140(XElement root, DateTime utcTime)
    {
        var data = ChildByLocalName(root, "EventData");
        var ipRaw = data is not null ? GetDataValue(data, "IPString") : null;
        ipRaw ??= root.Descendants().FirstOrDefault(e => e.Name.LocalName == "IPString")?.Value;

        var ip = ParseIp(ipRaw);
        if (ip is null) return null;

        return new FailedLogon(ip, string.Empty, utcTime, "RdpCoreTS/140");
    }

    private static XElement? ChildByLocalName(XElement parent, string localName) =>
        parent.Elements().FirstOrDefault(e => e.Name.LocalName == localName);

    private static string? GetDataValue(XElement eventData, string name)
    {
        var el = eventData.Elements().FirstOrDefault(e =>
            e.Name.LocalName == "Data" && (string?)e.Attribute("Name") == name);
        return el?.Value;
    }

    private static DateTime ParseTime(XElement system)
    {
        var systemTime = ChildByLocalName(system, "TimeCreated")?.Attribute("SystemTime")?.Value;
        if (systemTime is not null &&
            DateTime.TryParse(systemTime, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
        }
        return DateTime.UtcNow;
    }

    private static IPAddress? ParseIp(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();
        if (s == "-") return null;
        if (!IPAddress.TryParse(s, out var ip)) return null;
        if (IPAddress.IsLoopback(ip)) return null;
        if (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return null;
        return ip;
    }
}
