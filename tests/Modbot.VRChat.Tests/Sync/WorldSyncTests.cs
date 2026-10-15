using System.Net;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;
using Modbot.VRChat.Sync;

namespace Modbot.VRChat.Tests.Sync;

/// <summary>
/// The world sweep writes a world's error only when VRChat refused it. A pass that could not ask
/// leaves the world unread, because the world lists' save refuses a world carrying an error.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class WorldSyncTests(PostgresFixture fixture) : SyncTestBase(fixture)
{
    private async Task SeedAsync(params string[] worldIds)
    {
        await using var context = Database.NewContext();

        for (var i = 0; i < worldIds.Length; i++)
        {
            context.VRChatWorlds.Add(new VRChatWorld
            {
                WorldId = worldIds[i],
                FirstSeenAt = Clock.UtcNow.AddMinutes(i),
                LastSeenAt = Clock.UtcNow.AddMinutes(i),
            });
        }

        await context.SaveChangesAsync(Ct);
    }

    private async Task<WorldSyncRunResult> RunWorldsAsync()
    {
        await using var context = Database.NewContext();
        return await new WorldSync(Gate, context, Clock).RunOnceAsync(Ct);
    }

    private async Task<string?> ErrorOfAsync(string worldId)
    {
        await using var context = Database.NewContext();
        return (await context.VRChatWorlds.AsNoTracking().SingleAsync(w => w.WorldId == worldId, Ct)).RefreshError;
    }

    [Fact]
    public async Task AWorldVRChatRefuses_GetsItsError()
    {
        await SeedAsync("wrld_private");
        VRChat.Worlds.Status("wrld_private", HttpStatusCode.Forbidden);

        var run = await RunWorldsAsync();

        Assert.Equal(1, run.Failed);
        Assert.NotNull(await ErrorOfAsync("wrld_private"));
    }

    [Fact]
    public async Task WhenVRChatCannotBeAsked_TheWorldStaysUnread_AndThePassStops()
    {
        await SeedAsync("wrld_first", "wrld_second");
        VRChat.Worlds.Status("wrld_first", HttpStatusCode.ServiceUnavailable).Status("wrld_second", HttpStatusCode.Forbidden);

        var run = await RunWorldsAsync();

        Assert.Equal(SyncOutcome.Failed, run.Outcome);
        Assert.Equal(["wrld_first"], VRChat.Worlds.Requests);
        Assert.Null(await ErrorOfAsync("wrld_first"));
        Assert.Null(await ErrorOfAsync("wrld_second"));
    }
}
