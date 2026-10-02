using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;
using Modbot.VRChat.Calendar;
using Modbot.VRChat.Tests.Sync;

namespace Modbot.VRChat.Tests.Calendar;

/// <summary>
/// Calendar design §17 (2026-10-02): a permission given is picked up without an edit, every problem
/// that can be known is shown before anything is sent, and Try again and an edit send a failed
/// place again.
/// </summary>
/// <remarks>
/// From a recorded test on a live install: VRChat refused the picture id, then Manage Group
/// Calendar; the permission was given, and saving the event again unchanged still showed the old
/// failure, because the refusal was held until the event changed.
/// </remarks>
[Collection(nameof(PostgresCollection))]
public class CalendarVRChatChecksTests(PostgresFixture fixture) : CalendarTestBase(fixture)
{
    private static readonly TimeSpan Settle = CalendarVRChatPublisher.SettleFor + TimeSpan.FromSeconds(1);

    private const string Calendar = VRChatGroupPermissions.ManageCalendar;
    private const string AuditLog = VRChatGroupPermissions.ViewAuditLog;

    // ── A permission given is picked up ─────────────────────────────────────────────────

    [Fact]
    public async Task GivingThePermission_SendsTheEventAtTheNextReadOfTheGroup_WithNoEdit()
    {
        await AccountHoldsAsync(AuditLog);
        VRChat.Groups.GroupJson = MyMember(AuditLog);

        var e = await AddEventAsync(TimeSpan.FromDays(2), x => x.PublishToVRChat = true);
        Clock.Advance(Settle);

        Assert.Equal(CalendarPublishOutcome.Failed, (await PublishAsync()).Outcome);

        // Read once more before refusing on it, and nothing sent to the calendar.
        Assert.Equal(1, VRChat.Groups.GroupRequests);
        Assert.Equal(0, VRChat.Calendar.Calls);

        var place = await PlaceAsync(e.Id, CalendarPlaces.VRChat);
        Assert.Equal(CalendarPlaceStates.Failed, place?.State);
        Assert.Equal(Calendar, place?.MissingGroupPermission);
        Assert.Empty(place?.Problems ?? ["not checked first"]);

        // Held: neither read again nor sent on every pass.
        Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(CalendarPublishOutcome.NothingToDo, (await PublishAsync()).Outcome);
        Assert.Equal(1, VRChat.Groups.GroupRequests);
        Assert.Equal(0, VRChat.Calendar.Calls);

        // The permission is given in VRChat, and the group poll reads it.
        VRChat.Groups.Group = GroupInfoSnapshotTests.Group();
        VRChat.Groups.GroupJson = MyMember(AuditLog, Calendar);
        Clock.Advance(TimeSpan.FromMinutes(1));
        await RunGroupInfoAsync();

        var result = await PublishAsync();

        Assert.Equal(CalendarPublishOutcome.Written, result.Outcome);
        Assert.Equal("create", result.Action);
        Assert.Equal(1, VRChat.Calendar.Calls);

        place = await PlaceAsync(e.Id, CalendarPlaces.VRChat);
        Assert.Equal(CalendarPlaceStates.Published, place?.State);
        Assert.Null(place?.MissingGroupPermission);
        Assert.Null(place?.Problems);
    }

    [Fact]
    public async Task AReadOfTheGroupSayingThePermissionIsMissing_IsReadAgainBeforeRefusing_AndAPermissionGivenSinceIsUsed()
    {
        // The last poll is from before the permission was given.
        await AccountHoldsAsync(AuditLog);
        VRChat.Groups.GroupJson = MyMember(AuditLog, Calendar);

        var e = await AddEventAsync(TimeSpan.FromDays(2), x => x.PublishToVRChat = true);
        Clock.Advance(Settle);

        Assert.Equal(CalendarPublishOutcome.Written, (await PublishAsync()).Outcome);
        Assert.Equal(1, VRChat.Groups.GroupRequests);
        Assert.Single(VRChat.Calendar.Creates);
        Assert.Equal(CalendarPlaceStates.Published, (await PlaceAsync(e.Id, CalendarPlaces.VRChat))?.State);

        // And what the read found is kept, so the next save does not read again.
        Assert.Contains(Calendar, await HeldPermissionsAsync() ?? []);
    }

    [Fact]
    public async Task AGroupNeverReadIsNoProblem_VRChatsOwnAnswerSays()
    {
        var e = await AddEventAsync(TimeSpan.FromDays(2), x => x.PublishToVRChat = true);
        Clock.Advance(Settle);

        Assert.Equal(CalendarPublishOutcome.Written, (await PublishAsync()).Outcome);
        Assert.Equal(0, VRChat.Groups.GroupRequests);
        Assert.Equal(CalendarPlaceStates.Published, (await PlaceAsync(e.Id, CalendarPlaces.VRChat))?.State);
    }

