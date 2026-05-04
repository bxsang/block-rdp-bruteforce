using System.Diagnostics.Eventing.Reader;
using System.Runtime.Versioning;
using System.Threading.Channels;

namespace BlockRdpBruteForce.Eventing;

[SupportedOSPlatform("windows")]
public abstract class EventLogSubscriberBase : IDisposable
{
    private readonly string _logName;
    private readonly string _queryXPath;
    private readonly BookmarkStore _bookmarks;
    private readonly ChannelWriter<FailedLogon> _writer;
    private readonly ILogger _log;
    private EventLogWatcher? _watcher;
    private bool _disposed;

    protected EventLogSubscriberBase(
        string logName,
        string queryXPath,
        BookmarkStore bookmarks,
        ChannelWriter<FailedLogon> writer,
        ILogger log)
    {
        _logName = logName;
        _queryXPath = queryXPath;
        _bookmarks = bookmarks;
        _writer = writer;
        _log = log;
    }

    public string LogName => _logName;

    public void Start()
    {
        var bookmark = _bookmarks.TryLoad();
        if (TryEnable(bookmark)) return;

        if (bookmark is not null)
        {
            _log.LogWarning(
                "Bookmark for {Log} could not be replayed (log cleared or rolled). " +
                "Resuming from now; failures before this point will be missed.", _logName);
            _bookmarks.Delete();
            if (TryEnable(null)) return;
        }

        _log.LogError("Failed to subscribe to {Log}; subscriber inactive.", _logName);
    }

    private bool TryEnable(EventBookmark? bookmark)
    {
        try
        {
            var query = new EventLogQuery(_logName, PathType.LogName, _queryXPath);
            var watcher = new EventLogWatcher(query, bookmark, readExistingEvents: false);
            watcher.EventRecordWritten += OnEventWritten;
            watcher.Enabled = true;

            DisposeWatcher();
            _watcher = watcher;
            _log.LogInformation("Subscribed to {Log} (bookmark: {State}).",
                _logName, bookmark is null ? "starting from now" : "resumed");
            return true;
        }
        catch (EventLogException ex)
        {
            _log.LogWarning(ex, "EventLogException starting subscriber on {Log}", _logName);
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            _log.LogError(ex, "Access denied subscribing to {Log}; service must run as LocalSystem", _logName);
            return false;
        }
    }

    private void OnEventWritten(object? sender, EventRecordWrittenEventArgs e)
    {
        if (_disposed) return;

        if (e.EventException is not null)
        {
            _log.LogWarning(e.EventException, "Event subscription error on {Log}", _logName);
            return;
        }

        var record = e.EventRecord;
        if (record is null) return;

        try
        {
            string xml;
            try { xml = record.ToXml(); }
            catch (EventLogException ex)
            {
                _log.LogWarning(ex, "Failed to render event XML on {Log}", _logName);
                return;
            }

            var failed = ParseRecord(xml);
            if (failed is not null)
                WriteToChannel(failed);

            try
            {
                if (record.Bookmark is not null)
                    _bookmarks.Save(record.Bookmark);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to persist bookmark for {Log}", _logName);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Unhandled error processing {Log} event", _logName);
        }
        finally
        {
            record.Dispose();
        }
    }

    private void WriteToChannel(FailedLogon failed)
    {
        if (_writer.TryWrite(failed)) return;
        try
        {
            _writer.WriteAsync(failed).AsTask().GetAwaiter().GetResult();
        }
        catch (ChannelClosedException)
        {
        }
    }

    protected abstract FailedLogon? ParseRecord(string xml);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisposeWatcher();
        GC.SuppressFinalize(this);
    }

    private void DisposeWatcher()
    {
        if (_watcher is null) return;
        try { _watcher.Enabled = false; }
        catch (EventLogException) { }
        _watcher.EventRecordWritten -= OnEventWritten;
        _watcher.Dispose();
        _watcher = null;
    }
}
