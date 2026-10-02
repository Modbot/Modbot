using System.Globalization;
using System.Net;
using System.Net.Http;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Features.Calendar;
using Modbot.Core.Discord;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Calendar;

/// <summary>
/// Calendar design §3.3.1 and §16 (2026-10-02): the role an event's channel post may mention, and
/// Discord events that look like copies of each other, with Modbot's own marked.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class CalendarDiscordExtrasTests(PostgresFixture db)
{
    private const string Guild = "515151515151515151";
    private const string Open = "515151515151515152";
    private const string Closed = "515151515151515153";
    private const string Channel = "515151515151515154";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The server in settings, with @everyone, a role open to mentions and one that is not.</summary>
    private async Task SeedServerAsync(bool botCanMentionEveryone = false)
    {
        await using var context = db.NewContext();

        var settings = await context.GetSettingsAsync(Ct);
        settings.DiscordGuildId = Guild;

        await context.DiscordRoles.Where(r => r.GuildId == Guild).ExecuteDeleteAsync(Ct);
        await context.DiscordServers.Where(s => s.GuildId == Guild).ExecuteDeleteAsync(Ct);

        context.DiscordServers.Add(new DiscordServer
        {
            GuildId = Guild,
            Name = "Test",
            BotCanMentionEveryone = botCanMentionEveryone,
            RefreshedAt = Now,
            UpdatedAt = Now,
        });

        context.DiscordRoles.AddRange(
            new DiscordRole { RoleId = Guild, GuildId = Guild, Name = "@everyone", Everyone = true, Mentionable = true, FirstSeenAt = Now, UpdatedAt = Now },
            new DiscordRole { RoleId = Open, GuildId = Guild, Name = "GameNight", Mentionable = true, FirstSeenAt = Now, UpdatedAt = Now },
            new DiscordRole { RoleId = Closed, GuildId = Guild, Name = "Staff", Mentionable = false, FirstSeenAt = Now, UpdatedAt = Now });

        await context.SaveChangesAsync(Ct);
    }

    private static CalendarEventRequest Mentioning(string? roleId) => new(
        "Game night", "Bring a controller", "2026-10-09T20:00", "2026-10-09T22:00", "UTC", "none", [], null,
        "wrld_calendar", "members", "us", null, null, "gaming", [], [], [], "group", false,
        PublishToVRChat: false, PublishToDiscord: false, PostToChannel: true, ChannelId: Channel,
        AutoOpen: false, OpenMinutesBefore: 10, Draft: false, MentionRoleId: roleId);

    // ── The role the channel post mentions ──────────────────────────────────────────────

    [Fact]
    public async Task ARoleOpenToMentions_MayBeMentioned_AndNoRoleIsFine()
    {
        await SeedServerAsync();
        await using var context = db.NewContext();

        Assert.Null(await CalendarEndpoints.MentionProblemAsync(context, Mentioning(Open), kept: null, Ct));
        Assert.Null(await CalendarEndpoints.MentionProblemAsync(context, Mentioning(null), kept: null, Ct));
        Assert.Null(await CalendarEndpoints.MentionProblemAsync(context, Mentioning("  "), kept: null, Ct));
    }

    [Fact]
    public async Task Everyone_IsRefused_ByItsIdOrItsRow_EvenWhenKept()
    {
        await SeedServerAsync(botCanMentionEveryone: true);
        await using var context = db.NewContext();

        Assert.Equal(
            "The post cannot mention @everyone.",
            await CalendarEndpoints.MentionProblemAsync(context, Mentioning(Guild), kept: null, Ct));
        Assert.Equal(
            "The post cannot mention @everyone.",
            await CalendarEndpoints.MentionProblemAsync(context, Mentioning(Guild), kept: Guild, Ct));
    }

    [Fact]
    public async Task ARoleNotOpenToMentions_IsRefused_UnlessTheBotMayMentionAnyRole()
    {
        await SeedServerAsync();

        await using (var context = db.NewContext())
        {
            Assert.Equal(
                "The bot may not mention that role.",
                await CalendarEndpoints.MentionProblemAsync(context, Mentioning(Closed), kept: null, Ct));

            // Already on the event: an unrelated edit is not refused for a change made in Discord.
            Assert.Null(await CalendarEndpoints.MentionProblemAsync(context, Mentioning(Closed), kept: Closed, Ct));

            Assert.Equal(
                "That role is not in the Discord server.",
                await CalendarEndpoints.MentionProblemAsync(context, Mentioning("515151515151515199"), kept: null, Ct));
        }

        await SeedServerAsync(botCanMentionEveryone: true);

        await using (var context = db.NewContext())
        {
            Assert.Null(await CalendarEndpoints.MentionProblemAsync(context, Mentioning(Closed), kept: null, Ct));
        }
    }

    [Fact]
    public async Task SavingAnEventThatMentionsEveryone_IsRefused()
    {
        await SeedServerAsync();
        await using var host = await ApiTestHost.StartAsync(db);
        var (_, manager) = await host.SignedInAsync(ModbotPermissions.ViewCalendar | ModbotPermissions.ManageCalendar, Ct);

        var start = host.Clock.UtcNow.AddDays(2);
        var body = new
        {
            title = "Game night",
            description = "Bring a controller",
            startsAt = start.ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture),
            endsAt = start.AddHours(2).ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture),
            timeZone = "UTC",
            repeat = "none",
            worldId = "wrld_calendar",
            publishToVRChat = false,
            publishToDiscord = false,
            postToChannel = true,
            channelId = Channel,
            autoOpen = false,
            openMinutesBefore = 10,
            draft = false,
            mentionRoleId = Guild,
        };

        var refused = await host.SendJsonAsync(HttpMethod.Post, "/api/calendar/events", body, manager, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("The post cannot mention @everyone.", (await ApiTestHost.BodyOf(refused, Ct)).GetProperty("error").GetString());
    }

    // ── Possible duplicates in Discord ──────────────────────────────────────────────────

    private sealed class Listed(params DiscordServerEvent[] events) : IDiscordServerEvents
    {
        public Task<DiscordServerEventList?> ReadAsync(string guildId, CancellationToken ct = default) =>
            Task.FromResult<DiscordServerEventList?>(new DiscordServerEventList(events, Now));
    }

    private sealed class NotRead : IDiscordServerEvents
    {
        public Task<DiscordServerEventList?> ReadAsync(string guildId, CancellationToken ct = default) =>
            Task.FromResult<DiscordServerEventList?>(null);
    }

    [Fact]
    public async Task ModbotsOwnCopyIsMarked_AndOpensItsCalendarEvent_WhileAnotherBotsIsNamed()
    {
        await SeedServerAsync();
        var friday = Now.AddDays(1);
        var eventId = Guid.CreateVersion7();

        await using (var context = db.NewContext())
        {
            await context.CalendarEventPlaces.Where(p => p.ExternalId == "900").ExecuteDeleteAsync(Ct);

            context.CalendarEvents.Add(new CalendarEvent
            {
                Id = eventId,
                Title = "Sleepy Hollow Watch Party",
                StartsAt = friday,
                EndsAt = friday.AddHours(2),
                TimeZone = "UTC",
                State = CalendarEventStates.Scheduled,
                PublishToDiscord = true,
                CreatedAt = Now,
                UpdatedAt = Now,
            });
            context.CalendarEventPlaces.Add(new CalendarEventPlace
            {
                EventId = eventId,
                Place = CalendarPlaces.DiscordEvent,
                State = CalendarPlaceStates.Published,
                ExternalId = "900",
                UpdatedAt = Now,
            });
            await context.SaveChangesAsync(Ct);
        }

        var discord = new Listed(
            new DiscordServerEvent("900", "Sleepy Hollow Watch Party", friday, null, false, DiscordEventMakers.Modbot, "Modbot"),
            new DiscordServerEvent("901", "[VRChat, Group Public] Sleepy Hollow Watch Party 🎃", friday, null, false, DiscordEventMakers.Bot, "ChronicleBot"),
            new DiscordServerEvent("902", "sleepy hollow watch party", friday.AddMinutes(10), null, false, DiscordEventMakers.Person, null),
            new DiscordServerEvent("903", "Karaoke", friday, null, false, DiscordEventMakers.Bot, "ChronicleBot"));

        await using (var context = db.NewContext())
        {
            var view = await CalendarDiscordDuplicates.BuildAsync(context, discord, Ct);

            Assert.True(view.Read);
            var duplicate = Assert.Single(view.Duplicates);
            Assert.Equal(3, duplicate.Copies.Count);

            var ours = Assert.Single(duplicate.Copies, c => c.Id == "900");
            Assert.Equal(DiscordEventMakers.Modbot, ours.MadeBy);
            Assert.Equal(eventId, ours.CalendarEventId);
            Assert.Equal("Sleepy Hollow Watch Party", ours.CalendarEventTitle);

            var theirs = Assert.Single(duplicate.Copies, c => c.Id == "901");
            Assert.Equal(DiscordEventMakers.Bot, theirs.MadeBy);
            Assert.Equal("ChronicleBot", theirs.BotName);
            Assert.Null(theirs.CalendarEventId);

            // A person is never named.
            var person = Assert.Single(duplicate.Copies, c => c.Id == "902");
            Assert.Equal(DiscordEventMakers.Person, person.MadeBy);
            Assert.Null(person.BotName);
        }
    }

    [Fact]
    public async Task WithDiscordNotRead_NothingIsSaidAboutDuplicates()
    {
        await SeedServerAsync();
        await using var context = db.NewContext();

        var view = await CalendarDiscordDuplicates.BuildAsync(context, new NotRead(), Ct);
        Assert.False(view.Read);
        Assert.Empty(view.Duplicates);

        var none = await CalendarDiscordDuplicates.BuildAsync(context, serverEvents: null, Ct);
        Assert.False(none.Read);
    }
}
