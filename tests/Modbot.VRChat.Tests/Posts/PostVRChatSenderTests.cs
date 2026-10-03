using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Core.Data.Entities;
using Modbot.Core.Posts;
using Modbot.TestSupport;
using Modbot.VRChat.Posts;
using Modbot.VRChat.Tests.Fakes;
using Modbot.VRChat.Tests.Sync;
using VRChat.API.Model;

namespace Modbot.VRChat.Tests.Posts;

/// <summary>
/// Posts sent to the VRChat group from the Marketing tab (posts design §3.6): sent once at their
/// time; after a 500 or no answer, looked for in the group's posts and adopted, never sent again by
/// themselves; a 429 or a call never sent waits for the gate; a refusal names the permission; the
/// audit log adopts too. A real database, gate and limiter, and a scripted VRChat behind them.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class PostVRChatSenderTests(PostgresFixture fixture) : SyncTestBase(fixture)
{
    /// <summary>Modbot's own VRChat account, the author of every post it makes.</summary>
    private const string Account = "usr_fake";

    /// <summary>Just past the minutes after an attempt the look must wait before "not found" is an answer.</summary>
    private static readonly TimeSpan PastTheWindow = PostVRChatSender.FirstLookAfter + TimeSpan.FromSeconds(2);

    private async Task SetUpAsync(bool uploadsOn = false)
    {
        await using var context = Database.NewContext();
        var settings = await context.GetSettingsAsync(Ct);
        settings.VRChatUsername = "modbot";
        settings.VRChatPasswordEncrypted = "sealed";
        settings.VRChatAccountUserId = Account;
        settings.VRChatPictureUploads = uploadsOn;
        await context.SaveChangesAsync(Ct);
    }

    private async Task<PostVRChatPass> RunAsync(IVRChatGate? gate = null)
    {
        await using var context = Database.NewContext();
        var sender = new PostVRChatSender(
            gate ?? Gate,
            context,
            Clock,
            new FactWriter(context, Clock),
            new EventPartitionMaintainer(context, Clock),
            new PostClaim(context, Clock));

        return await sender.RunOnceAsync(Ct);
    }

    /// <summary>A post due now, ticked for VRChat only.</summary>
    private async Task<(Guid PostId, Guid DestinationId)> AddPostAsync(
        string? title = "Movie night",
        string text = "Friday at eight. Bring snacks.",
        VRChatPostOptions? options = null,
        Guid? pictureId = null)
    {
        await using var context = Database.NewContext();

        var post = new Post
        {
            Id = Guid.CreateVersion7(),
            Title = title,
            Text = text,
            PictureId = pictureId,
            Status = PostStatuses.Scheduled,
            SendAt = Clock.UtcNow,
            TimeZone = "UTC",
            CreatedAt = Clock.UtcNow,
            UpdatedAt = Clock.UtcNow,
        };

        var destination = new PostDestination
        {
            Id = Guid.CreateVersion7(),
            PostId = post.Id,
            Network = PostNetworks.VRChat,
            Target = GroupId,
            Options = PostTexts.WriteVRChatOptions(options ?? new VRChatPostOptions()),
            State = PostDestinationStates.Waiting,
            UpdatedAt = Clock.UtcNow,
        };

        post.Destinations.Add(destination);
        context.Posts.Add(post);
        await context.SaveChangesAsync(Ct);

        return (post.Id, destination.Id);
    }

    private async Task<PostDestination> DestinationAsync(Guid id)
    {
        await using var context = Database.NewContext();
        return await context.PostDestinations.AsNoTracking().SingleAsync(d => d.Id == id, Ct);
    }

    /// <summary>A post as VRChat lists it.</summary>
    private static GroupPost Listed(string id, string title, string text, DateTimeOffset createdAt, string author = Account) =>
        new(
            authorId: author,
            createdAt: createdAt.UtcDateTime,
            editorId: author,
            groupId: GroupId,
            id: id,
            imageId: null!,
            imageUrl: null!,
            roleIds: [],
            text: text,
            title: title,
            updatedAt: createdAt.UtcDateTime,
            visibility: GroupPostVisibility.Group);

    /// <summary>VRChat answers every post with this status, and makes it all the same when <paramref name="lands"/>.</summary>
    private void VRChatAnswers(HttpStatusCode status, bool lands, string id = "not_made")
    {
        VRChat.Groups.PostStatus = status;
        VRChat.Groups.PostLands = lands;

        // VRChat rewrites some of what it is sent: an en dash dropped, "." turned into a look-alike dot.
        VRChat.Groups.PostAnswer = request => Listed(
            id,
            request.Title.Replace(" – ", " ", StringComparison.Ordinal),
            request.Text.Replace(".", "․", StringComparison.Ordinal),
            Clock.UtcNow);
    }

    [Fact]
    public async Task APostGoesOnceAtItsTime()
    {
        await SetUpAsync();
        VRChatAnswers(HttpStatusCode.OK, lands: true, id: "not_1");
        var (_, destinationId) = await AddPostAsync();

        var pass = await RunAsync();

        Assert.Equal(1, pass.Sent);
        var sent = Assert.Single(VRChat.Groups.Posts);
        Assert.Equal("Movie night", sent.Title);
        Assert.Equal(GroupPostVisibility.Group, sent.Visibility);
        Assert.False(sent.SendNotification);

        var row = await DestinationAsync(destinationId);
        Assert.Equal(PostDestinationStates.Posted, row.State);
        Assert.Equal("not_1", row.ExternalId);
        Assert.Equal(PostTexts.VRChatLink(GroupId), row.Link);

        // Nothing goes twice.
        Clock.Advance(TimeSpan.FromMinutes(5));
        await RunAsync();
        Assert.Single(VRChat.Groups.Posts);
    }

    [Fact]
    public async Task A500IsLookedForAndAdoptedNeverSentAgain()
    {
        await SetUpAsync();
        VRChatAnswers(HttpStatusCode.InternalServerError, lands: true, id: "not_made");
        var (_, destinationId) = await AddPostAsync(title: "Movie night – Friday");

        await RunAsync();

        var checking = await DestinationAsync(destinationId);
        Assert.Equal(PostDestinationStates.Checking, checking.State);
        Assert.True(checking.MayBeSent);

        // Too early: nothing read, and nothing sent.
        await RunAsync();
        Assert.Empty(VRChat.Groups.PostsQueries);

        // Another post by somebody else with the same words, and an older one of Modbot's own.
        VRChat.Groups.GroupPostList.Add(Listed("not_other", "Movie night – Friday", "Friday at eight. Bring snacks.", Clock.UtcNow, author: "usr_someone"));
        VRChat.Groups.GroupPostList.Add(Listed("not_old", "Movie night – Friday", "Friday at eight. Bring snacks.", Clock.UtcNow - TimeSpan.FromMinutes(10)));

        Clock.Advance(PastTheWindow);
        await RunAsync();

        var adopted = await DestinationAsync(destinationId);
        Assert.Equal(PostDestinationStates.Posted, adopted.State);
        Assert.Equal("not_made", adopted.ExternalId);
        Assert.False(adopted.MayBeSent);
        Assert.Single(VRChat.Groups.Posts);
    }

    [Fact]
    public async Task NoMatchAfterTheWholeReadIsFailedAndTryAgainLooksFirst()
    {
        await SetUpAsync();
        VRChatAnswers(HttpStatusCode.InternalServerError, lands: false);
        var (postId, destinationId) = await AddPostAsync();

        await RunAsync();
        Clock.Advance(PastTheWindow);
        await RunAsync();

        var failed = await DestinationAsync(destinationId);
        Assert.Equal(PostDestinationStates.Failed, failed.State);
        Assert.Equal(PostVRChatSender.NotTaken, failed.Error);
        Assert.True(failed.MayBeSent);
        Assert.Single(VRChat.Groups.Posts);

        // Try again looks first, and sends only once it is not there.
        await using (var context = Database.NewContext())
        {
            var post = await context.Posts.Include(p => p.Destinations).SingleAsync(p => p.Id == postId, Ct);
            PostChanges.TryAgain(post, post.Destinations.Single(), Clock.UtcNow);
            await context.SaveChangesAsync(Ct);
        }

        var reads = VRChat.Groups.PostsQueries.Count;
        VRChatAnswers(HttpStatusCode.OK, lands: true, id: "not_2");

        // One pass: the look reads the group first, finds nothing, and only then is it sent.
        await RunAsync();
        Assert.True(VRChat.Groups.PostsQueries.Count > reads);
        Assert.Equal(2, VRChat.Groups.Posts.Count);

        var posted = await DestinationAsync(destinationId);
        Assert.Equal(PostDestinationStates.Posted, posted.State);
        Assert.Equal("not_2", posted.ExternalId);
    }

    [Fact]
    public async Task TryAgainOnAPostThatIsThereAdoptsItAndSendsNothing()
    {
        await SetUpAsync();
        VRChatAnswers(HttpStatusCode.InternalServerError, lands: false);
        var (postId, destinationId) = await AddPostAsync();

        await RunAsync();
        Clock.Advance(PastTheWindow);
        await RunAsync();
        Assert.Equal(PostDestinationStates.Failed, (await DestinationAsync(destinationId)).State);

        // VRChat shows it late, after all.
        var attempt = (await DestinationAsync(destinationId)).SentAt!.Value;
        VRChat.Groups.GroupPostList.Add(Listed("not_late", "Movie night", "Friday at eight. Bring snacks.", attempt + TimeSpan.FromSeconds(30)));

        await using (var context = Database.NewContext())
        {
            var post = await context.Posts.Include(p => p.Destinations).SingleAsync(p => p.Id == postId, Ct);
            PostChanges.TryAgain(post, post.Destinations.Single(), Clock.UtcNow);
            await context.SaveChangesAsync(Ct);
        }

        await RunAsync();

        var adopted = await DestinationAsync(destinationId);
        Assert.Equal(PostDestinationStates.Posted, adopted.State);
        Assert.Equal("not_late", adopted.ExternalId);
        Assert.Single(VRChat.Groups.Posts);
    }

    [Fact]
    public async Task ALookThatCannotCoverTheWindowNeverTurnsIntoASend()
    {
        await SetUpAsync();
        VRChatAnswers(HttpStatusCode.InternalServerError, lands: false);
        var (_, destinationId) = await AddPostAsync();

        await RunAsync();

        // A group so busy every page is full of posts newer than the attempt.
        for (var i = 0; i < PostVRChatSender.LookSize * PostVRChatSender.LookPages; i++)
            VRChat.Groups.GroupPostList.Add(Listed($"not_busy_{i}", $"Other {i}", "Other", Clock.UtcNow + TimeSpan.FromSeconds(1)));

        Clock.Advance(PastTheWindow);
        await RunAsync();

        var still = await DestinationAsync(destinationId);
        Assert.Equal(PostDestinationStates.Checking, still.State);
        Assert.Equal(PostVRChatSender.TooBusy, still.Error);

        // A read VRChat refuses is no answer either.
        VRChat.Groups.PostsReadStatus = HttpStatusCode.BadGateway;
        Clock.Advance(PostRules.LookAgainAfter);
        await RunAsync();
        Assert.Equal(PostDestinationStates.Checking, (await DestinationAsync(destinationId)).State);

        // An hour after the looking began it is Failed, still as one VRChat may have.
        Clock.Advance(PostRules.StopLookingAfter);
        await RunAsync();

        var given = await DestinationAsync(destinationId);
        Assert.Equal(PostDestinationStates.Failed, given.State);
        Assert.Equal(PostVRChatSender.CouldNotCheck, given.Error);
        Assert.True(given.MayBeSent);
        Assert.Single(VRChat.Groups.Posts);
    }

    [Fact]
    public async Task A429WaitsAndIsNotSentAgainBeforeTheGateAllows()
    {
        await SetUpAsync();
        VRChatAnswers(HttpStatusCode.TooManyRequests, lands: false);
        var (_, destinationId) = await AddPostAsync();

        await RunAsync();

        var waiting = await DestinationAsync(destinationId);
        Assert.Equal(PostDestinationStates.Waiting, waiting.State);
        Assert.False(waiting.MayBeSent);
        Assert.Single(VRChat.Groups.Posts);

        // The next attempt comes after its own wait, and the gate's cold stop sends nothing: the call
        // was never sent, and the post still waits.
        Clock.Advance(PostRules.NotSentRetryAfter);
        await RunAsync();

        Assert.Single(VRChat.Groups.Posts);
        Assert.Equal(PostDestinationStates.Waiting, (await DestinationAsync(destinationId)).State);
    }

    /// <summary>
    /// A sign-in VRChat refuses means the post was never sent: it waits, with the reason, instead of
    /// sitting in Checking for an hour as one VRChat may have. Once it is an hour late it is Failed,
    /// and the words say why it never went.
    /// </summary>
    [Fact]
    public async Task ARefusedSignInWaitsAndTheLateFailureSaysWhy()
    {
        await SetUpAsync();
        var (_, destinationId) = await AddPostAsync();

        var refusing = new FakeVRChat().RespondsWith(FakeVRChat.Status(HttpStatusCode.Unauthorized));
        using var gate = new VRChatGate(
            new FakeClientFactory(refusing.Client),
            new FakeConnectionStore(),
            Limiter.Limiter,
            Clock,
            new FakeMonotonicClock());

        await RunAsync(gate);

        var waiting = await DestinationAsync(destinationId);
        Assert.Equal(PostDestinationStates.Waiting, waiting.State);
        Assert.False(waiting.MayBeSent);
        Assert.Contains("rejected the credentials", waiting.Error, StringComparison.Ordinal);
        Assert.Empty(refusing.Groups.Posts);

        // Still refused an hour on: Failed, with the reason after "Not sent on time.", and nothing
        // that Try again would have to look for first.
        Clock.Advance(PostRules.LateLimit + TimeSpan.FromMinutes(1));
        await RunAsync(gate);

        var failed = await DestinationAsync(destinationId);
        Assert.Equal(PostDestinationStates.Failed, failed.State);
        Assert.False(failed.MayBeSent);
        Assert.StartsWith(PostRules.NotSentOnTime + " ", failed.Error, StringComparison.Ordinal);
        Assert.Contains("rejected the credentials", failed.Error, StringComparison.Ordinal);
        Assert.Empty(refusing.Groups.Posts);
    }

    /// <summary>
    /// What the gate's answer says about whether VRChat may have made the post. Only answers that
    /// prove the call never reached VRChat, or made nothing there, count as not sent; a timeout, a
    /// lost connection and a Cloudflare page may have reached it, and are looked for.
    /// </summary>
    [Theory]
    [InlineData(0, VRChatFailureKind.CredentialsRejected, true)]
    [InlineData(401, VRChatFailureKind.CredentialsRejected, true)]
    [InlineData(200, VRChatFailureKind.CredentialsRejected, true)]
    [InlineData(0, VRChatFailureKind.TwoFactorMissing, true)]
    [InlineData(0, VRChatFailureKind.SignInWaiting, true)]
    [InlineData(0, VRChatFailureKind.NotConfigured, true)]
    [InlineData(0, VRChatFailureKind.RateLimited, true)]
    [InlineData(429, VRChatFailureKind.RateLimited, true)]
    [InlineData(0, VRChatFailureKind.NameResolution, true)]
    [InlineData(0, VRChatFailureKind.Timeout, false)]
    [InlineData(0, VRChatFailureKind.Network, false)]
    [InlineData(0, VRChatFailureKind.Other, false)]
    [InlineData(503, VRChatFailureKind.WafBlocked, false)]
    [InlineData(500, VRChatFailureKind.Other, false)]
    public void OnlyACallThatNeverReachedVRChatIsNotSent(int status, VRChatFailureKind kind, bool notSent)
    {
        var result = VRChatResult<GroupPost>.Failure(status, "no", kind: kind);

        Assert.Equal(notSent, PostVRChatSender.NothingMade(result));

        // Never both: a call that made nothing is never looked for.
        if (notSent)
            Assert.False(PostVRChatSender.Unclear(result));
    }

    [Theory]
    [InlineData(0, VRChatFailureKind.Timeout)]
    [InlineData(0, VRChatFailureKind.Network)]
    [InlineData(0, VRChatFailureKind.Other)]
    [InlineData(500, VRChatFailureKind.Other)]
    [InlineData(408, VRChatFailureKind.Other)]
    [InlineData(503, VRChatFailureKind.WafBlocked)]
    public void ACallThatMayHaveReachedVRChatIsLookedFor(int status, VRChatFailureKind kind) =>
        Assert.True(PostVRChatSender.Unclear(VRChatResult<GroupPost>.Failure(status, "no", kind: kind)));

    /// <summary>
    /// The late words carry the reason only when it came from this wait: words from before a
    /// person sent it on its way again are about an attempt that no longer matters.
    /// </summary>
    [Fact]
    public void TheLateWordsCarryOnlyThisWaitsReason()
    {
        var now = new DateTimeOffset(2026, 10, 3, 20, 0, 0, TimeSpan.Zero);

        var fresh = new PostDestination { Error = "Signed out.", ErrorAt = now, UpdatedAt = now - TimeSpan.FromHours(1) };
        var stale = new PostDestination { Error = "Old words.", ErrorAt = now - TimeSpan.FromHours(2), UpdatedAt = now - TimeSpan.FromHours(1) };
        var none = new PostDestination { UpdatedAt = now };

        Assert.Equal($"{PostRules.NotSentOnTime} Signed out.", PostVRChatSender.LateReason(fresh));
        Assert.Equal(PostRules.NotSentOnTime, PostVRChatSender.LateReason(stale));
        Assert.Equal(PostRules.NotSentOnTime, PostVRChatSender.LateReason(none));
    }

    /// <summary>
    /// A post of Modbot's own with the same words but no time from VRChat may be this one or an
    /// older one. It is never adopted, and never "not found": the post stays Checking with the
    /// reason, and after the hour of looking it is Failed as one VRChat may have, so Try again looks
    /// again rather than sending a second copy.
    /// </summary>
    [Fact]
    public async Task AMatchWithNoTimeIsNeverNotFound()
    {
        await SetUpAsync();
        VRChatAnswers(HttpStatusCode.InternalServerError, lands: false);
        var (postId, destinationId) = await AddPostAsync();

        await RunAsync();

        VRChat.Groups.GroupPostList.Add(Listed("not_undated", "Movie night", "Friday at eight. Bring snacks.", default));

        Clock.Advance(PastTheWindow);
        await RunAsync();

        var checking = await DestinationAsync(destinationId);
        Assert.Equal(PostDestinationStates.Checking, checking.State);
        Assert.Equal(PostVRChatSender.NoTimeOnMatch, checking.Error);
        Assert.True(checking.MayBeSent);
        Assert.Null(checking.ExternalId);

        Clock.Advance(PostRules.StopLookingAfter);
        await RunAsync();

        var failed = await DestinationAsync(destinationId);
        Assert.Equal(PostDestinationStates.Failed, failed.State);
        Assert.Equal(PostVRChatSender.NoTimeOnMatch, failed.Error);
        Assert.True(failed.MayBeSent);

        // Try again looks first, finds the same undated post, and sends nothing.
        await using (var context = Database.NewContext())
        {
            var post = await context.Posts.Include(p => p.Destinations).SingleAsync(p => p.Id == postId, Ct);
            PostChanges.TryAgain(post, post.Destinations.Single(), Clock.UtcNow);
            await context.SaveChangesAsync(Ct);
        }

        VRChatAnswers(HttpStatusCode.OK, lands: true, id: "not_second");
        await RunAsync();
        Clock.Advance(PostRules.NotSentRetryAfter);
        await RunAsync();

        Assert.Equal(PostDestinationStates.Checking, (await DestinationAsync(destinationId)).State);
        Assert.Single(VRChat.Groups.Posts);
    }

    [Fact]
    public async Task A403NamesTheMissingPermission()
    {
        await SetUpAsync();
        VRChatAnswers(HttpStatusCode.Forbidden, lands: false);
        var (_, destinationId) = await AddPostAsync();

        await RunAsync();

        var failed = await DestinationAsync(destinationId);
        Assert.Equal(PostDestinationStates.Failed, failed.State);
        Assert.Equal(VRChatGroupPermissions.ManageAnnouncement, failed.MissingPermission);
        Assert.False(failed.MayBeSent);
        Assert.Equal("no", failed.Error);
    }

    [Fact]
    public async Task APostWithNoTitleIsNotSent()
    {
        await SetUpAsync();
        VRChatAnswers(HttpStatusCode.OK, lands: true);
        var (_, destinationId) = await AddPostAsync(title: null);

        await RunAsync();

        var failed = await DestinationAsync(destinationId);
        Assert.Equal(PostDestinationStates.Failed, failed.State);
        Assert.Equal(PostVRChatSender.NeedsTitle, failed.Error);
        Assert.Empty(VRChat.Groups.Posts);
    }

    [Fact]
    public async Task TheVRChatSwitchHoldsPosts()
    {
        await SetUpAsync();
        VRChatAnswers(HttpStatusCode.OK, lands: true);
        await AddPostAsync();

        await using (var context = Database.NewContext())
        {
            (await context.GetSettingsAsync(Ct)).VRChatPostsOn = false;
            await context.SaveChangesAsync(Ct);
        }

        await RunAsync();
        Assert.Empty(VRChat.Groups.Posts);
    }

    [Fact]
    public async Task ThePictureGoesOnlyWhileUploadsAreOn()
    {
        var picture = Guid.CreateVersion7();

        await using (var context = Database.NewContext())
        {
            context.CalendarCoverPictures.Add(new CalendarCoverPicture
            {
                Id = picture,
                Bytes = [0x89, 0x50, 0x4E, 0x47],
                ContentType = "image/png",
                CreatedAt = Clock.UtcNow,
            });
            await context.SaveChangesAsync(Ct);
        }

        await SetUpAsync(uploadsOn: false);
        VRChatAnswers(HttpStatusCode.OK, lands: true, id: "not_text");
        var (_, textOnly) = await AddPostAsync(options: new VRChatPostOptions(ImageId: "file_1", PictureId: picture), pictureId: picture);

        await RunAsync();

        Assert.Null(VRChat.Groups.Posts[^1].ImageId);
        Assert.Null(PostTexts.VRChatOptionsOf(await DestinationAsync(textOnly)).ImageId);

        await SetUpAsync(uploadsOn: true);
        VRChatAnswers(HttpStatusCode.OK, lands: true, id: "not_picture");
        var (_, withPicture) = await AddPostAsync(options: new VRChatPostOptions(ImageId: "file_1", PictureId: picture), pictureId: picture);

        Clock.Advance(TimeSpan.FromSeconds(15));
        await RunAsync();

        Assert.Equal("file_1", VRChat.Groups.Posts[^1].ImageId);
        Assert.Equal("file_1", PostTexts.VRChatOptionsOf(await DestinationAsync(withPicture)).ImageId);
    }

    [Fact]
    public async Task TheAuditLogsPostCreateAdopts()
    {
        await SetUpAsync();
        VRChatAnswers(HttpStatusCode.InternalServerError, lands: false);
        var (_, destinationId) = await AddPostAsync();

        await RunAsync();
        Assert.Equal(PostDestinationStates.Checking, (await DestinationAsync(destinationId)).State);

        // The audit-log sync writes VRChat's entry, with the post's words as VRChat stored them.
        await using (var context = Database.NewContext())
        {
            await new FactWriter(context, Clock).WriteAsync(
                new FactRecord
                {
                    Type = FactType.GroupPostCreated,
                    OccurredAt = Clock.UtcNow,
                    SubjectPlatform = FactPlatform.VRChat,
                    SubjectId = "not_audit",
                    ActorPlatform = FactPlatform.VRChat,
                    ActorId = Account,
                    Source = FactSource.AuditLog,
                    Data = new JsonObject
                    {
                        ["groupId"] = GroupId,
                        ["title"] = "Movie night",
                        ["text"] = "Friday at eight․ Bring snacks․",
                        ["authorId"] = Account,
                    },
                },
                Ct);
        }

        // Before the look is due: the audit log alone settles it, with no read of VRChat.
        Clock.Advance(TimeSpan.FromSeconds(15));
        await RunAsync();

        var adopted = await DestinationAsync(destinationId);
        Assert.Equal(PostDestinationStates.Posted, adopted.State);
        Assert.Equal("not_audit", adopted.ExternalId);
        Assert.Empty(VRChat.Groups.PostsQueries);
        Assert.Single(VRChat.Groups.Posts);
    }

    [Fact]
    public void AnEditSendsThePictureAgainAndNotifiesNobody()
    {
        var options = new VRChatPostOptions(VRChatPostVisibilities.Group, ["grol_a"], Notify: true, ImageId: "file_1");

        var edit = PostVRChatSender.EditRequest("Cinema", "New words", options);

        Assert.Equal("file_1", edit.ImageId);
        Assert.False(edit.SendNotification);
        Assert.Equal(["grol_a"], edit.RoleIds);
        Assert.Equal(GroupPostVisibility.Group, edit.Visibility);
    }

    [Fact]
    public void APostForEveryoneSendsNoRoles()
    {
        var request = PostVRChatSender.Request("Title", "Text", new VRChatPostOptions(VRChatPostVisibilities.Everyone, ["grol_a"]), notify: true);

        Assert.Equal(GroupPostVisibility.Public, request.Visibility);
        Assert.True(request.RoleIds is null || request.RoleIds.Count == 0);
        Assert.True(request.SendNotification);
    }
}
