using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Analytics.Facts;
using Modbot.Core.Data.Entities;
using Modbot.Discord.Cards;
using Modbot.Discord.ModerationLog;
using Modbot.Discord.Tests.Fakes;
using Modbot.TestSupport;

namespace Modbot.Discord.Tests.ModerationLog;

/// <summary>
/// The poster puts the whole story on a card: a Discord member's name and picture from the
/// server's member list, Discord's reason and a timeout's end, and for VRChat's record of a ban
/// Modbot made, who decided it and why.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class WholeStoryPosterTests
{
    private const string Channel = "1234567890";
    private const string Guild = "800000000000000001";
    private const string Member = "900000000000000777";
    private const string Target = "usr_c9094d86-1846-43eb-b79d-7e3dc318f42a";
    private const string ModbotVRChat = "usr_2a323be9-ac4e-4502-af07-357d79c48ccf";
    private const string Avatar = "https://cdn.discordapp.com/avatars/900000000000000777/abc.png";

    private readonly PostgresFixture _db;

    public WholeStoryPosterTests(PostgresFixture db) => _db = db;

    private static async Task<ModerationLogPass> RunAsync(TestServices services, FakeGateway gateway, CancellationToken ct)
    {
        using var scope = services.Scope();
        var poster = scope.ServiceProvider.GetRequiredService<ModerationLogPoster>();
        return await poster.RunOnceAsync(gateway, (_, _) => Task.CompletedTask, ct);
    }

    [Fact]
    public async Task ADiscordTimeout_IsHeadedByTheMember_AndSaysWhyAndUntilWhen()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await services.ConfigureAsync(s => s.PublicAddress = "https://modbot.example.com", ct);
        var gateway = new FakeGateway();

        await using (var db = services.Database.NewContext())
        {
            db.DiscordMembers.Add(new DiscordMember
            {
                GuildId = Guild,
                UserId = Member,
                Username = "jessie_q",
                DisplayName = "Jessie",
                AvatarUrl = Avatar,
                FirstSeenAt = services.Clock.UtcNow,
                UpdatedAt = services.Clock.UtcNow,
            });
            await db.SaveChangesAsync(ct);
        }

        await services.AddRouteAsync(Channel, [FactType.DiscordMemberTimedOut], ct: ct);
        await RunAsync(services, gateway, ct);

        var until = services.Clock.UtcNow.AddHours(6);

        await services.WriteFactAsync(new FactRecord
        {
            Type = FactType.DiscordMemberTimedOut,
            OccurredAt = services.Clock.UtcNow,
            SubjectPlatform = FactPlatform.Discord,
            SubjectId = Member,
            ActorPlatform = FactPlatform.Discord,
            ActorId = "900000000000000111",
            Source = FactSource.Discord,
            Data = new JsonObject
            {
                ["auditEntryId"] = "1",
                ["reason"] = "Cool off",
                ["until"] = until,
                ["displayName"] = "jessie at the time",
                ["actorDisplayName"] = "E-Ray",
            },
        }, ct);

        await RunAsync(services, gateway, ct);

        var card = Assert.Single(Assert.Single(gateway.Posts).Embeds);

        Assert.Equal("Timed out on Discord", card.Title);
        Assert.Equal("Jessie", card.AuthorName);
        Assert.Equal(Avatar, card.AuthorIconUrl);
        Assert.Equal($"https://modbot.example.com/discord/members?subject=discord-person%3A{Member}", card.AuthorUrl);
        Assert.Equal("Cool off", card.Fields.Single(f => f.Name == "Reason").Value);
        Assert.Contains($"<t:{until.ToUnixTimeSeconds()}:f>", card.Fields.Single(f => f.Name == "Until").Value, StringComparison.Ordinal);
        Assert.StartsWith("[E-Ray](", card.Fields.Single(f => f.Name == "By").Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VRChatsBan_OfOneModbotMade_CarriesTheModeratorAndTheReasons()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var gateway = new FakeGateway();

        ModbotUser moderator;
        await using (var db = services.Database.NewContext())
            moderator = await TestAccounts.CreateAsync(db, "sam", TestAccounts.Password, ModbotPermissions.Ban, linked: false, ct);

        await services.AddRouteAsync(Channel, [FactType.MemberBanned], ct: ct);
        await RunAsync(services, gateway, ct);

        var pressed = services.Clock.UtcNow;

        // Modbot's record of the press, then VRChat's record of the ban a few seconds later.
        await services.WriteFactAsync(new FactRecord
        {
            Type = FactType.ActionBan,
            OccurredAt = pressed,
            SubjectPlatform = FactPlatform.VRChat,
            SubjectId = Target,
            ActorPlatform = FactPlatform.Modbot,
            ActorId = moderator.Id.ToString(),
            Source = FactSource.Manual,
            Data = new JsonObject
            {
                ["action"] = "ban",
                ["actorDisplayName"] = "sam",
                ["reasonLabels"] = new JsonArray("Harassment"),
                ["note"] = "Third time",
            },
        }, ct);

        await services.WriteAuditFactAsync(FactType.MemberBanned, Target, ModbotVRChat, "Modbot", at: pressed.AddSeconds(3), ct: ct);

        // A ban somebody made in VRChat itself, long after: nobody decided it in Modbot.
        await services.WriteAuditFactAsync(FactType.MemberBanned, "usr_other", ModbotVRChat, "Modbot", at: pressed.AddMinutes(5), ct: ct);

        await RunAsync(services, gateway, ct);

        var cards = Assert.Single(gateway.Posts).Embeds;
        Assert.Equal(2, cards.Count);

        Assert.Equal("sam", cards[0].Fields.Single(f => f.Name == "Decided by").Value);
        Assert.Equal("Harassment", cards[0].Fields.Single(f => f.Name == "Reason").Value);
        Assert.DoesNotContain(cards[0].Fields, f => f.Name == "Note" || f.Value.Contains("Third time", StringComparison.Ordinal));

        Assert.DoesNotContain(cards[1].Fields, f => f.Name is "Decided by" or "Reason");
    }

    /// <summary>
    /// Ten long cards in one message came to more than Discord's six thousand characters, Discord
    /// refused it, and the same message was tried again forever. A message is now filled by size
    /// as well as by count, and every card still goes out, in order.
    /// </summary>
    [Fact]
    public async Task LongCards_AreSpreadOverMessages_SoNoneIsOverDiscordsLimit()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var gateway = new FakeGateway();

        await services.AddRouteAsync(Channel, [FactType.UserProfileChanged], ct: ct);
        await RunAsync(services, gateway, ct);

        for (var i = 0; i < 10; i++)
        {
            var changed = new JsonObject();
            for (var field = 0; field < 8; field++)
                changed[$"field{field}"] = new JsonObject { ["old"] = new string('o', 80), ["new"] = new string('n', 80) };

            await services.WriteFactAsync(new FactRecord
            {
                Type = FactType.UserProfileChanged,
                OccurredAt = services.Clock.UtcNow.AddSeconds(i),
                SubjectPlatform = FactPlatform.VRChat,
                SubjectId = $"usr_long_{i}",
                Source = FactSource.Modbot,
                Data = new JsonObject { ["description"] = new string('d', 400), ["changed"] = changed },
            }, ct);
        }

        var pass = await RunAsync(services, gateway, ct);

        Assert.Equal(10, pass.Posted);
        Assert.True(gateway.Posts.Count > 1);
        Assert.All(gateway.Posts, post => Assert.True(post.Embeds.Sum(EmbedSize.Of) <= EmbedSize.MessageLimit));
        Assert.Equal(10, gateway.Posts.Sum(p => p.Embeds.Count));
    }
}