    [Fact]
    public async Task A403IsJudgedAgainstAFreshReadOfTheGroup_SoAnotherReasonIsShownInVRChatsWords()
    {
        // Never read before; the account has the permission, and VRChat refuses for something else.
        VRChat.Groups.GroupJson = MyMember(Calendar);
        VRChat.Calendar.Refuse(HttpStatusCode.Forbidden, "That picture is not yours.");

        var e = await AddEventAsync(TimeSpan.FromDays(2), x => x.PublishToVRChat = true);
        Clock.Advance(Settle);

        Assert.Equal(CalendarPublishOutcome.Failed, (await PublishAsync()).Outcome);
        Assert.Equal(1, VRChat.Groups.GroupRequests);

        var place = await PlaceAsync(e.Id, CalendarPlaces.VRChat);
        Assert.Null(place?.MissingGroupPermission);
        Assert.Equal("That picture is not yours.", place?.Error);
        Assert.Null(place?.Problems);
    }

    [Fact]
    public async Task A403ForThePermission_StandsOnlyUntilAReadOfTheGroupFindsIt()
    {
        // VRChat's own refusal, with nothing read of the group to say otherwise.
        VRChat.Calendar.Refuse(HttpStatusCode.Forbidden, "You do not have permission to do that.");

        var e = await AddEventAsync(TimeSpan.FromDays(2), x => x.PublishToVRChat = true);
        Clock.Advance(Settle);

        Assert.Equal(CalendarPublishOutcome.Failed, (await PublishAsync()).Outcome);
        Assert.Equal(Calendar, (await PlaceAsync(e.Id, CalendarPlaces.VRChat))?.MissingGroupPermission);

        Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(CalendarPublishOutcome.NothingToDo, (await PublishAsync()).Outcome);
        Assert.Equal(1, VRChat.Calendar.Calls);

        VRChat.Groups.Group = GroupInfoSnapshotTests.Group();
        VRChat.Groups.GroupJson = MyMember(Calendar);
        await RunGroupInfoAsync();

        Assert.Equal(CalendarPublishOutcome.Written, (await PublishAsync()).Outcome);
        Assert.Equal(2, VRChat.Calendar.Calls);
        Assert.Equal(CalendarPlaceStates.Published, (await PlaceAsync(e.Id, CalendarPlaces.VRChat))?.State);
    }

    // ── Every problem at once ───────────────────────────────────────────────────────────

    [Fact]
    public async Task EveryProblemFoundBeforeSending_IsShownTogether_ThePermissionFirst_AndNothingIsSent()
    {
        await AccountHoldsAsync(AuditLog);
        VRChat.Groups.GroupJson = MyMember(AuditLog);

        var e = await AddEventAsync(TimeSpan.FromDays(2), x =>
        {
            x.PublishToVRChat = true;
            x.Description = "";
            x.VRChatImageId = "https://example.com/a picture.png";
        });

        Clock.Advance(Settle);

        Assert.Equal(CalendarPublishOutcome.Failed, (await PublishAsync()).Outcome);
        Assert.Equal(0, VRChat.Calendar.Calls);

        var place = await PlaceAsync(e.Id, CalendarPlaces.VRChat);
        Assert.Equal(Calendar, place?.MissingGroupPermission);
        Assert.Equal(new[] { CalendarVRChatChecks.NoDescription, CalendarVRChatChecks.NotAPictureId }, place?.Problems);
        Assert.Equal(
            "Modbot's VRChat account needs Manage Group Calendar in this group. "
            + CalendarVRChatChecks.NoDescription + " " + CalendarVRChatChecks.NotAPictureId,
            place?.Error);

        // One fact, with every problem, the permission first; not said twice as a fix as well.
        var fact = Assert.Single(await FactsOfTypeAsync(FactType.PlannedEventPublishFailed));
        var data = JsonNode.Parse(fact.Data)!;
        Assert.Equal(place?.Error, data["error"]?.ToString());
        Assert.Null(data["fix"]);

        // Fixing the fields with the permission still missing leaves only the permission.
        await EditAsync(e.Id, x =>
        {
            x.Description = "Bring snacks";
            x.VRChatImageId = null;
        });
        Clock.Advance(Settle);

        Assert.Equal(CalendarPublishOutcome.Failed, (await PublishAsync()).Outcome);
        Assert.Equal(0, VRChat.Calendar.Calls);

        place = await PlaceAsync(e.Id, CalendarPlaces.VRChat);
        Assert.Equal(Calendar, place?.MissingGroupPermission);
        Assert.Empty(place?.Problems ?? ["not checked first"]);
    }

