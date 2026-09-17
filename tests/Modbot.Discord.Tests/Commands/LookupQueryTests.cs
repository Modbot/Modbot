using Microsoft.Extensions.DependencyInjection;
using Modbot.Discord.Commands;
using Modbot.TestSupport;

namespace Modbot.Discord.Tests.Commands;

/// <summary>
/// How <c>/lookup</c> finds a person by name: the whole name first, then part of one, each as
/// typed and in its plain spelling (names design).
/// </summary>
[Collection(nameof(PostgresCollection))]
public class LookupQueryTests
{
    private readonly PostgresFixture _db;

    public LookupQueryTests(PostgresFixture db) => _db = db;

    [Fact]
    public async Task ANameInAFontOrLookAlikeLettersIsFoundByItsPlainSpelling()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await services.AddProfileAsync("usr_fancy", "༻sᴜɢᴀʀʙᴜɴɴɪᴇ༺", ct: ct);
        await services.AddProfileAsync("usr_lookalike", "Addеrаll", ct: ct);
        await services.AddProfileAsync("usr_plain", "Adderall Fan", ct: ct);

        using var scope = services.Scope();
        var lookup = scope.ServiceProvider.GetRequiredService<LookupQuery>();

        // The whole name, in plain letters, before a partial match on the plain one.
        var whole = await lookup.FindAsync("adderall", ct);
        Assert.Equal(["usr_lookalike"], whole.Select(m => m.UserId));

        var part = await lookup.FindAsync("sugar", ct);
        Assert.Equal(["usr_fancy"], part.Select(m => m.UserId));
        Assert.Equal("༻sᴜɢᴀʀʙᴜɴɴɪᴇ༺", part[0].DisplayName);

        // As written still works, and decoration alone finds nobody.
        Assert.Equal(["usr_fancy"], (await lookup.FindAsync("sᴜɢᴀʀ", ct)).Select(m => m.UserId));
        Assert.Empty(await lookup.FindAsync("༒", ct));
    }
}
