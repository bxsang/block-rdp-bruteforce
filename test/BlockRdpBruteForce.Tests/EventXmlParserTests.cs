using System.Net;
using BlockRdpBruteForce.Eventing;

namespace BlockRdpBruteForce.Tests;

public class EventXmlParserTests
{
    private const string Ns = "http://schemas.microsoft.com/win/2004/08/events/event";

    private static string Security4625(string ipAddress, string logonType = "10", string user = "administrator", string time = "2025-01-01T12:00:00.000Z", string logonProcessName = "User32 ", string authPackageName = "Negotiate") =>
$$"""
<Event xmlns="{{Ns}}">
  <System>
    <Provider Name="Microsoft-Windows-Security-Auditing" Guid="{54849625-5478-4994-A5BA-3E3B0328C30D}" />
    <EventID>4625</EventID>
    <Version>0</Version>
    <Level>0</Level>
    <Task>12544</Task>
    <Opcode>0</Opcode>
    <Keywords>0x8010000000000000</Keywords>
    <TimeCreated SystemTime="{{time}}" />
    <EventRecordID>1</EventRecordID>
    <Channel>Security</Channel>
    <Computer>HOST</Computer>
  </System>
  <EventData>
    <Data Name="SubjectUserSid">S-1-5-18</Data>
    <Data Name="TargetUserName">{{user}}</Data>
    <Data Name="TargetDomainName">DOMAIN</Data>
    <Data Name="Status">0xc000006d</Data>
    <Data Name="LogonType">{{logonType}}</Data>
    <Data Name="LogonProcessName">{{logonProcessName}}</Data>
    <Data Name="AuthenticationPackageName">{{authPackageName}}</Data>
    <Data Name="WorkstationName">ATTACKER</Data>
    <Data Name="IpAddress">{{ipAddress}}</Data>
    <Data Name="IpPort">3389</Data>
  </EventData>
</Event>
""";

    private static string RdpCoreTs140(string ipAddress, string time = "2025-01-01T12:00:00.000Z") =>
$$"""
<Event xmlns="{{Ns}}">
  <System>
    <Provider Name="Microsoft-Windows-RemoteDesktopServices-RdpCoreTS" Guid="{1139c61b-b549-4251-8ed3-27250a1edec8}" />
    <EventID>140</EventID>
    <Version>0</Version>
    <Level>3</Level>
    <Task>4</Task>
    <Opcode>14</Opcode>
    <Keywords>0x4000000000000000</Keywords>
    <TimeCreated SystemTime="{{time}}" />
    <EventRecordID>2</EventRecordID>
    <Channel>Microsoft-Windows-RemoteDesktopServices-RdpCoreTS/Operational</Channel>
    <Computer>HOST</Computer>
  </System>
  <EventData>
    <Data Name="IPString">{{ipAddress}}</Data>
  </EventData>
</Event>
""";