    [Fact]
    public async Task AFieldProblemAlone_IsHeldUntilTheEventChanges_AndSendsNothing()
    {
        var e = await AddEventAsync(TimeSpan.FromDays(2), x =>
        {
            x.PublishToVRChat = true;
            x.VRChatImageId = "file_abc def";
        });

        Clock.Advance(Settle);

        Assert.Equal(CalendarPublishOutcome.Failed, (await PublishAsync()).Outcome);
        Assert.Equal(new[] { CalendarVRChatChecks.NotAPictureId }, (await PlaceAsync(e.Id, CalendarPlaces.VRChat))?.Problems);
        Assert.Null((await PlaceAsync(e.Id, CalendarPlaces.VRChat))?.MissingGroupPermission);

        Clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(CalendarPublishOutcome.NothingToDo, (await PublishAsync()).Outcome);
        Assert.Equal(0, VRChat.Calendar.Calls);

        await EditAsync(e.Id, x => x.VRChatImageId = "file_abc");
        Clock.Advance(Settle);

        Assert.Equal(CalendarPublishOutcome.Written, (await PublishAsync()).Outcome);
        Assert.Equal("file_abc", Assert.Single(VRChat.Calendar.Creates).ImageId);
    }

    // ── Try again and an edit ───────────────────────────────────────────────────────────

    [Fact]
    public async Task TryAgain_SendsARefusalAgainWithoutAnEdit_AndASecondPressFindsNothingToDo()
    {
        var e = await AddEventAsync(TimeSpan.FromDays(2), x => x.PublishToVRChat = true);
        Clock.Advance(Settle);
        VRChat.Calendar.Answer(HttpStatusCode.BadRequest);

        Assert.Equal(CalendarPublishOutcome.Failed, (await PublishAsync()).Outcome);
        Assert.Equal(CalendarPublishOutcome.NothingToDo, (await PublishAsync()).Outcome);
        Assert.Equal(1, VRChat.Calendar.Calls);

        Assert.True(await ChangePlaceAsync(e.Id, CalendarVRChatPublisher.TryAgain));
        Assert.False(await ChangePlaceAsync(e.Id, CalendarVRChatPublisher.TryAgain));

        var place = await PlaceAsync(e.Id, CalendarPlaces.VRChat);
        Assert.Equal(CalendarPlaceStates.Waiting, place?.State);
        Assert.Null(place?.Error);

        var result = await PublishAsync();
        Assert.Equal(CalendarPublishOutcome.Written, result.Outcome);
        Assert.Equal("create", result.Action);
        Assert.Equal(2, VRChat.Calendar.Calls);
    }

    [Fact]
    public async Task TryAgain_OnAMissingPermission_ReadsTheGroupFirst_AndSendsOnlyOnceItIsGiven()
    {
        await AccountHoldsAsync(AuditLog);
        VRChat.Groups.GroupJson = MyMember(AuditLog);

        var e = await AddEventAsync(TimeSpan.FromDays(2), x => x.PublishToVRChat = true);
        Clock.Advance(Settle);
        await PublishAsync();

        // Pressed before the permission is given: read again, still missing, nothing sent.
        Assert.True(await ChangePlaceAsync(e.Id, CalendarVRChatPublisher.TryAgain));
        Clock.Advance(TimeSpan.FromSeconds(15));
        Assert.Equal(CalendarPublishOutcome.Failed, (await PublishAsync()).Outcome);
        Assert.Equal(2, VRChat.Groups.GroupRequests);
        Assert.Equal(0, VRChat.Calendar.Calls);

        // Pressed after: read again, found, sent.
        VRChat.Groups.GroupJson = MyMember(AuditLog, Calendar);
        Assert.True(await ChangePlaceAsync(e.Id, CalendarVRChatPublisher.TryAgain));
        Clock.Advance(TimeSpan.FromSeconds(15));

        Assert.Equal(CalendarPublishOutcome.Written, (await PublishAsync()).Outcome);
        Assert.Equal(3, VRChat.Groups.GroupRequests);
        Assert.Equal(1, VRChat.Calendar.Calls);
    }

