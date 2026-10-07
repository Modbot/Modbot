using System.Net;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Calendar;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;
using Modbot.VRChat.Calendar;

namespace Modbot.VRChat.Tests.Calendar;

/// <summary>
/// Calendar auto-invite design: once Modbot opens an event's instance it invites the host, the staff
/// and the list, in that order, once each, one VRChat invite every thirty seconds, and remembers who
/// is a friend of its VRChat account.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class CalendarInviterTests(PostgresFixture fixture) : CalendarTestBase(fixture)
{
    private static readonly TimeSpan Turn = CalendarInviter.NoFasterThan;

    /// <summary>An event whose instance opens now, inviting whoever <paramref name="shape"/> says.</summary>
    private async Task<CalendarEvent> OpenedEventAsync(Action<CalendarEvent> shape, TimeSpan? startsIn = null)
    {
        var e = await AddEventAsync(startsIn ?? TimeSpan.FromMinutes(5), x =>
        {
            x.AutoOpen = true;
            x.OpenMinutesBefore = 10;
            shape(x);
        });

        Assert.Equal(1, (await OpenAsync()).Opened);
        return e;
    }

    [Fact]
    public async Task HostThenStaffThenList_AndNobodyIsInvitedTwice()
    {
        var host = await AddStaffAsync("host", "usr_host");
        var ann = await AddStaffAsync("ann", "usr_ann");
        var list = await AddEverybodyListAsync("usr_ann", "usr_bob", "usr_cat");
        VRChat.Invites.FriendsWith("usr_host", "usr_ann", "usr_bob", "usr_cat");

        // The host is listed as staff too, and Ann is staff and on the list.
        var e = await OpenedEventAsync(x =>
        {
            x.InviteHostUserId = host.Id;
            x.InviteStaffUserIds = [ann.Id, host.Id];
            x.InviteListId = list.Id;
        });

        await InviteAsync();

        var rows = await InviteRowsAsync(e.Id);
        Assert.Equal(["usr_host", "usr_ann", "usr_bob", "usr_cat"], rows.Select(r => r.VRChatUserId));
        Assert.Equal(
            [CalendarInviteRoles.Host, CalendarInviteRoles.Staff, CalendarInviteRoles.List, CalendarInviteRoles.List],
            rows.Select(r => r.Role));

        for (var i = 0; i < 4; i++)
        {
            Clock.Advance(Turn);
            await InviteAsync();
        }

        Assert.Equal(["usr_host", "usr_ann", "usr_bob", "usr_cat"], VRChat.Invites.Sent.Select(s => s.UserId));
        Assert.All(VRChat.Invites.Sent, s => Assert.Contains(":", s.Location));
        Assert.Equal(4, (await FactsOfTypeAsync(FactType.PlannedEventInviteSent)).Count);
    }

    [Fact]
    public async Task OnlyListMembersWhoAskedAreInvited_TheRestDidNotAsk()
    {
        var list = await AddEverybodyListAsync(asked: false, "usr_a", "usr_b", "usr_c");
        await AskForInvitesAsync(["usr_b"]);
        VRChat.Invites.FriendsWith("usr_a", "usr_b", "usr_c");

        var e = await OpenedEventAsync(x => x.InviteListId = list.Id);

        for (var i = 0; i < 4; i++)
        {
            await InviteAsync();
            Clock.Advance(Turn);
        }

        // Neither a VRChat invite nor a direct message for the two who did not ask.
        Assert.Equal(["usr_b"], VRChat.Invites.Sent.Select(s => s.UserId));

        var rows = await InviteRowsAsync(e.Id);
        Assert.Equal(CalendarInviteStates.NotAsked, rows.Single(r => r.VRChatUserId == "usr_a").State);
        Assert.Equal(CalendarInviteStates.NotAsked, rows.Single(r => r.VRChatUserId == "usr_c").State);

        var counts = CalendarInviteCounts.From(rows.Select(r => r.State));
        Assert.Equal(1, counts.Invited);
        Assert.Equal(3, counts.Total);
        Assert.Equal(2, counts.NotAsked);
    }

    [Fact]
    public async Task WhileMeIsOff_NoMemberCountsAsAsked_StaffStillDo()
    {
        var ann = await AddStaffAsync("ann", "usr_ann");
        var list = await AddEverybodyListAsync("usr_a", "usr_b");
        VRChat.Invites.FriendsWith("usr_ann", "usr_a", "usr_b");

        // The operator switched /me off after the members asked.
        await using (var context = Database.NewContext())
        {
            var settings = await context.GetSettingsAsync(Ct);
            settings.SwitchCommand("me", false);
            await context.SaveChangesAsync(Ct);
        }

        var e = await OpenedEventAsync(x =>
        {
            x.InviteStaffUserIds = [ann.Id];
            x.InviteListId = list.Id;
        });

        for (var i = 0; i < 4; i++)
        {
            await InviteAsync();
            Clock.Advance(Turn);
        }

        Assert.Equal(["usr_ann"], VRChat.Invites.Sent.Select(s => s.UserId));
        Assert.All(
            (await InviteRowsAsync(e.Id)).Where(r => r.Role == CalendarInviteRoles.List),
            r => Assert.Equal(CalendarInviteStates.NotAsked, r.State));
    }

    [Fact]
    public async Task AChoiceCountsForAVRChatAccountOnlyThroughTheLinkAsItIsNow()
    {
        var list = await AddEverybodyListAsync("usr_a");
        VRChat.Invites.FriendsWith("usr_a");

        // The Discord account that asked is no longer linked to usr_a.
        await using (var context = Database.NewContext())
        {
            var link = await context.DiscordAccountLinks.SingleAsync(l => l.VRChatUserId == "usr_a", Ct);
            link.UnlinkedAt = Clock.UtcNow;
            await context.SaveChangesAsync(Ct);
        }

        var e = await OpenedEventAsync(x => x.InviteListId = list.Id);
        await InviteAsync();

        Assert.Empty(VRChat.Invites.Sent);
        Assert.Equal(CalendarInviteStates.NotAsked, Assert.Single(await InviteRowsAsync(e.Id)).State);
    }

    [Fact]
    public async Task StaffWithEventInvitesOff_AreNotInvited()
    {
        var host = await AddStaffAsync("host", "usr_host", getsEventInvites: false);
        var ann = await AddStaffAsync("ann", "usr_ann");
        VRChat.Invites.FriendsWith("usr_host", "usr_ann");

        var e = await OpenedEventAsync(x =>
        {
            x.InviteHostUserId = host.Id;
            x.InviteStaffUserIds = [ann.Id];
        });

        for (var i = 0; i < 3; i++)
        {
            await InviteAsync();
            Clock.Advance(Turn);
        }

        Assert.Equal(["usr_ann"], VRChat.Invites.Sent.Select(s => s.UserId));
        Assert.Equal(CalendarInviteStates.NotAsked, (await InviteRowsAsync(e.Id))[0].State);
    }

    [Fact]
    public async Task StoppingEventInvitesAfterTheQueueIsWritten_StopsTheInvite()
    {
        var list = await AddEverybodyListAsync("usr_a", "usr_b");
        VRChat.Invites.FriendsWith("usr_a", "usr_b");

        var e = await OpenedEventAsync(x => x.InviteListId = list.Id);
        await InviteAsync();

        // usr_b presses "Stop event invites" before their turn comes.
        await AskForInvitesAsync(["usr_b"], wants: false);

        Clock.Advance(Turn);
        await InviteAsync();

        Assert.Equal(["usr_a"], VRChat.Invites.Sent.Select(s => s.UserId));
        Assert.Equal(CalendarInviteStates.NotAsked, (await InviteRowsAsync(e.Id))[1].State);
    }

    [Fact]
    public async Task OneVRChatInviteEveryThirtySeconds()
    {
        var list = await AddEverybodyListAsync("usr_a", "usr_b", "usr_c");
        VRChat.Invites.FriendsWith("usr_a", "usr_b", "usr_c");

        await OpenedEventAsync(x => x.InviteListId = list.Id);

        await InviteAsync();
        Assert.Single(VRChat.Invites.Sent);

        Clock.Advance(TimeSpan.FromSeconds(10));
        await InviteAsync();
        Assert.Single(VRChat.Invites.Sent);

        Clock.Advance(TimeSpan.FromSeconds(20));
        await InviteAsync();
        Assert.Equal(2, VRChat.Invites.Sent.Count);
    }

    [Fact]
    public async Task ANotFriendIsRemembered_GoesToDiscord_AndIsAskedOnVRChatAgainAfterAWeek()
    {
        var eve = await AddStaffAsync("eve", "usr_eve", discordUserId: "100");

        var first = await OpenedEventAsync(x => x.InviteStaffUserIds = [eve.Id]);
        await InviteAsync();

        var row = Assert.Single(await InviteRowsAsync(first.Id));
        Assert.Equal(CalendarInviteStates.ToMessage, row.State);
        Assert.Equal(CalendarInvites.NotFriends, row.Problem);
        Assert.Single(VRChat.Invites.Sent);

        await using (var context = Database.NewContext())
        {
            var friend = await context.VRChatFriends.AsNoTracking().SingleAsync(f => f.UserId == "usr_eve", Ct);
            Assert.False(friend.IsFriend);
            Assert.Equal(VRChatFriendSources.Refused, friend.LearnedFrom);
        }

        // Two days on: straight to Discord, no VRChat turn spent on her.
        Clock.Advance(TimeSpan.FromDays(2));
        var second = await OpenedEventAsync(x => x.InviteStaffUserIds = [eve.Id]);
        await InviteAsync();

        Assert.Equal(CalendarInviteStates.ToMessage, Assert.Single(await InviteRowsAsync(second.Id)).State);
        Assert.Single(VRChat.Invites.Sent);

        // A week after she was last refused, and she has added the account since: VRChat again.
        Clock.Advance(CalendarInviter.AskAgainAfter);
        VRChat.Invites.FriendsWith("usr_eve");
        var third = await OpenedEventAsync(x => x.InviteStaffUserIds = [eve.Id]);
        await InviteAsync();

        Assert.Equal(CalendarInviteStates.Invited, Assert.Single(await InviteRowsAsync(third.Id)).State);
        Assert.Equal(2, VRChat.Invites.Sent.Count);
    }

    [Fact]
    public async Task NotAFriendAndNoDiscord_IsNoWayToReach()
    {
        var zed = await AddStaffAsync("zed", "usr_zed");

        var e = await OpenedEventAsync(x => x.InviteStaffUserIds = [zed.Id]);
        await InviteAsync();

        var row = Assert.Single(await InviteRowsAsync(e.Id));
        Assert.Equal(CalendarInviteStates.NoWay, row.State);
        Assert.Single(await FactsOfTypeAsync(FactType.PlannedEventInviteFailed));
    }

    [Fact]
    public async Task A429IsNeverTriedAgain_AndTheNextPeopleWaitForTheClass()
    {
        var list = await AddEverybodyListAsync("usr_a", "usr_b");
        VRChat.Invites.FriendsWith("usr_a", "usr_b");
        VRChat.Invites.Status["usr_a"] = HttpStatusCode.TooManyRequests;

        var e = await OpenedEventAsync(x => x.InviteListId = list.Id);
        await InviteAsync();

        for (var i = 0; i < 4; i++)
        {
            Clock.Advance(Turn);
            await InviteAsync();
        }

        // usr_a was asked once, and never again; usr_b is still waiting for the class to open.
        Assert.Equal(["usr_a"], VRChat.Invites.Sent.Select(s => s.UserId));

        // VRChat certainly did not deliver it, and their linked Discord account is the way left.
        var rows = await InviteRowsAsync(e.Id);
        Assert.Equal(CalendarInviteStates.ToMessage, rows[0].State);
        Assert.Equal(CalendarInviteStates.Waiting, rows[1].State);
    }

    [Fact]
    public async Task AfterA500TheInviteMayHaveArrived_SoNoDirectMessageFollows()
    {
        var eve = await AddStaffAsync("eve", "usr_eve", discordUserId: "100");
        VRChat.Invites.Status["usr_eve"] = HttpStatusCode.InternalServerError;

        var e = await OpenedEventAsync(x => x.InviteStaffUserIds = [eve.Id]);
        await InviteAsync();

        for (var i = 0; i < 3; i++)
        {
            Clock.Advance(Turn);
            await InviteAsync();
        }

        var row = Assert.Single(await InviteRowsAsync(e.Id));
        Assert.Equal(CalendarInviteStates.CouldNotReach, row.State);
        Assert.Single(VRChat.Invites.Sent);
    }

    [Fact]
    public async Task ACloudflareBlockTeachesNothing_AndTheInviteWaitsForALaterTurn()
    {
        var eve = await AddStaffAsync("eve", "usr_eve", discordUserId: "100");
        VRChat.Invites.FriendsWith("usr_eve");
        VRChat.Invites.CloudflareBlocks.Add("usr_eve");

        await using (var context = Database.NewContext())
        {
            context.VRChatFriends.Add(new VRChatFriend
            {
                UserId = "usr_eve",
                IsFriend = true,
                CheckedAt = Clock.UtcNow,
                LearnedFrom = VRChatFriendSources.SignIn,
            });
            await context.SaveChangesAsync(Ct);
        }

        var e = await OpenedEventAsync(x => x.InviteStaffUserIds = [eve.Id]);
        await InviteAsync();

        // Still a friend, still waiting, and no direct message on its way.
        var row = Assert.Single(await InviteRowsAsync(e.Id));
        Assert.Equal(CalendarInviteStates.Waiting, row.State);

        await using (var context = Database.NewContext())
        {
            var friend = await context.VRChatFriends.AsNoTracking().SingleAsync(f => f.UserId == "usr_eve", Ct);
            Assert.True(friend.IsFriend);
            Assert.Equal(VRChatFriendSources.SignIn, friend.LearnedFrom);
        }

        // Cloudflare lets it through later: the invite goes out on a later turn.
        VRChat.Invites.CloudflareBlocks.Clear();
        Clock.Advance(Turn);
        await InviteAsync();

        Assert.Equal(CalendarInviteStates.Invited, Assert.Single(await InviteRowsAsync(e.Id)).State);
        Assert.Equal(2, VRChat.Invites.Sent.Count);
    }

    [Fact]
    public async Task PeopleBannedFromTheGroupAreSkipped()
    {
        var list = await AddEverybodyListAsync("usr_a", "usr_banned");
        VRChat.Invites.FriendsWith("usr_a", "usr_banned");

        await using (var context = Database.NewContext())
        {
            context.GroupBans.Add(new GroupBan
            {
                GroupId = GroupId,
                UserId = "usr_banned",
                FirstSeenAt = Clock.UtcNow,
                LastSeenAt = Clock.UtcNow,
            });
            await context.SaveChangesAsync(Ct);
        }

        var e = await OpenedEventAsync(x => x.InviteListId = list.Id);

        for (var i = 0; i < 3; i++)
        {
            await InviteAsync();
            Clock.Advance(Turn);
        }

        Assert.Equal(["usr_a"], VRChat.Invites.Sent.Select(s => s.UserId));

        var banned = (await InviteRowsAsync(e.Id)).Single(r => r.VRChatUserId == "usr_banned");
        Assert.Equal(CalendarInviteStates.Skipped, banned.State);
        Assert.Equal(CalendarInvites.BannedFromGroup, banned.Problem);

        // Skipped people are not in "Invited N of M".
        var counts = CalendarInviteCounts.From((await InviteRowsAsync(e.Id)).Select(r => r.State));
        Assert.Equal(1, counts.Total);
        Assert.Equal(1, counts.Skipped);
    }

    [Fact]
    public async Task SendingStopsWhenTheInstanceCloses()
    {
        var list = await AddEverybodyListAsync("usr_a", "usr_b", "usr_c");
        VRChat.Invites.FriendsWith("usr_a", "usr_b", "usr_c");

        var e = await OpenedEventAsync(x => x.InviteListId = list.Id);
        await InviteAsync();

        await using (var context = Database.NewContext())
        {
            var opening = await context.CalendarOpenings.SingleAsync(o => o.EventId == e.Id, Ct);
            var instance = await context.VRChatInstances.SingleAsync(i => i.Id == opening.InstanceId, Ct);
            instance.ClosedAt = Clock.UtcNow;
            await context.SaveChangesAsync(Ct);
        }

        Clock.Advance(Turn);
        await InviteAsync();

        Assert.Single(VRChat.Invites.Sent);
        var rows = await InviteRowsAsync(e.Id);
        Assert.All(rows.Skip(1), r => Assert.Equal(CalendarInviteStates.Stopped, r.State));
        Assert.All(rows.Skip(1), r => Assert.Equal(CalendarInvites.InstanceClosed, r.Problem));
    }

    [Fact]
    public async Task SendingStopsWhenTheEventEnds()
    {
        var list = await AddEverybodyListAsync("usr_a", "usr_b");
        VRChat.Invites.FriendsWith("usr_a", "usr_b");

        var e = await OpenedEventAsync(x => x.InviteListId = list.Id);
        await InviteAsync();

        Clock.Advance(TimeSpan.FromHours(3));
        await InviteAsync();

        Assert.Single(VRChat.Invites.Sent);
        Assert.Equal(CalendarInviteStates.Stopped, (await InviteRowsAsync(e.Id))[1].State);
    }

    [Fact]
    public async Task ARestartCarriesOn_AndAnInviteCaughtMidSendIsNeverSentAgain()
    {
        var list = await AddEverybodyListAsync("usr_a", "usr_b");
        VRChat.Invites.FriendsWith("usr_a", "usr_b");

        var e = await OpenedEventAsync(x => x.InviteListId = list.Id);
        await InviteAsync();
        Assert.Single(VRChat.Invites.Sent);

        // A crash between the mark and the answer: the second row was marked and never answered.
        await using (var context = Database.NewContext())
        {
            var row = await context.CalendarInvites.SingleAsync(i => i.EventId == e.Id && i.VRChatUserId == "usr_b", Ct);
            row.State = CalendarInviteStates.Sending;
            row.TriedAt = Clock.UtcNow;
            await context.SaveChangesAsync(Ct);
        }

        // Every pass is a fresh context: all a restart leaves is the database.
        for (var i = 0; i < 3; i++)
        {
            Clock.Advance(Turn);
            await InviteAsync();
        }

        Assert.Single(VRChat.Invites.Sent);
        Assert.Equal(2, CalendarInviteCounts.From((await InviteRowsAsync(e.Id)).Select(r => r.State)).VRChat);

        // And the queue itself is written once.
        Assert.Equal(2, (await InviteRowsAsync(e.Id)).Count);
    }

    [Fact]
    public async Task FriendsFromASignInOnlyAdd()
    {
        await using (var context = Database.NewContext())
        {
            context.VRChatFriends.Add(new VRChatFriend
            {
                UserId = "usr_not",
                IsFriend = false,
                CheckedAt = Clock.UtcNow,
                LearnedFrom = VRChatFriendSources.Refused,
            });
            await context.SaveChangesAsync(Ct);
        }

        SignInFriends.Remember(["usr_yes", "usr_yes", ""], Clock.UtcNow);
        await InviteAsync();

        await using (var context = Database.NewContext())
        {
            var friends = await context.VRChatFriends.AsNoTracking().ToDictionaryAsync(f => f.UserId, Ct);
            Assert.True(friends["usr_yes"].IsFriend);
            Assert.Equal(VRChatFriendSources.SignIn, friends["usr_yes"].LearnedFrom);

            // Missing from the sign-in's list is not "not a friend", and not a friend stays so.
            Assert.False(friends["usr_not"].IsFriend);
            Assert.Equal(2, friends.Count);
        }

        // Taken once.
        Assert.Null(SignInFriends.Take());
    }

    [Fact]
    public async Task AnEventThatInvitesNobodyQueuesNothing()
    {
        var e = await OpenedEventAsync(_ => { });

        await InviteAsync();

        Assert.Empty(await InviteRowsAsync(e.Id));
        Assert.Empty(VRChat.Invites.Sent);
    }

    [Fact]
    public async Task TheVRChatPostForTheFirstPersonGoesOnce_ToTheGroup_WithoutANotification()
    {
        var e = await OpenedEventAsync(x => x.AnnounceFirstJoinInVRChat = true);

        // Nobody there yet: nothing posted.
        Assert.Equal(0, await PostFirstJoinAsync());

        await using (var context = Database.NewContext())
        {
            var opening = await context.CalendarOpenings.SingleAsync(o => o.EventId == e.Id, Ct);
            var instance = await context.VRChatInstances.SingleAsync(i => i.Id == opening.InstanceId, Ct);
            instance.LastUserCount = 1;
            await context.SaveChangesAsync(Ct);
        }

        Assert.Equal(1, await PostFirstJoinAsync());
        Assert.Equal(0, await PostFirstJoinAsync());

        var post = Assert.Single(VRChat.Groups.Posts);
        Assert.False(post.SendNotification);
        Assert.Equal(global::VRChat.API.Model.GroupPostVisibility.Group, post.Visibility);
        Assert.Equal(e.Title, post.Title);
        Assert.Contains("vrchat.com/home/launch", post.Text);
    }
}
