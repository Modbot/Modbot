using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Analytics.Facts;
using Modbot.Core.Calendar;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Discord.Calendar;
using Modbot.Discord.Commands;
using Modbot.Discord.Gateway;
using Modbot.Discord.Tests.Fakes;
using Modbot.TestSupport;

namespace Modbot.Discord.Tests.Commands;

/// <summary>
/// <c>/events</c> (Discord commands design §3.6 and §4, step 5): off until the operator turns it on,
/// open to every member, the next five dates in two weeks, only what is already published in this
/// Discord, and a Join button only for an event anyone can join whose instance is open.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class EventsCommandTests
{
    private const string Guild = "111111111111111111";
    private const string Channel = "222222222222222222";
    private const string World = "wrld_4432ea9b-729c-46e3-8eaf-846aa0a37fdd";
    private const string Group = "grp_0a17232e-6ad4-4889-8e1e-6e0c5fa815fd";

    private readonly PostgresFixture _db;

    public EventsCommandTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DiscordCommandCall Call(string caller, params (string Name, string Value)[] options)
        => new(
            caller,
            "someone",
            DiscordCommands.Events,
            options.ToDictionary(o => o.Name, o => o.Value, StringComparer.Ordinal),
            (_, _) => Task.CompletedTask);

    private static DiscordCommandCall HelpCall(string caller)
        => new(caller, "someone", DiscordCommands.Help, new Dictionary<string, string>(), (_, _) => Task.CompletedTask);

    private static async Task<TestServices> SetUpAsync(PostgresFixture db, bool on = true)
    {
        var services = await TestServices.CreateAsync(db, Ct);
        await services.ConfigureAsync(s =>
        {
            s.DiscordGuildId = Guild;
            s.SwitchCommand(DiscordCommands.Events, on);
        }, Ct);
        return services;
    }

    /// <summary>Runs it as the bot does: through the switch, as a member with no Modbot account.</summary>
    private static async Task<DiscordReply?> AskAsync(TestServices services, string caller = "999", params (string Name, string Value)[] options)
    {
        using var scope = services.Scope();
        return await scope.ServiceProvider.GetRequiredService<DiscordCommandHandler>().RunAsync(Call(caller, options), Ct);
    }

    private static string Unix(DateTimeOffset at) => at.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

    /// <summary>A scheduled event published to this Discord, as the calendar leaves it once its post is up.</summary>
    private static async Task<CalendarEvent> AddEventAsync(
        TestServices services,
        string title,
        TimeSpan startsIn,
        Action<CalendarEvent>? shape = null,
        string? place = CalendarPlaces.DiscordEvent,
        string placeState = CalendarPlaceStates.Published)
    {
        var now = services.Clock.UtcNow;
        var e = new CalendarEvent
        {
            Id = Guid.CreateVersion7(),
            Title = title,
            Description = "Bring snacks",
            StartsAt = now + startsIn,
            EndsAt = now + startsIn + TimeSpan.FromHours(2),
            TimeZone = "UTC",
            WorldId = World,
            State = CalendarEventStates.Scheduled,
            PublishToDiscord = true,
            PostToChannel = false,
            ChannelId = Channel,
            AccessType = "members",
            CreatedAt = now,
            UpdatedAt = now,
        };

        shape?.Invoke(e);
        CalendarTimeline.Advance(e, now);

        await using var context = services.Database.NewContext();
        context.CalendarEvents.Add(e);

        if (place is not null)
        {
            context.CalendarEventPlaces.Add(new CalendarEventPlace
            {
                EventId = e.Id,
                Place = place,
                State = placeState,
                ExternalId = "5551234",
                ChannelId = Channel,
                OccurrenceStartsAt = e.OccurrenceStartsAt,
                UpdatedAt = now,
            });
        }

        await context.SaveChangesAsync(Ct);
        return e;
    }

    private static async Task AddWorldAsync(TestServices services, string name)
    {
        await using var context = services.Database.NewContext();
        context.VRChatWorlds.Add(new VRChatWorld
        {
            WorldId = World,
            Name = name,
            FirstSeenAt = services.Clock.UtcNow,
            LastSeenAt = services.Clock.UtcNow,
        });
        await context.SaveChangesAsync(Ct);
    }

    /// <summary>What the VRChat side leaves when it opens the instance: the instance and the opening row.</summary>
    private static async Task<string> OpenInstanceAsync(TestServices services, CalendarEvent e, bool closed = false)
    {
        // One instance for each event, so each has a link of its own.
        var number = e.Id.ToString("N")[^10..];
        var location = $"{World}:{number}~group({Group})~groupAccessType({e.AccessType})~region(us)";
        var now = services.Clock.UtcNow;

        await using var context = services.Database.NewContext();

        var instance = new VRChatInstance
        {
            Id = Guid.CreateVersion7(),
            Location = location,
            WorldId = World,
            VRChatInstanceId = number,
            GroupId = Group,
            OpenedAt = now,
            LastSeenAt = now,
            ClosedAt = closed ? now : null,
        };

        context.VRChatInstances.Add(instance);
        context.CalendarOpenings.Add(new CalendarOpening
        {
            EventId = e.Id,
            OccurrenceStartsAt = e.OccurrenceStartsAt ?? e.StartsAt,
            AttemptedAt = now,
            Location = location,
            InstanceId = instance.Id,
        });

        await context.SaveChangesAsync(Ct);
        return InstanceJoinLink.For(location)!;
    }

    // ── What is registered ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Events_IsForEveryone_OffByDefault_AndRepliesInPublicUnlessTheMemberAsksNot()
    {
        var events = Assert.Single(DiscordCommands.All, c => c.Name == DiscordCommands.Events);

        Assert.Equal(DiscordShownTo.Everyone, events.ShownTo);
        Assert.False(events.StaffOnly);
        Assert.Equal(DiscordReplyKind.Chosen, events.Reply);
        Assert.Equal("private", events.PrivateOption);
        Assert.Equal(TimeSpan.FromSeconds(60), events.PublicOncePer);
        Assert.Equal("Upcoming events, in your time zone", events.Description);

        var ask = Assert.Single(events.Options);
        Assert.Equal("private", ask.Name);
        Assert.Equal("Only show me", ask.Description);
        Assert.Equal(DiscordOptionKind.YesNo, ask.Kind);
        Assert.False(ask.Required);

        Assert.True(events.RepliesInPublic(new Dictionary<string, string>()));
        Assert.False(events.RepliesInPublic(new Dictionary<string, string> { ["private"] = "true" }));
        Assert.True(events.RepliesInPublic(new Dictionary<string, string> { ["private"] = "false" }));

        Assert.False(DiscordCommandSwitches.Find(DiscordCommands.Events)?.OnByDefault);
        Assert.DoesNotContain(DiscordCommands.For(DiscordCommandSwitches.Empty), c => c.Name == DiscordCommands.Events);
        Assert.Contains(
            DiscordCommands.For(DiscordCommandSwitches.With(null, DiscordCommands.Events, true)),
            c => c.Name == DiscordCommands.Events);

        Assert.True(DiscordCommands.IsForEveryone(DiscordCommands.Events));
        Assert.Null(DiscordCommands.Requires(DiscordCommands.Events));
        Assert.Equal(["`/events private:` — Upcoming events, in your time zone"], DiscordCommandHandler.HelpLines(events));
    }

    [Fact]
    public async Task WhileOff_ItIsTold_AndRecordedAsOff_AndShowsNothing()
    {
        await using var services = await SetUpAsync(_db, on: false);
        await AddEventAsync(services, "Movie night", TimeSpan.FromDays(1));

        var reply = await AskAsync(services);

        Assert.Equal("/events is turned off on this server.", reply?.Text);
        Assert.Equal("off", (await LastCommandFactAsync(services)).GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Help_ListsItForAnyMember_OnlyWhileItIsOn()
    {
        await using var services = await SetUpAsync(_db);

        using (var scope = services.Scope())
        {
            var on = await scope.ServiceProvider.GetRequiredService<DiscordCommandHandler>().HandleAsync(HelpCall("999"), Ct);
            Assert.Contains("`/events private:` — Upcoming events, in your time zone", on.Text, StringComparison.Ordinal);
        }

        await services.ConfigureAsync(s => s.SwitchCommand(DiscordCommands.Events, false), Ct);

        using var again = services.Scope();
        var off = await again.ServiceProvider.GetRequiredService<DiscordCommandHandler>().HandleAsync(HelpCall("999"), Ct);
        Assert.DoesNotContain("/events", off.Text, StringComparison.Ordinal);
    }

    private static async Task<JsonElement> LastCommandFactAsync(TestServices services)
    {
        var facts = await services.FactsOfTypeAsync(FactType.DiscordCommandRun, Ct);
        return JsonDocument.Parse(facts[^1].Data).RootElement;
    }

    // ── What it shows ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AMemberWithNoModbotAccount_GetsTheNextDates_AsTitleTimeAndWorld()
    {
        await using var services = await SetUpAsync(_db);
        await AddWorldAsync(services, "The Black Cat");
        var e = await AddEventAsync(services, "Movie night", TimeSpan.FromDays(1));

        var reply = await AskAsync(services);

        // The title, the time as Discord's own timestamp (each reader sees their own zone), the
        // time to go, and the world's name.
        Assert.Equal(
            $"**Movie night** · <t:{Unix(e.StartsAt)}:f> (<t:{Unix(e.StartsAt)}:R>) · The Black Cat",
            reply?.Text);
        Assert.DoesNotContain("UTC", reply?.Text, StringComparison.Ordinal);
        Assert.Null(reply?.Links);

        var fact = Assert.Single(await services.FactsOfTypeAsync(FactType.DiscordCommandRun, Ct));
        Assert.Equal("999", fact.SubjectId);
        Assert.Null(fact.ActorId);
        Assert.Equal("events", JsonDocument.Parse(fact.Data).RootElement.GetProperty("command").GetString());
        Assert.Equal("answered", JsonDocument.Parse(fact.Data).RootElement.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task ItShowsFiveDates_SoonestFirst_AndNothingPastFourteenDays()
    {
        await using var services = await SetUpAsync(_db);

        for (var day = 7; day >= 1; day--)
            await AddEventAsync(services, $"Event {day}", TimeSpan.FromDays(day));

        await AddEventAsync(services, "Far away", TimeSpan.FromDays(20));

        var lines = (await AskAsync(services))!.Text!.Split('\n');

        Assert.Equal(5, lines.Length);
        Assert.Equal(
            ["Event 1", "Event 2", "Event 3", "Event 4", "Event 5"],
            lines.Select(l => l[2..l.IndexOf("**", 2, StringComparison.Ordinal)]));
    }

    [Fact]
    public async Task TheWindowIsFourteenDays_AndWithNothingInItTheReplySaysSo()
    {
        await using var services = await SetUpAsync(_db);
        await AddEventAsync(services, "Far away", TimeSpan.FromDays(14.1));

        Assert.Equal("No events in the next 14 days.", (await AskAsync(services))?.Text);

        await AddEventAsync(services, "Just inside", TimeSpan.FromDays(13.9));

        Assert.StartsWith("**Just inside**", (await AskAsync(services, "888"))?.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnlyEventsPublishedInThisDiscordAreShown()
    {
        await using var services = await SetUpAsync(_db);

        await AddEventAsync(services, "On the server", TimeSpan.FromHours(1));
        await AddEventAsync(services, "A channel post", TimeSpan.FromHours(2), e => (e.PublishToDiscord, e.PostToChannel) = (false, true), CalendarPlaces.ChannelPost);

        // Never published: no place row at all, or one that is not up.
        await AddEventAsync(services, "Not posted yet", TimeSpan.FromHours(3), place: null);
        await AddEventAsync(services, "Waiting", TimeSpan.FromHours(4), placeState: CalendarPlaceStates.Waiting);
        await AddEventAsync(services, "Failed", TimeSpan.FromHours(5), placeState: CalendarPlaceStates.Failed);
        await AddEventAsync(services, "Taken down", TimeSpan.FromHours(6), placeState: CalendarPlaceStates.Removed);

        // Published once, switched off since: the place row may be a pass behind.
        await AddEventAsync(services, "Switched off", TimeSpan.FromHours(7), e => e.PublishToDiscord = false);

        // Published somewhere that is not Discord.
        await AddEventAsync(services, "On VRChat only", TimeSpan.FromHours(8), e => (e.PublishToDiscord, e.PublishToVRChat) = (false, true), CalendarPlaces.VRChat);

        // States that are not coming up.
        await AddEventAsync(services, "Draft", TimeSpan.FromHours(9), e => e.State = CalendarEventStates.Draft);
        await AddEventAsync(services, "Cancelled", TimeSpan.FromHours(10), e => (e.State, e.CancelledAt) = (CalendarEventStates.Cancelled, services.Clock.UtcNow));
        await AddEventAsync(services, "Finished", TimeSpan.FromHours(11), e => e.State = CalendarEventStates.Finished);
        await AddEventAsync(services, "Deleted", TimeSpan.FromHours(12), e => e.DeletedAt = services.Clock.UtcNow);

        var text = (await AskAsync(services))!.Text!;

        Assert.Equal(["On the server", "A channel post"], text.Split('\n').Select(l => l[2..l.IndexOf("**", 2, StringComparison.Ordinal)]));
    }

    [Fact]
    public async Task WhatThePublisherPostsIsWhatItShows()
    {
        await using var services = await SetUpAsync(_db);
        var gateway = new FakeGateway();

        await AddEventAsync(services, "Movie night", TimeSpan.FromDays(1), e => e.PostToChannel = true, place: null);
        await AddEventAsync(services, "Not on Discord", TimeSpan.FromDays(2), e => (e.PublishToDiscord, e.PostToChannel) = (false, false), place: null);

        using (var scope = services.Scope())
            await scope.ServiceProvider.GetRequiredService<CalendarDiscordPublisher>().RunOnceAsync(gateway, Ct);

        var text = (await AskAsync(services))!.Text!;

        Assert.Single(text.Split('\n'));
        Assert.StartsWith("**Movie night**", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARepeatingEvent_ShowsItsDates_WithACancelledOneLeftOutAndAMovedOneWhereItWent()
    {
        await using var services = await SetUpAsync(_db);

        // Weekly from tomorrow: two dates inside the fortnight.
        var weekly = await AddEventAsync(services, "Weekly", TimeSpan.FromDays(1), e => e.Repeat = CalendarRepeats.Weekly);
        var first = weekly.StartsAt;
        var second = first.AddDays(7);

        var both = (await AskAsync(services))!.Text!.Split('\n');
        Assert.Equal(2, both.Length);
        Assert.Contains($"<t:{Unix(first)}:f>", both[0], StringComparison.Ordinal);
        Assert.Contains($"<t:{Unix(second)}:f>", both[1], StringComparison.Ordinal);

        await using (var context = services.Database.NewContext())
        {
            context.CalendarDateChanges.Add(new CalendarDateChange
            {
                Id = Guid.CreateVersion7(),
                EventId = weekly.Id,
                PlannedStartsAt = first,
                Cancelled = true,
                CreatedAt = services.Clock.UtcNow,
                UpdatedAt = services.Clock.UtcNow,
            });
            context.CalendarDateChanges.Add(new CalendarDateChange
            {
                Id = Guid.CreateVersion7(),
                EventId = weekly.Id,
                PlannedStartsAt = second,
                StartsAt = second.AddHours(3),
                Title = "Weekly, later",
                CreatedAt = services.Clock.UtcNow,
                UpdatedAt = services.Clock.UtcNow,
            });
            await context.SaveChangesAsync(Ct);
        }

        var changed = Assert.Single((await AskAsync(services))!.Text!.Split('\n'));
        Assert.StartsWith("**Weekly, later**", changed, StringComparison.Ordinal);
        Assert.Contains($"<t:{Unix(second.AddHours(3))}:f>", changed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOpenEvent_SaysOpenNow_AndAScheduledOneDoesNot()
    {
        await using var services = await SetUpAsync(_db);

        var open = await AddEventAsync(services, "Running", TimeSpan.FromMinutes(-10));
        await AddEventAsync(services, "Later", TimeSpan.FromHours(5));
        Assert.Equal(CalendarEventStates.Open, open.State);

        var lines = (await AskAsync(services))!.Text!.Split('\n');

        Assert.EndsWith(" · Open now", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("**Running**", lines[0], StringComparison.Ordinal);
        Assert.DoesNotContain("Open now", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheTitleIsShownAsText_NotAsMarkdown()
    {
        await using var services = await SetUpAsync(_db);
        await AddEventAsync(services, "*loud* @everyone", TimeSpan.FromHours(1));

        var text = (await AskAsync(services))!.Text!;

        Assert.StartsWith("**\\*loud\\* @everyone**", text, StringComparison.Ordinal);
    }

    // ── The Join button ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task APublicEventWithAnOpenInstance_GetsAJoinButton_ToTheInstancesLaunchPage()
    {
        await using var services = await SetUpAsync(_db);
        var e = await AddEventAsync(services, "Movie night", TimeSpan.FromMinutes(-10), x => x.AccessType = "public");
        var link = await OpenInstanceAsync(services, e);

        var reply = await AskAsync(services);

        var button = Assert.Single(reply!.Links!);
        Assert.Equal("Join Movie night", button.Label);
        Assert.Equal(link, button.Url);
        Assert.StartsWith("https://vrchat.com/home/launch?", button.Url, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("members")]
    [InlineData("plus")]
    public async Task AnEventOnlyMembersCanJoin_IsListed_WithNoJoinButton(string access)
    {
        await using var services = await SetUpAsync(_db);
        var e = await AddEventAsync(services, "Movie night", TimeSpan.FromMinutes(-10), x => x.AccessType = access);
        await OpenInstanceAsync(services, e);

        var reply = await AskAsync(services);

        Assert.Contains("Movie night", reply!.Text, StringComparison.Ordinal);
        Assert.Contains("Open now", reply.Text, StringComparison.Ordinal);
        Assert.Null(reply.Links);
    }

    [Fact]
    public async Task APublicEvent_WithNoInstanceOpenYet_OrOneThatClosed_GetsNoJoinButton()
    {
        await using var services = await SetUpAsync(_db);

        // Open by the clock, but Modbot opened nothing.
        await AddEventAsync(services, "Nothing opened", TimeSpan.FromMinutes(-10), x => x.AccessType = "public");

        // Opened, then closed.
        var closed = await AddEventAsync(services, "Closed", TimeSpan.FromMinutes(-9), x => x.AccessType = "public");
        await OpenInstanceAsync(services, closed, closed: true);

        // Coming up, so no instance yet.
        await AddEventAsync(services, "Later", TimeSpan.FromHours(3), x => x.AccessType = "public");

        var reply = await AskAsync(services);

        Assert.Equal(3, reply!.Text!.Split('\n').Length);
        Assert.Null(reply.Links);
    }

    [Fact]
    public async Task NoMoreThanFiveJoinButtons_OneForEachDateShown()
    {
        await using var services = await SetUpAsync(_db);

        for (var i = 0; i < 6; i++)
        {
            var e = await AddEventAsync(services, "Party " + i, TimeSpan.FromMinutes(-10 - i), x => x.AccessType = "public");
            await OpenInstanceAsync(services, e);
        }

        var reply = await AskAsync(services);

        Assert.Equal(5, reply!.Text!.Split('\n').Length);
        Assert.Equal(5, reply.Links!.Count);
        Assert.Equal(5, reply.Links.Select(l => l.Url).Distinct().Count());
    }

    // ── The member limit ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task OneMemberMayAskFiveTimesAMinute_AndASixthIsToldToWait_UnrecordedAndThenAllowedAgain()
    {
        await using var services = await SetUpAsync(_db);
        await AddEventAsync(services, "Movie night", TimeSpan.FromDays(1));

        for (var i = 0; i < MemberCommandLimits.PerMinute; i++)
            Assert.StartsWith("**Movie night**", (await AskAsync(services, "999"))?.Text, StringComparison.Ordinal);

        Assert.Equal("Slow down. Try again in a minute.", (await AskAsync(services, "999"))?.Text);
        Assert.Equal(MemberCommandLimits.PerMinute, (await services.FactsOfTypeAsync(FactType.DiscordCommandRun, Ct)).Count);

        // Somebody else is not held up, and a minute later neither is the first.
        Assert.StartsWith("**Movie night**", (await AskAsync(services, "888"))?.Text, StringComparison.Ordinal);

        services.Clock.Advance(TimeSpan.FromSeconds(61));
        Assert.StartsWith("**Movie night**", (await AskAsync(services, "999"))?.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ItsLimitIsItsOwn_SoMeAndVerifyDoNotUseItUp()
    {
        await using var services = await SetUpAsync(_db);
        await AddEventAsync(services, "Movie night", TimeSpan.FromDays(1));

        using var scope = services.Scope();
        var me = scope.ServiceProvider.GetRequiredService<MemberCommandLimits>();

        for (var i = 0; i < MemberCommandLimits.PerMinute; i++)
            Assert.True(me.TryUse("999", services.Clock.UtcNow));

        Assert.StartsWith("**Movie night**", (await AskAsync(services, "999"))?.Text, StringComparison.Ordinal);
    }
}
