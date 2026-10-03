using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Api.Features.Calendar;
using Modbot.Core.Calendar;
using Modbot.Core.Data.Entities;
using Modbot.Core.Google;
using Modbot.Core.Security;
using Modbot.TestSupport;
using Modbot.VRChat.Calendar;

namespace Modbot.Api.Tests.Features.Calendar;

/// <summary>
/// The Google Calendar sending loop (Google Calendar design §3.4 to §3.7, step 2) against a scripted
/// Google: the id is written before the insert and an insert with no answer is read back, never sent
/// twice; a limit stops every call; a 401 gets one new token; dates are changed on their own; and
/// removal, a changed calendar and a cancel go the way §3.6 says.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class CalendarGooglePublisherTests(PostgresFixture db) : IDisposable
{
    private const string CalendarId = "c_events@group.calendar.google.com";
    private const string WorldId = "wrld_google";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeGoogle _google = new();
    private GoogleSignIn? _signIn;
    private RSA? _rsa;

    public void Dispose() => _rsa?.Dispose();

    /// <summary>Google set up, checked and sending, with no events.</summary>
    private async Task SetUpAsync(Action<Modbot.Core.Data.Entities.Settings>? change = null)
    {
        _signIn = new GoogleSignIn(new OneHandlerClients(_google), _clock);

        var (json, rsa) = FakeGoogle.KeyFile();
        _rsa = rsa;
        var key = GoogleKeyFile.Parse(json, out _)!;

        await using var context = db.NewContext();
        await context.CalendarDateChanges.ExecuteDeleteAsync(Ct);
        await context.CalendarEventPlaces.ExecuteDeleteAsync(Ct);
        await context.CalendarEvents.ExecuteDeleteAsync(Ct);

        var settings = await context.GetSettingsAsync(Ct);
        settings.ManagedGroupName = "Test group";
        settings.GoogleClientEmail = key.ClientEmail;
        settings.GoogleKeyId = key.KeyId;
        settings.GooglePrivateKeyEncrypted = key.PrivateKeyPem;
        settings.GoogleCalendarId = CalendarId;
        settings.GoogleCheckedAt = _clock.UtcNow.AddHours(-1);
        settings.GoogleCanChange = true;
        settings.GoogleProblem = null;
        settings.GoogleSendingOn = true;
        settings.GoogleRemovingEvents = false;
        settings.GoogleStoppedUntil = null;
        change?.Invoke(settings);

        await context.SaveChangesAsync(Ct);
    }

    private async Task<CalendarGoogleResult> PassAsync()
    {
        await using var context = db.NewContext();
        var facts = new CalendarFacts(new FactWriter(context, _clock), new EventPartitionMaintainer(context, _clock), _clock);

        return await new CalendarGooglePublisher(
            context, _clock, new PlainProtector(), _signIn!, new GoogleCalendarClient(new OneHandlerClients(_google)), facts)
            .RunOnceAsync(Ct);
    }

    private async Task<CalendarEvent> AddEventAsync(Action<CalendarEvent>? shape = null)
    {
        var now = _clock.UtcNow;
        var e = new CalendarEvent
        {
            Id = Guid.CreateVersion7(),
            Title = "Movie night",
            Description = "Bring snacks",
            StartsAt = now.AddDays(2),
            EndsAt = now.AddDays(2).AddHours(2),
            TimeZone = "Europe/London",
            WorldId = WorldId,
            Visibility = "public",
            PublishToGoogle = true,
            State = CalendarEventStates.Scheduled,
            CreatedAt = now.AddMinutes(-5),
            UpdatedAt = now.AddMinutes(-5),
        };

        shape?.Invoke(e);

        await using var context = db.NewContext();
        context.CalendarEvents.Add(e);
        await context.SaveChangesAsync(Ct);
        return e;
    }

    private async Task EditAsync(Guid id, Action<CalendarEvent> change)
    {
        await using var context = db.NewContext();
        var e = await context.CalendarEvents.SingleAsync(x => x.Id == id, Ct);
        change(e);
        e.Version++;
        e.UpdatedAt = _clock.UtcNow;
        await context.SaveChangesAsync(Ct);
    }

    private async Task<CalendarEventPlace?> PlaceAsync(Guid id)
    {
        await using var context = db.NewContext();
        return await context.CalendarEventPlaces.AsNoTracking()
            .SingleOrDefaultAsync(p => p.EventId == id && p.Place == CalendarPlaces.Google, Ct);
    }

    private async Task<Modbot.Core.Data.Entities.Settings> SettingsAsync()
    {
        await using var context = db.NewContext();
        return await context.Settings.AsNoTracking().SingleAsync(s => s.Id == 1, Ct);
    }

    private List<string> Methods() => [.. _google.EventCalls.Select(c => c.Method.Method)];

    private void Settle() => _clock.Advance(CalendarGooglePublisher.SettleFor + TimeSpan.FromSeconds(1));

    [Fact]
    public async Task AnEventIsInsertedOnce_WithTheIdModbotChose_AndOnlyTheWorldPageAsALink()
    {
        await SetUpAsync();
        var e = await AddEventAsync();

        var result = await PassAsync();

        Assert.Equal(CalendarGoogleOutcome.Written, result.Outcome);
        Assert.Equal(["POST"], Methods());

        var id = GoogleEventIds.For(e.Id, 0);
        var sent = _google.Events[(CalendarId, id)];
        Assert.Equal("Movie night", sent["summary"]!.GetValue<string>());
        Assert.Equal("transparent", sent["transparency"]!.GetValue<string>());
        Assert.Equal(e.Id.ToString("D"), sent["extendedProperties"]!["private"]!["modbotEvent"]!.GetValue<string>());
        Assert.Equal("https://vrchat.com/home/world/" + WorldId, sent["source"]!["url"]!.GetValue<string>());
        Assert.DoesNotContain("/api/calendar/join", sent.ToJsonString(), StringComparison.Ordinal);

        var place = await PlaceAsync(e.Id);
        Assert.Equal(CalendarPlaceStates.Published, place!.State);
        Assert.Equal(id, place.ExternalId);
        Assert.Equal(CalendarId, place.GoogleCalendarId);

        // Nothing changed: nothing more is sent.
        _clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(CalendarGoogleOutcome.NothingToDo, (await PassAsync()).Outcome);
        Assert.Single(_google.EventCalls);
    }

    [Fact]
    public async Task AMembersOnlyEventIsNeverSent_TickedOrNot()
    {
        await SetUpAsync();
        var e = await AddEventAsync(x => x.Visibility = "group");

        await PassAsync();

        Assert.Empty(_google.EventCalls);
        Assert.Null(await PlaceAsync(e.Id));
    }

    [Fact]
    public async Task WithSendingOffNothingIsAskedOfGoogleAtAll()
    {
        await SetUpAsync(s => s.GoogleSendingOn = false);
        await AddEventAsync();

        Assert.Equal(CalendarGoogleOutcome.Off, (await PassAsync()).Outcome);
        Assert.Empty(_google.Requests);
    }

    [Fact]
    public async Task AnInsertWhoseAnswerWasLost_IsReadBackAMinuteOn_AndNeverSentTwice()
    {
        await SetUpAsync();
        var e = await AddEventAsync();
        _google.DropAnswer = (method, _) => method == HttpMethod.Post;

        await PassAsync();

        var place = await PlaceAsync(e.Id);
        Assert.Equal(CalendarPlaceStates.Waiting, place!.State);
        Assert.Equal(GoogleEventIds.For(e.Id, 0), place.ExternalId);
        Assert.Null(place.SentFingerprint);

        // Not even read back before a minute is out.
        _google.DropAnswer = null;
        _clock.Advance(TimeSpan.FromSeconds(30));
        await PassAsync();
        Assert.Equal(["POST"], Methods());

        _clock.Advance(TimeSpan.FromSeconds(31));
        await PassAsync();

        // Read back, found as Modbot's own, written once more: one event on Google.
        Assert.Equal(["POST", "GET", "PUT"], Methods());
        Assert.Single(_google.Events);
        Assert.Equal(CalendarPlaceStates.Published, (await PlaceAsync(e.Id))!.State);
    }

    [Fact]
    public async Task AnInsertThatWasNotMade_IsSentAgainWithTheSameId()
    {
        await SetUpAsync();
        var e = await AddEventAsync();
        var first = true;
        _google.Answer = (method, _) =>
        {
            if (method != HttpMethod.Post || !first)
                return null;

            first = false;
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        };

        await PassAsync();
        _clock.Advance(CalendarGooglePublisher.NoAnswerWait + TimeSpan.FromSeconds(1));
        await PassAsync();

        Assert.Equal(["POST", "GET", "POST"], Methods());
        var posts = _google.EventCalls.Where(c => c.Method == HttpMethod.Post).Select(c => JsonNode.Parse(c.Body!)!["id"]!.GetValue<string>()).ToList();
        Assert.Equal(posts[0], posts[1]);
        Assert.Equal(CalendarPlaceStates.Published, (await PlaceAsync(e.Id))!.State);
    }

    [Fact]
    public async Task ATakenIdThatIsModbotsOwn_IsAdoptedAndWrittenOver()
    {
        await SetUpAsync();
        var e = await AddEventAsync();
        _google.Events[(CalendarId, GoogleEventIds.For(e.Id, 0))] = Owned(e.Id, "An older title");

        await PassAsync();

        Assert.Equal(["POST", "GET", "PUT"], Methods());
        Assert.Equal("Movie night", _google.Events[(CalendarId, GoogleEventIds.For(e.Id, 0))]["summary"]!.GetValue<string>());
        Assert.Equal(CalendarPlaceStates.Published, (await PlaceAsync(e.Id))!.State);
    }

    [Fact]
    public async Task ATakenIdThatWasDeletedOnGoogle_MakesTheEventWithTheNextId()
    {
        await SetUpAsync();
        var e = await AddEventAsync();
        var gone = Owned(e.Id, "Movie night");
        gone["status"] = "cancelled";
        _google.Events[(CalendarId, GoogleEventIds.For(e.Id, 0))] = gone;

        await PassAsync();

        Assert.Equal(["POST", "GET", "POST"], Methods());
        Assert.Equal(GoogleEventIds.For(e.Id, 1), (await PlaceAsync(e.Id))!.ExternalId);
        Assert.True(_google.Events.ContainsKey((CalendarId, GoogleEventIds.For(e.Id, 1))));
    }

    [Fact]
    public async Task ATakenIdThatIsSomebodyElses_IsNeverWrittenOver()
    {
        await SetUpAsync();
        var e = await AddEventAsync();
        _google.Events[(CalendarId, GoogleEventIds.For(e.Id, 0))] = Owned(Guid.NewGuid(), "Not ours");

        await PassAsync();

        Assert.Equal(["POST", "GET"], Methods());
        var place = await PlaceAsync(e.Id);
        Assert.Equal(CalendarPlaceStates.Failed, place!.State);
        Assert.Equal(CalendarGooglePublisher.TakenError, place.Error);
        Assert.Equal(GoogleEventIds.For(e.Id, 1), place.ExternalId);
        Assert.Equal("Not ours", _google.Events[(CalendarId, GoogleEventIds.For(e.Id, 0))]["summary"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(429, "rateLimitExceeded")]
    [InlineData(403, "rateLimitExceeded")]
    [InlineData(403, "userRateLimitExceeded")]
    public async Task ALimitStopsEveryCallUntilItEnds_ThenOneProbe(int status, string reason)
    {
        await SetUpAsync();
        await AddEventAsync();
        await AddEventAsync(x => x.Title = "Second");
        var limited = true;
        _google.Answer = (_, _) => limited ? FakeGoogle.Error((HttpStatusCode)status, reason) : null;

        var result = await PassAsync();

        // One call, refused; nothing else in the pass, the second event included.
        Assert.Equal(CalendarGoogleOutcome.Stopped, result.Outcome);
        Assert.Single(_google.EventCalls);
        Assert.Equal(_clock.UtcNow + GoogleErrors.LimitStop, (await SettingsAsync()).GoogleStoppedUntil);

        // Nothing of any kind before it ends, a token request included.
        limited = false;
        var requests = _google.Requests.Count;
        _clock.Advance(TimeSpan.FromMinutes(14));
        Assert.Equal(CalendarGoogleOutcome.Stopped, (await PassAsync()).Outcome);
        Assert.Equal(requests, _google.Requests.Count);

        // Then the pass opens with one call; once it is answered, the stop is over.
        _clock.Advance(TimeSpan.FromMinutes(2));
        await PassAsync();
        Assert.Equal("GET", Methods()[1]);
        Assert.Null((await SettingsAsync()).GoogleStoppedUntil);
    }

    [Fact]
    public async Task ARefusedTokenGetsOneNewToken()
    {
        await SetUpAsync();
        var e = await AddEventAsync();
        _google.RefuseTokenOnce = true;

        await PassAsync();

        Assert.Equal(2, _google.TokenRequests);
        Assert.Equal(["POST", "POST"], Methods());
        Assert.Equal(CalendarPlaceStates.Published, (await PlaceAsync(e.Id))!.State);
    }

    [Fact]
    public async Task UntickingDeletesTheEvent_AndAnEventAlreadyGoneCountsAsRemoved()
    {
        await SetUpAsync();
        var kept = await AddEventAsync();
        var gone = await AddEventAsync(x => x.Title = "Gone already");
        await PassAsync();

        _google.Events.Remove((CalendarId, GoogleEventIds.For(gone.Id, 0)));
        await EditAsync(kept.Id, x => x.PublishToGoogle = false);
        await EditAsync(gone.Id, x => x.PublishToGoogle = false);
        await PassAsync();

        Assert.Equal(CalendarPlaceStates.Removed, (await PlaceAsync(kept.Id))!.State);
        Assert.Equal(CalendarPlaceStates.Removed, (await PlaceAsync(gone.Id))!.State);
        Assert.Equal("cancelled", _google.Events[(CalendarId, GoogleEventIds.For(kept.Id, 0))]["status"]!.GetValue<string>());

        // Ticked again: made with the next id, never the deleted one.
        await EditAsync(kept.Id, x => x.PublishToGoogle = true);
        Settle();
        await PassAsync();

        Assert.Equal(GoogleEventIds.For(kept.Id, 1), (await PlaceAsync(kept.Id))!.ExternalId);
    }

    [Fact]
    public async Task AnotherCalendarInSettings_TakesTheEventOffTheOldOne_AndPutsItOnTheNew()
    {
        await SetUpAsync();
        var e = await AddEventAsync();
        await PassAsync();

        const string Other = "c_other@group.calendar.google.com";
        await using (var context = db.NewContext())
        {
            var settings = await context.GetSettingsAsync(Ct);
            settings.GoogleCalendarId = Other;
            await context.SaveChangesAsync(Ct);
        }

        await PassAsync();
        await PassAsync();

        var calls = _google.EventCalls;
        Assert.Equal(HttpMethod.Delete, calls[1].Method);
        Assert.Contains(Uri.EscapeDataString(CalendarId), calls[1].Uri.AbsoluteUri, StringComparison.Ordinal);
        Assert.Equal(HttpMethod.Post, calls[2].Method);
        Assert.Contains(Uri.EscapeDataString(Other), calls[2].Uri.AbsoluteUri, StringComparison.Ordinal);

        var place = await PlaceAsync(e.Id);
        Assert.Equal(Other, place!.GoogleCalendarId);
        Assert.Equal(CalendarPlaceStates.Published, place.State);
    }

    [Fact]
    public async Task AMovedDateIsLookedUpAndChangedOnItsOwn_AndSentAgainAfterTheSeriesIsWritten()
    {
        await SetUpAsync();
        var e = await AddEventAsync(x => x.Repeat = CalendarRepeats.Weekly);
        await PassAsync();

        var second = e.StartsAt.AddDays(7);
        await AddDateChangeAsync(e.Id, second, c =>
        {
            c.StartsAt = second.AddHours(1);
            c.EndsAt = second.AddHours(3);
            c.Title = "Double feature";
        });

        await PassAsync();

        var (_, instanceId, body) = Assert.Single(_google.InstanceWrites);
        Assert.StartsWith(GoogleEventIds.For(e.Id, 0) + "_", instanceId, StringComparison.Ordinal);
        Assert.Equal("Double feature", body["summary"]!.GetValue<string>());
        Assert.Equal("confirmed", body["status"]!.GetValue<string>());
        Assert.NotNull((await DateAsync(e.Id)).GoogleSentFingerprint);

        // The whole series changes: written again, then the date is sent again.
        await EditAsync(e.Id, x => x.Description = "Bring snacks and a friend");
        Settle();
        await PassAsync();

        Assert.Equal(2, _google.InstanceWrites.Count);
    }

    [Fact]
    public async Task ADatePutBackAsPlannedIsSentBack_ThenItsRowIsForgotten()
    {
        await SetUpAsync();
        var e = await AddEventAsync(x => x.Repeat = CalendarRepeats.Weekly);
        await PassAsync();

        var second = e.StartsAt.AddDays(7);
        await AddDateChangeAsync(e.Id, second, c =>
        {
            c.StartsAt = second.AddHours(1);
            c.EndsAt = second.AddHours(3);
        });
        await PassAsync();

        await using (var context = db.NewContext())
        {
            var row = await context.CalendarDateChanges.SingleAsync(c => c.EventId == e.Id, Ct);
            row.StartsAt = null;
            row.EndsAt = null;
            row.UpdatedAt = _clock.UtcNow.AddMinutes(-1);
            await context.SaveChangesAsync(Ct);
        }

        await PassAsync();

        Assert.Equal(2, _google.InstanceWrites.Count);
        Assert.Equal("Movie night", _google.InstanceWrites[1].Body["summary"]!.GetValue<string>());

        await using var after = db.NewContext();
        Assert.False(await after.CalendarDateChanges.AnyAsync(c => c.EventId == e.Id, Ct));
    }

    [Fact]
    public async Task ACancelledEventIsMarkedForADay_ThenRemoved()
    {
        await SetUpAsync();
        var e = await AddEventAsync();
        await PassAsync();

        await EditAsync(e.Id, x =>
        {
            x.State = CalendarEventStates.Cancelled;
            x.CancelledAt = _clock.UtcNow;
        });
        Settle();
        await PassAsync();

        var id = GoogleEventIds.For(e.Id, 0);
        Assert.Equal("Cancelled: Movie night", _google.Events[(CalendarId, id)]["summary"]!.GetValue<string>());
        Assert.Equal(CalendarPlaceStates.Published, (await PlaceAsync(e.Id))!.State);

        _clock.UtcNow = e.EndsAt + CalendarGoogle.KeepCancelledFor + TimeSpan.FromMinutes(1);
        await PassAsync();

        Assert.Equal(CalendarPlaceStates.Removed, (await PlaceAsync(e.Id))!.State);
        Assert.Equal("cancelled", _google.Events[(CalendarId, id)]["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task RemoveModbotsEvents_DeletesEveryOne_ThenStops()
    {
        await SetUpAsync();
        var first = await AddEventAsync();
        var second = await AddEventAsync(x => x.Title = "Second");
        await PassAsync();

        await using (var context = db.NewContext())
        {
            var settings = await context.GetSettingsAsync(Ct);
            settings.GoogleSendingOn = false;
            settings.GoogleRemovingEvents = true;
            await context.SaveChangesAsync(Ct);
        }

        await PassAsync();

        Assert.Equal(2, _google.EventCalls.Count(c => c.Method == HttpMethod.Delete));
        Assert.Equal(CalendarPlaceStates.Removed, (await PlaceAsync(first.Id))!.State);
        Assert.Equal(CalendarPlaceStates.Removed, (await PlaceAsync(second.Id))!.State);
        Assert.False((await SettingsAsync()).GoogleRemovingEvents);

        // Done, and Sending is off: nothing more.
        var calls = _google.Requests.Count;
        Assert.Equal(CalendarGoogleOutcome.Off, (await PassAsync()).Outcome);
        Assert.Equal(calls, _google.Requests.Count);
    }

    [Fact]
    public async Task ARefusedKeyStopsSending_AndSaysSoWhereCheckDoes()
    {
        await SetUpAsync();
        await AddEventAsync();
        _google.TokenStatus = HttpStatusCode.BadRequest;

        await PassAsync();

        Assert.Empty(_google.EventCalls);
        Assert.Equal("Google did not accept the key.", (await SettingsAsync()).GoogleProblem);

        _clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(CalendarGoogleOutcome.Off, (await PassAsync()).Outcome);
    }

    [Fact]
    public async Task ACalendarNoLongerSharedToChange_FailsAndWaitsForTheNextGoodCheck()
    {
        await SetUpAsync();
        var e = await AddEventAsync();
        var refused = true;
        _google.Answer = (_, _) => refused ? FakeGoogle.Error(HttpStatusCode.Forbidden, "forbidden") : null;

        await PassAsync();

        var place = await PlaceAsync(e.Id);
        Assert.Equal(CalendarPlaceStates.Failed, place!.State);
        Assert.Equal(GoogleErrors.CannotChange, place.Error);
        Assert.Equal(GoogleErrors.CannotChange, (await SettingsAsync()).GoogleProblem);

        // A good Check later: sent again with no edit.
        refused = false;
        _clock.Advance(TimeSpan.FromMinutes(5));
        await using (var context = db.NewContext())
        {
            var settings = await context.GetSettingsAsync(Ct);
            settings.GoogleProblem = null;
            settings.GoogleCheckedAt = _clock.UtcNow;
            await context.SaveChangesAsync(Ct);
        }

        _clock.Advance(TimeSpan.FromSeconds(1));
        await PassAsync();

        Assert.Equal(CalendarPlaceStates.Published, (await PlaceAsync(e.Id))!.State);
    }

    private async Task AddDateChangeAsync(Guid id, DateTimeOffset planned, Action<CalendarDateChange> change)
    {
        await using var context = db.NewContext();
        var e = await context.CalendarEvents.SingleAsync(x => x.Id == id, Ct);

        var row = new CalendarDateChange
        {
            Id = Guid.CreateVersion7(),
            EventId = id,
            PlannedStartsAt = planned,
            CreatedAt = _clock.UtcNow,

            // Settled already: the pass after this one sends it.
            UpdatedAt = _clock.UtcNow - CalendarGooglePublisher.SettleFor - TimeSpan.FromSeconds(1),
        };

        change(row);
        e.DateChanges.Add(row);
        await context.SaveChangesAsync(Ct);
    }

    private async Task<CalendarDateChange> DateAsync(Guid id)
    {
        await using var context = db.NewContext();
        return await context.CalendarDateChanges.AsNoTracking().SingleAsync(c => c.EventId == id, Ct);
    }

    private static JsonObject Owned(Guid eventId, string summary) => new()
    {
        ["status"] = "confirmed",
        ["summary"] = summary,
        ["extendedProperties"] = new JsonObject
        {
            ["private"] = new JsonObject { [CalendarGoogleBody.OwnerProperty] = eventId.ToString("D") },
        },
    };

    /// <summary>The key is stored as it is: the protector's own tests cover the encryption.</summary>
    private sealed class PlainProtector : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;

        public string? Unprotect(string? ciphertext) => ciphertext;
    }
}
