using System.Runtime.Versioning;
using System.Threading.Channels;

namespace BlockRdpBruteForce.Eventing;

[SupportedOSPlatform("windows")]
public sealed class SecurityEventSubscriber : EventLogSubscriberBase
{
    private const string LogChannelName = "Security";
    private const string InteractiveOnlyXPath =
        "*[System/EventID=4625] and *[EventData/Data[@Name='LogonType']='10']";
    // Windows Event Log XPath is a narrow XPath 1.0 subset; an OR combining two
    // top-level *[...] predicates with parenthesized grouping silently matches
    // zero events on Server 2019. When the NLA-NTLM relaxation is on, broaden
    // the OS-side filter to all 4625 events and let EventXmlParser apply the
    // LogonType + LogonProcessName check in C# (where it can also Trim()
    // the padded LogonProcessName field).
    private const string AllFailedLogonsXPath = "*[System/EventID=4625]";

    private readonly bool _acceptNlaNtlm;

    public SecurityEventSubscriber(
        BookmarkStore bookmarks,
        ChannelWriter<FailedLogon> writer,
        ILogger<SecurityEventSubscriber> log,
        bool acceptNlaNtlm)
        : base(LogChannelName, acceptNlaNtlm ? AllFailedLogonsXPath : InteractiveOnlyXPath, bookmarks, writer, log)
    {
        _acceptNlaNtlm = acceptNlaNtlm;
    }

    protected override FailedLogon? ParseRecord(string xml) =>
        EventXmlParser.TryParse(xml, _acceptNlaNtlm);
}
