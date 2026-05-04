using System.Runtime.Versioning;
using System.Threading.Channels;

namespace BlockRdpBruteForce.Eventing;

[SupportedOSPlatform("windows")]
public sealed class RdpCoreTsSubscriber : EventLogSubscriberBase
{
    private const string LogChannelName =
        "Microsoft-Windows-RemoteDesktopServices-RdpCoreTS/Operational";
    private const string XPath = "*[System/EventID=140]";

    public RdpCoreTsSubscriber(
        BookmarkStore bookmarks,
        ChannelWriter<FailedLogon> writer,
        ILogger<RdpCoreTsSubscriber> log)
        : base(LogChannelName, XPath, bookmarks, writer, log)
    {
    }

    protected override FailedLogon? ParseRecord(string xml) => EventXmlParser.TryParse(xml);
}
