using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.ShareLinks.Configuration;
using Jellyfin.Plugin.ShareLinks.Models;
using Jellyfin.Plugin.ShareLinks.Services;
using Jellyfin.Plugin.ShareLinks.Storage;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Jellyfin.Plugin.ShareLinks.Tests;

/// <summary>Shared fixtures. Jellyfin keeps a few things in statics, so they are set once here.</summary>
internal static class TestSupport
{
    private static readonly object Gate = new();
    private static Plugin? _plugin;

    /// <summary>
    /// Stands in for the library behind BaseItem's static LibraryManager, which items
    /// use to find their parents. Shared by every test, so set it up with fresh ids only.
    /// </summary>
    public static Mock<ILibraryManager> StaticLibrary { get; } = new();

    /// <summary>
    /// Plugin.Instance with a default configuration. Created once: it is a static,
    /// and tests run in parallel, so nothing may change the configuration it holds.
    /// </summary>
    public static Plugin EnsurePlugin()
    {
        lock (Gate)
        {
            if (_plugin is not null)
            {
                return _plugin;
            }

            var directory = Path.Combine(Path.GetTempPath(), "sharelinks-tests", "plugin-config");
            Directory.CreateDirectory(directory);
            var paths = new Mock<IApplicationPaths>();
            paths.SetupGet(p => p.PluginConfigurationsPath).Returns(directory);
            paths.SetupGet(p => p.PluginsPath).Returns(directory);
            paths.SetupGet(p => p.DataPath).Returns(directory);
            var xml = new Mock<IXmlSerializer>();
            xml.Setup(x => x.DeserializeFromFile(typeof(PluginConfiguration), It.IsAny<string>()))
                .Returns(new PluginConfiguration());
            _plugin = new Plugin(paths.Object, xml.Object);
            _ = _plugin.Configuration;

            BaseItem.LibraryManager = StaticLibrary.Object;

            // A video asks this whether it is still being recorded. None are.
            Video.RecordingsManager = new Mock<MediaBrowser.Controller.LiveTv.IRecordingsManager>().Object;
            return _plugin;
        }
    }

    public static User Guest(Guid? id = null)
    {
        return new User("share-" + Guid.NewGuid().ToString("N"), GuestAuthenticationProvider.ProviderId, "reset")
        {
            Id = id ?? Guid.NewGuid()
        };
    }

    public static User Regular(Guid? id = null)
    {
        return new User("someone-" + Guid.NewGuid().ToString("N")[..6], "Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider", "reset")
        {
            Id = id ?? Guid.NewGuid()
        };
    }

    public static ShareLinkRecord ActiveRecord(Guid guestUserId, Action<ShareLinkRecord>? configure = null)
    {
        var record = new ShareLinkRecord
        {
            Id = Guid.NewGuid(),
            TokenHash = Guid.NewGuid().ToString("N"),
            ItemId = Guid.NewGuid().ToString("D"),
            GuestUserId = guestUserId,
            GuestUserName = "share-guest",
            CreatedByUserId = Guid.NewGuid(),
            CreatedAtUtc = DateTimeOffset.UtcNow,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(4),
            Status = ShareLinkStatus.Redeemed,
            AllowedTag = "sharelinks-" + Guid.NewGuid().ToString("N"),
            OneUse = false
        };
        configure?.Invoke(record);
        return record;
    }

    public static T Item<T>(string? name = null)
        where T : BaseItem, new()
    {
        return new T { Id = Guid.NewGuid(), Name = name ?? typeof(T).Name };
    }
}

/// <summary>A store in its own temporary folder, removed afterwards.</summary>
internal sealed class TempStore : IDisposable
{
    public TempStore()
    {
        Directory = Path.Combine(Path.GetTempPath(), "sharelinks-tests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);
        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(p => p.DataPath).Returns(Directory);
        Paths = paths.Object;
        Store = new ShareLinkStore(Paths, NullLogger<ShareLinkStore>.Instance);
    }

    public string Directory { get; }

    public IApplicationPaths Paths { get; }

    public ShareLinkStore Store { get; }

    public ShareLinkStore Reopen() => new(Paths, NullLogger<ShareLinkStore>.Instance);

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>An item graph described by hand, for the tag tree walk.</summary>
internal sealed class FakeTree : IItemTreeSource
{
    private readonly Dictionary<Guid, List<BaseItem>> _physical = new();
    private readonly Dictionary<Guid, List<BaseItem>> _linked = new();
    private readonly Dictionary<Guid, List<BaseItem>> _artist = new();

    public List<BaseItem> Tagged { get; } = new();

    public FakeTree Physical(BaseItem parent, params BaseItem[] descendants)
    {
        Get(_physical, parent.Id).AddRange(descendants);
        return this;
    }

    public FakeTree Linked(BaseItem parent, params BaseItem[] children)
    {
        Get(_linked, parent.Id).AddRange(children);
        return this;
    }

    public FakeTree ArtistMedia(MusicArtist artist, params BaseItem[] media)
    {
        Get(_artist, artist.Id).AddRange(media);
        return this;
    }

    public IEnumerable<BaseItem> GetPhysicalDescendants(Folder folder) => Find(_physical, folder.Id);

    public IEnumerable<BaseItem> GetLinkedChildren(Folder folder) => Find(_linked, folder.Id);

    public IEnumerable<BaseItem> GetArtistMedia(MusicArtist artist) => Find(_artist, artist.Id);

    public IEnumerable<BaseItem> GetItemsWithTag(string tag)
    {
        return Tagged.Where(item => item.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)).ToList();
    }

    private static List<BaseItem> Get(Dictionary<Guid, List<BaseItem>> map, Guid id)
    {
        if (!map.TryGetValue(id, out var list))
        {
            list = new List<BaseItem>();
            map[id] = list;
        }

        return list;
    }

    private static IEnumerable<BaseItem> Find(Dictionary<Guid, List<BaseItem>> map, Guid id)
    {
        return map.TryGetValue(id, out var list) ? list : Enumerable.Empty<BaseItem>();
    }
}
