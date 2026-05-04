using System.Diagnostics.Eventing.Reader;
using System.Runtime.Versioning;
using BlockRdpBruteForce.Eventing;

namespace BlockRdpBruteForce.Tests;

[SupportedOSPlatform("windows")]
public sealed class BookmarkStoreTests : IDisposable
{
    private readonly string _path;

    public BookmarkStoreTests()
    {
        _path = Path.Combine(Path.GetTempPath(),
            $"brbf-bookmark-{Guid.NewGuid():N}.xml");
    }

    public void Dispose()
    {
        try { if (File.Exists(_path)) File.Delete(_path); } catch { }
        try { if (File.Exists(_path + ".tmp")) File.Delete(_path + ".tmp"); } catch { }
    }

    [Fact]
    public void TryLoad_returns_null_when_file_missing()
    {
        var store = new BookmarkStore(_path);
        Assert.Null(store.TryLoad());
    }

    [Fact]
    public void Save_then_TryLoad_round_trips_xml()
    {
        const string xml =
            "<BookmarkList><Bookmark Channel='Security' RecordId='42' " +
            "IsCurrent='true'/></BookmarkList>";
        var store = new BookmarkStore(_path);
        store.Save(new EventBookmark(xml));

        var reloaded = store.TryLoad();
        Assert.NotNull(reloaded);
        Assert.Equal(xml, reloaded!.BookmarkXml);
    }

    [Fact]
    public void Save_overwrites_existing_atomically()
    {
        var store = new BookmarkStore(_path);
        store.Save(new EventBookmark("<v1/>"));
        store.Save(new EventBookmark("<v2/>"));

        Assert.False(File.Exists(_path + ".tmp"));
        Assert.Equal("<v2/>", store.TryLoad()!.BookmarkXml);
    }

    [Fact]
    public void Delete_removes_persisted_bookmark()
    {
        var store = new BookmarkStore(_path);
        store.Save(new EventBookmark("<v/>"));
        Assert.True(File.Exists(_path));

        store.Delete();
        Assert.False(File.Exists(_path));
        Assert.Null(store.TryLoad());
    }

    [Fact]
    public void TryLoad_returns_null_for_empty_file()
    {
        File.WriteAllText(_path, "");
        var store = new BookmarkStore(_path);
        Assert.Null(store.TryLoad());
    }

    [Fact]
    public void Save_creates_missing_directory()
    {
        var nested = Path.Combine(Path.GetTempPath(),
            $"brbf-bookmark-{Guid.NewGuid():N}", "sub", "bookmark.xml");
        try
        {
            var store = new BookmarkStore(nested);
            store.Save(new EventBookmark("<v/>"));
            Assert.True(File.Exists(nested));
        }
        finally
        {
            var parent = Path.GetDirectoryName(Path.GetDirectoryName(nested));
            if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
                Directory.Delete(parent, recursive: true);
        }
    }
}