    [Fact]
    public void Parses_4625_IPv4()
    {
        var result = EventXmlParser.TryParse(Security4625("203.0.113.7"));
        Assert.NotNull(result);
        Assert.Equal(IPAddress.Parse("203.0.113.7"), result!.Ip);
        Assert.Equal("administrator", result.User);
        Assert.Equal(DateTimeKind.Utc, result.UtcTime.Kind);
        Assert.Equal(new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc), result.UtcTime);
        Assert.Equal("Security/4625", result.Source);
    }

    [Fact]
    public void Parses_4625_IPv6()
    {
        var result = EventXmlParser.TryParse(Security4625("2001:db8::1"));
        Assert.NotNull(result);
        Assert.Equal(IPAddress.Parse("2001:db8::1"), result!.Ip);
    }

    [Fact]
    public void Rejects_4625_DashIp()
    {
        var result = EventXmlParser.TryParse(Security4625("-"));
        Assert.Null(result);
    }

    [Fact]
    public void Rejects_4625_UnspecifiedIPv6()
    {
        var result = EventXmlParser.TryParse(Security4625("::"));
        Assert.Null(result);
    }

    [Fact]
    public void Rejects_4625_AnyIPv4()
    {
        var result = EventXmlParser.TryParse(Security4625("0.0.0.0"));
        Assert.Null(result);
    }

    [Fact]
    public void Rejects_4625_LoopbackV4()
    {
        var result = EventXmlParser.TryParse(Security4625("127.0.0.1"));
        Assert.Null(result);
    }

    [Fact]
    public void Rejects_4625_LoopbackV6()
    {
        var result = EventXmlParser.TryParse(Security4625("::1"));
        Assert.Null(result);
    }

    [Fact]
    public void Rejects_4625_NonInteractiveLogonType()
    {
        var result = EventXmlParser.TryParse(Security4625("203.0.113.7", logonType: "3"));
        Assert.Null(result);
    }

    [Fact]
    public void Rejects_4625_NlaNtlm_When_Flag_Off()
    {
        var xml = Security4625("160.19.178.8", logonType: "3", logonProcessName: "NtLmSsp", authPackageName: "NTLM");
        var result = EventXmlParser.TryParse(xml, acceptNlaNtlm: false);
        Assert.Null(result);
    }

    [Fact]
    public void Accepts_4625_NlaNtlm_When_Flag_On()
    {
        var xml = Security4625("160.19.178.8", logonType: "3", user: "administrator", logonProcessName: "NtLmSsp", authPackageName: "NTLM");
        var result = EventXmlParser.TryParse(xml, acceptNlaNtlm: true);
        Assert.NotNull(result);
        Assert.Equal(IPAddress.Parse("160.19.178.8"), result!.Ip);
        Assert.Equal("administrator", result.User);
        Assert.Equal("Security/4625", result.Source);
    }

    [Fact]
    public void Rejects_4625_LogonType3_NonNtlmProcess_Even_When_Flag_On()
    {
        var xml = Security4625("203.0.113.7", logonType: "3", logonProcessName: "Advapi  ", authPackageName: "Negotiate");
        var result = EventXmlParser.TryParse(xml, acceptNlaNtlm: true);
        Assert.Null(result);
    }

    [Fact]
    public void Accepts_4625_NlaNtlm_With_Padded_LogonProcessName()
    {
        // Real Security/4625 events on Server 2019 emit "NtLmSsp " (padded).
        var xml = Security4625("160.19.178.8", logonType: "3", logonProcessName: "NtLmSsp ", authPackageName: "NTLM");
        var result = EventXmlParser.TryParse(xml, acceptNlaNtlm: true);
        Assert.NotNull(result);
        Assert.Equal(IPAddress.Parse("160.19.178.8"), result!.Ip);
    }

    [Fact]
    public void Parses_RdpCoreTs_140_IPv4()
    {
        var result = EventXmlParser.TryParse(RdpCoreTs140("198.51.100.42"));
        Assert.NotNull(result);
        Assert.Equal(IPAddress.Parse("198.51.100.42"), result!.Ip);
        Assert.Equal(string.Empty, result.User);
        Assert.Equal("RdpCoreTS/140", result.Source);
    }

    [Fact]
    public void Parses_RdpCoreTs_140_IPv6()
    {
        var result = EventXmlParser.TryParse(RdpCoreTs140("2001:db8::abcd"));
        Assert.NotNull(result);
        Assert.Equal(IPAddress.Parse("2001:db8::abcd"), result!.Ip);
    }

    [Fact]
    public void Rejects_UnknownEventId()
    {
        var xml = Security4625("203.0.113.7").Replace("<EventID>4625</EventID>", "<EventID>4624</EventID>");
        var result = EventXmlParser.TryParse(xml);
        Assert.Null(result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not xml")]
    [InlineData("<bad")]
    public void Rejects_GarbageInput(string xml)
    {
        var result = EventXmlParser.TryParse(xml);
        Assert.Null(result);
    }

    [Fact]
    public void Returns_Null_When_4625_Missing_IpAddress()
    {
        var xml = Security4625("203.0.113.7").Replace("<Data Name=\"IpAddress\">203.0.113.7</Data>", string.Empty);
        var result = EventXmlParser.TryParse(xml);
        Assert.Null(result);
    }
}
