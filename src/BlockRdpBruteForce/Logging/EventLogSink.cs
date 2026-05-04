using System.Diagnostics;
using System.Runtime.Versioning;
using Serilog;
using Serilog.Configuration;
using Serilog.Core;
using Serilog.Events;

namespace BlockRdpBruteForce.Logging;

[SupportedOSPlatform("windows")]
public sealed class EventLogSink : ILogEventSink, IDisposable
{
    private readonly EventLog _eventLog;
    private readonly IFormatProvider? _formatProvider;
    private bool _logFailureReported;

    public EventLogSink(string source, string logName, IFormatProvider? formatProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(logName);
        _eventLog = new EventLog(logName) { Source = source };
        _formatProvider = formatProvider;
    }

    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);

        var message = logEvent.RenderMessage(_formatProvider);
        if (logEvent.Exception is not null)
            message = string.Concat(message, Environment.NewLine, logEvent.Exception);

        var entryType = logEvent.Level switch
        {
            LogEventLevel.Fatal or LogEventLevel.Error => EventLogEntryType.Error,
            LogEventLevel.Warning => EventLogEntryType.Warning,
            _ => EventLogEntryType.Information,
        };

        try
        {
            _eventLog.WriteEntry(Truncate(message), entryType);
        }
        catch (Exception ex) when (
            ex is System.Security.SecurityException or InvalidOperationException or ArgumentException)
        {
            if (!_logFailureReported)
            {
                _logFailureReported = true;
                SelfLog.WriteLine("EventLogSink suppressed write error ({0}): {1}", ex.GetType().Name, ex.Message);
            }
        }
    }

    private static string Truncate(string s) =>
        s.Length <= 31_800 ? s : s[..31_800];

    public void Dispose() => _eventLog.Dispose();
}

public static class EventLogSinkExtensions
{
    [SupportedOSPlatform("windows")]
    public static LoggerConfiguration WindowsEventLog(
        this LoggerSinkConfiguration sinkConfiguration,
        string source,
        string logName = "Application",
        LogEventLevel restrictedToMinimumLevel = LogEventLevel.Information,
        IFormatProvider? formatProvider = null)
    {
        ArgumentNullException.ThrowIfNull(sinkConfiguration);
        return sinkConfiguration.Sink(
            new EventLogSink(source, logName, formatProvider),
            restrictedToMinimumLevel);
    }
}

internal static class SelfLog
{
    public static void WriteLine(string format, params object?[] args)
    {
        Serilog.Debugging.SelfLog.WriteLine(format, args);
    }
}
