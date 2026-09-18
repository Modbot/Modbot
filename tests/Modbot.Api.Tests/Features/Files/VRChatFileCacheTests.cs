using Modbot.Api.Features.Files;
using Modbot.TestSupport;
using Modbot.VRChat.Files;

namespace Modbot.Api.Tests.Features.Files;

/// <summary>
/// The cache of VRChat pictures on disk (VRChat files design §5): a hit costs VRChat nothing, a
/// miss stores what came back, and the folder never grows past the cap the operator set.
/// </summary>
public class VRChatFileCacheTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Day = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);

    private const string Address = "https://api.vrchat.cloud/api/1/file/file_abc/1/file";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "modbot-file-cache-tests", Guid.NewGuid().ToString("n"));

    private readonly FakeClock _clock = new() { UtcNow = Day };

    private VRChatFileCache NewCache() =>
        new(new VRChatFileCacheOptions { Root = _root }, _clock);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void AnAddressNeverFetchedIsAMiss()
        => Assert.Null(NewCache().Find(Address));

    [Fact]
    public async Task WhatWasStoredIsFoundAgain_WithTheTypeItCameWith()
    {
        var cache = NewCache();

        await cache.StoreAsync(Address, new VRChatFile([1, 2, 3], "image/jpeg"), 1_000_000, Ct);

        var held = cache.Find(Address);

        Assert.NotNull(held);
        Assert.Equal("image/jpeg", held.ContentType);
        Assert.Equal([1, 2, 3], await ReadAsync(held));

        // The key is the SHA-256 of the address, which is also the ETag: the address names a
        // version, so the bytes behind it never change.
        Assert.Equal(VRChatFileCache.KeyFor(Address), held.Tag);
    }

    /// <summary>Two addresses that differ by one character are two files, not one.</summary>
    [Fact]
    public async Task ADifferentAddressIsADifferentFile()
    {
        var cache = NewCache();
        var other = Address.Replace("/1/file", "/2/file", StringComparison.Ordinal);

        await cache.StoreAsync(Address, new VRChatFile([1], "image/png"), 1_000_000, Ct);

        Assert.True(IsHeld(cache, Address));
        Assert.False(IsHeld(cache, other));
    }

    /// <summary>A cap of zero means no cache at all: nothing is written and every read is a miss.</summary>
    [Fact]
    public async Task WithNoRoomTheCacheStoresNothing()
    {
        var cache = NewCache();

        await cache.StoreAsync(Address, new VRChatFile([1, 2, 3], "image/png"), 0, Ct);

        Assert.Null(cache.Find(Address));
    }

    /// <summary>One file bigger than the whole cap is not stored, rather than stored and swept.</summary>
    [Fact]
    public async Task AFileBiggerThanTheWholeCapIsNotStored()
    {
        var cache = NewCache();

        await cache.StoreAsync(Address, new VRChatFile(new byte[100], "image/png"), 50, Ct);

        Assert.Null(cache.Find(Address));
    }

    /// <summary>
    /// Over the cap, the pictures fetched longest ago go first. The order is Modbot's own clock,
    /// stamped on each file when it was written, so it does not depend on the machine's.
    /// </summary>
    [Fact]
    public async Task OverTheCapTheOldestPicturesGoFirst()
    {
        var cache = NewCache();

        // Three 100-byte pictures, an hour apart, into a cap that holds two of them.
        var first = Address + "?a";
        var second = Address + "?b";
        var third = Address + "?c";

        await cache.StoreAsync(first, new VRChatFile(new byte[100], "image/png"), 1_000_000, Ct);

        _clock.UtcNow = Day.AddHours(1);
        await cache.StoreAsync(second, new VRChatFile(new byte[100], "image/png"), 1_000_000, Ct);

        _clock.UtcNow = Day.AddHours(2);
        await cache.StoreAsync(third, new VRChatFile(new byte[100], "image/png"), 250, Ct);

        Assert.False(IsHeld(cache, first));
        Assert.True(IsHeld(cache, second));
        Assert.True(IsHeld(cache, third));
    }

    /// <summary>The type file goes with the bytes it describes, so a swept file leaves nothing behind.</summary>
    [Fact]
    public async Task SweepingAPictureTakesItsTypeWithIt()
    {
        var cache = NewCache();

        await cache.StoreAsync(Address, new VRChatFile(new byte[100], "image/png"), 1_000_000, Ct);

        _clock.UtcNow = Day.AddHours(1);
        await cache.StoreAsync(Address + "?b", new VRChatFile(new byte[100], "image/png"), 100, Ct);

        Assert.Null(cache.Find(Address));

        var left = Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).ToList();
        Assert.Equal(2, left.Count);
    }

    /// <summary>
    /// A type Modbot would no longer serve is a miss, not something to hand a browser. The cache
    /// outlives a change to what counts as showable, and the rule that matters is today's.
    /// </summary>
    [Fact]
    public async Task AFileWhoseTypeIsNoLongerShowableIsAMiss()
    {
        var cache = NewCache();

        await cache.StoreAsync(Address, new VRChatFile([1], "image/png"), 1_000_000, Ct);

        var type = Directory.EnumerateFiles(_root, "*.type", SearchOption.AllDirectories).Single();
        await File.WriteAllTextAsync(type, "text/html", Ct);

        Assert.Null(cache.Find(Address));
    }

    /// <summary>
    /// <strong>The same picture stored by several requests at once is not corrupted by them.</strong>
    /// Everybody without a picture of their own shares one address, so a member list asks for the
    /// same file several times over, and every one of those is a store. When they all wrote to one
    /// <c>.partial</c> name they filled it together and the first rename carried somebody's
    /// half-written bytes onto the key -- where they stayed, because nothing here ever expires.
    /// </summary>
    [Fact]
    public async Task TheSameAddressStoredManyTimesAtOnceIsStillWholeAfterwards()
    {
        var cache = NewCache();
        var bytes = new byte[256 * 1024];
        Random.Shared.NextBytes(bytes);

        await Task.WhenAll(Enumerable.Range(0, 24).Select(_ =>
            Task.Run(() => cache.StoreAsync(Address, new VRChatFile(bytes, "image/png"), 100_000_000, Ct), Ct)));

        var held = cache.Find(Address);

        Assert.NotNull(held);
        Assert.Equal("image/png", held.ContentType);
        Assert.Equal(bytes, await ReadAsync(held));

        // Nothing half-written is left lying about either.
        Assert.Empty(Directory.EnumerateFiles(_root, "*.partial", SearchOption.AllDirectories));
    }

    /// <summary>
    /// Different pictures stored at once each end up whole, and the sweep running beside them
    /// does not take a picture that is still being written.
    /// </summary>
    [Fact]
    public async Task DifferentAddressesStoredAtOnceEachEndUpWhole()
    {
        var cache = NewCache();

        var files = Enumerable.Range(0, 24)
            .Select(i => (Url: $"{Address}?{i}", Bytes: new byte[] { (byte)i, (byte)(i + 1), (byte)(i + 2) }))
            .ToList();

        await Task.WhenAll(files.Select(f =>
            Task.Run(() => cache.StoreAsync(f.Url, new VRChatFile(f.Bytes, "image/png"), 100_000_000, Ct), Ct)));

        foreach (var (url, expected) in files)
        {
            var held = cache.Find(url);
            Assert.NotNull(held);
            Assert.Equal(expected, await ReadAsync(held));
        }
    }

    /// <summary>
    /// <strong>A file found is a file that can still be read.</strong> The sweep deleting it, or
    /// another store of the same address renaming a new one onto it, must not turn a hit into a
    /// failed response halfway through sending it.
    /// </summary>
    [Fact]
    public async Task AFileAlreadyFoundIsStillReadableAfterTheSweepTakesIt()
    {
        var cache = NewCache();

        await cache.StoreAsync(Address, new VRChatFile([1, 2, 3], "image/png"), 1_000_000, Ct);

        var held = cache.Find(Address);
        Assert.NotNull(held);

        // Everything goes: this is the sweep at its most severe, mid-read.
        _clock.UtcNow = Day.AddHours(1);
        await cache.StoreAsync(Address + "?b", new VRChatFile([9], "image/png"), 1, Ct);

        Assert.Equal([1, 2, 3], await ReadAsync(held));
    }

    /// <summary>Whether the cache holds it, without leaving the file open.</summary>
    private static bool IsHeld(VRChatFileCache cache, string url)
    {
        var held = cache.Find(url);
        held?.Content.Dispose();

        return held is not null;
    }

    private static async Task<byte[]> ReadAsync(CachedFile held)
    {
        await using var content = held.Content;
        using var copy = new MemoryStream();

        await content.CopyToAsync(copy, Ct);

        return copy.ToArray();
    }

    /// <summary>With no folder configured the cache is simply off, and nothing throws.</summary>
    [Fact]
    public async Task WithNoFolderTheCacheIsOff()
    {
        var cache = new VRChatFileCache(new VRChatFileCacheOptions(), _clock);

        Assert.False(cache.IsOn);
        await cache.StoreAsync(Address, new VRChatFile([1], "image/png"), 1_000_000, Ct);
        Assert.Null(cache.Find(Address));
    }
}
