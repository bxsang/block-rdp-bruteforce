using System.Diagnostics.Eventing.Reader;
using System.Runtime.Versioning;

namespace BlockRdpBruteForce.Eventing;

[SupportedOSPlatform("windows")]
public sealed class BookmarkStore
{
    private readonly object _gate = new();
    private readonly string _path;

    public BookmarkStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Environment.ExpandEnvironmentVariables(path);
    }

    public string ResolvedPath => _path;

    public EventBookmark? TryLoad()
    {
        lock (_gate)
        {
            if (!File.Exists(_path)) return null;
            try
            {
                var xml = File.ReadAllText(_path);
                if (string.IsNullOrWhiteSpace(xml)) return null;
                return new EventBookmark(xml);
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    public void Save(EventBookmark bookmark)
    {
        ArgumentNullException.ThrowIfNull(bookmark);
        lock (_gate)
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = _path + ".tmp";

            // Antivirus and indexer briefly hold the file after we close it;
            // retry with backoff to ride out transient sharing violations.
            const int maxAttempts = 4;
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.WriteAllText(tmp, bookmark.BookmarkXml);
                    if (File.Exists(_path))
                        File.Replace(tmp, _path, destinationBackupFileName: null);
                    else
                        File.Move(tmp, _path);
                    return;
                }
                catch (IOException) when (attempt < maxAttempts)
                {
                    Thread.Sleep(25 * attempt);
                }
            }
        }
    }

    public void Delete()
    {
        lock (_gate)
        {
            if (File.Exists(_path))
            {
                try { File.Delete(_path); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