    [Fact]
    public async Task AnEditClearsAnOldRefusalAtOnce_AndSendsItAgainEvenWhenNothingChanged()
    {
        var e = await AddEventAsync(TimeSpan.FromDays(2), x => x.PublishToVRChat = true);
        Clock.Advance(Settle);
        VRChat.Calendar.Answer(HttpStatusCode.BadRequest);
        await PublishAsync();

        Assert.True(await ChangePlaceAsync(e.Id, CalendarVRChatPublisher.ClearAfterEdit));

        var place = await PlaceAsync(e.Id, CalendarPlaces.VRChat);
        Assert.Equal(CalendarPlaceStates.Waiting, place?.State);
        Assert.Null(place?.Error);
        Assert.Null(place?.ErrorAt);

        Assert.Equal(CalendarPublishOutcome.Written, (await PublishAsync()).Outcome);
        Assert.Equal(2, VRChat.Calendar.Calls);
    }

    [Fact]
    public async Task AnEditLeavesACreateThatWasNotAddedToItsOwnTryAgain()
    {
        var e = await AddEventAsync(TimeSpan.FromDays(2), x => x.PublishToVRChat = true);
        Clock.Advance(Settle);
        VRChat.Calendar.Answer(HttpStatusCode.InternalServerError);
        await PublishAsync();
        Clock.Advance(CalendarVRChatPublisher.LookAfter);
        await PublishAsync();

        Assert.True(CalendarVRChatPublisher.NotAdded((await PlaceAsync(e.Id, CalendarPlaces.VRChat))!));
        Assert.False(await ChangePlaceAsync(e.Id, CalendarVRChatPublisher.ClearAfterEdit));
        Assert.True(CalendarVRChatPublisher.NotAdded((await PlaceAsync(e.Id, CalendarPlaces.VRChat))!));
    }

    [Fact]
    public async Task AFailureOfAVersionVRChatNeverGot_EndsOnceTheEventIsBackToWhatVRChatHas()
    {
        var e = await AddEventAsync(TimeSpan.FromDays(2), x => x.PublishToVRChat = true);
        Clock.Advance(Settle);
        await PublishAsync();

        await EditAsync(e.Id, x => x.Title = "Movie night: Alien");
        Clock.Advance(Settle);
        VRChat.Calendar.Answer(HttpStatusCode.BadRequest);
        Assert.Equal(CalendarPublishOutcome.Failed, (await PublishAsync()).Outcome);

        await EditAsync(e.Id, x => x.Title = "Movie night");
        Assert.Equal(CalendarPublishOutcome.NothingToDo, (await PublishAsync()).Outcome);

        var place = await PlaceAsync(e.Id, CalendarPlaces.VRChat);
        Assert.Equal(CalendarPlaceStates.Published, place?.State);
        Assert.Null(place?.Error);
        Assert.Equal(2, VRChat.Calendar.Calls);
    }

    [Fact]
    public void TryAgainOnOneDate_ClearsItsFailure_AndAPlainDateHasNothingToTryAgain()
    {
        var change = new CalendarDateChange
        {
            VRChatError = "Could not find this date on VRChat's calendar.",
            VRChatErrorAt = Clock.UtcNow,
            VRChatFailedFingerprint = "x",
        };

        Assert.True(CalendarVRChatPublisher.TryDateAgain(change));
        Assert.Null(change.VRChatError);
        Assert.Null(change.VRChatErrorAt);
        Assert.Null(change.VRChatFailedFingerprint);
        Assert.False(CalendarVRChatPublisher.TryDateAgain(change));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────

    /// <summary>The account's permissions as the group was last read, the way the group poll keeps them.</summary>
    private async Task AccountHoldsAsync(params string[] permissions)
    {
        await using var context = Database.NewContext();
        var settings = await context.GetSettingsAsync(Ct);
        settings.VRChatAccountRoleIds = ["grol_modbot"];
        settings.VRChatAccountPermissions = [.. permissions.Order(StringComparer.Ordinal)];
        await context.SaveChangesAsync(Ct);
    }

    private async Task<List<string>?> HeldPermissionsAsync()
    {
        await using var context = Database.NewContext();
        return (await context.Settings.AsNoTracking().SingleAsync(s => s.Id == 1, Ct)).VRChatAccountPermissions;
    }

    /// <summary>A group answer whose <c>myMember</c> holds these permissions.</summary>
    private static string MyMember(params string[] permissions) =>
        JsonSerializer.Serialize(new { id = "grp_test", myMember = new { roleIds = new[] { "grol_modbot" }, permissions } });

    /// <summary>A moderator's Try again, or an edit, on the VRChat place, in its own scope the way a request has one.</summary>
    private async Task<bool> ChangePlaceAsync(Guid id, Func<CalendarEventPlace, DateTimeOffset, bool> change)
    {
        await using var context = Database.NewContext();
        var place = await context.CalendarEventPlaces.SingleAsync(p => p.EventId == id && p.Place == CalendarPlaces.VRChat, Ct);
        var changed = change(place, Clock.UtcNow);
        await context.SaveChangesAsync(Ct);
        return changed;
    }
}
