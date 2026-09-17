using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Analytics.Facts;
using Modbot.Analytics.Messages;
using Modbot.Api.Features.Audit;
using Modbot.Api.Features.DiscordLink;
using Modbot.Api.Features.DiscordMembers;
using Modbot.Api.Tests.Features.Audit;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.DiscordMembers;

/// <summary>
/// The Discord server's member list: current and past members of the server in settings, with
/// search and a role filter, for whoever may see the group's members.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class DiscordMemberTests
{
    private const string Guild = "424242";

    private readonly PostgresFixture _db;

    public DiscordMemberTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<ReadSurfaceTestHost> StartAsync(PostgresFixture db)
    {
        var host = await ReadSurfaceTestHost.StartAsync(db);
        await host.ResetAsync(Ct);

        using var scope = host.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        var settings = await context.GetSettingsAsync(Ct);
        settings.DiscordGuildId = Guild;

        var at = host.Clock.UtcNow;

        context.DiscordServers.Add(new DiscordServer
        {
            GuildId = Guild, Name = "The Black Cat", RefreshedAt = at, UpdatedAt = at, MembersListedAt = at.AddHours(-1),
        });

        context.DiscordRoles.AddRange(
            new DiscordRole { RoleId = "201", GuildId = Guild, Name = "Member", Color = 0x3498DB, Position = 1, FirstSeenAt = at, UpdatedAt = at },
            new DiscordRole { RoleId = "202", GuildId = Guild, Name = "Staff", Position = 2, FirstSeenAt = at, UpdatedAt = at });

        context.DiscordMembers.AddRange(
            Member("1", "ada", "Ada", at.AddDays(-30), roles: """["201","202"]""", nickname: "Ada the Brave", at: at),
            Member("2", "bo", "Bo", at.AddDays(-2), roles: """["201"]""", at: at),
            Member("3", "cy_100%", "Cy", at.AddDays(-60), left: at.AddDays(-1), at: at),
            new DiscordMember { GuildId = "777", UserId = "9", Username = "elsewhere", DisplayName = "Elsewhere", FirstSeenAt = at, UpdatedAt = at });

        await context.SaveChangesAsync(Ct);
        return host;
    }

    private static DiscordMember Member(
        string id, string username, string display, DateTimeOffset joined, DateTimeOffset at,
        string roles = "[]", string? nickname = null, DateTimeOffset? left = null) => new()
    {
        GuildId = Guild,
        UserId = id,
        Username = username,
        DisplayName = nickname ?? display,
        GlobalName = display,
        Nickname = nickname,
        AvatarUrl = $"https://cdn.discordapp.com/avatars/{id}/a.png",
        JoinedAt = joined,
        LeftAt = left,
        Roles = roles,
        FirstSeenAt = joined,
        UpdatedAt = at,
    };

    [Fact]
    public async Task TheList_ShowsMembersInTheServer_NewestFirst_WithTheirRolesNamed()
    {
        await using var host = await StartAsync(_db);
        var cookie = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);

        var body = await host.GetJsonAsync<DiscordMemberListResponse>("/api/discord/members", cookie, Ct);

        Assert.Equal(["2", "1"], body.Members.Select(m => m.UserId));
        Assert.Equal(2, body.Total);
        Assert.Equal(2, body.Coverage.InServer);
        Assert.Equal(Guild, body.Coverage.GuildId);
        Assert.Equal(host.Clock.UtcNow.AddHours(-1), body.Coverage.ListedAt);

        var ada = body.Members[1];
        Assert.Equal("Ada the Brave", ada.DisplayName);
        Assert.Equal("Ada", ada.GlobalName);
        Assert.Equal(["Staff", "Member"], ada.Roles.Select(r => r.Name));
        Assert.Equal(0x3498DB, ada.Roles[1].Color);

        Assert.Equal(["Staff", "Member"], body.Roles.Select(r => r.Name));
    }

    [Fact]
    public async Task Filters_ForLeft_All_Role_AndSearch()
    {
        await using var host = await StartAsync(_db);
        var cookie = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);

        var left = await host.GetJsonAsync<DiscordMemberListResponse>("/api/discord/members?state=left", cookie, Ct);
        Assert.Equal(["3"], left.Members.Select(m => m.UserId));
        Assert.NotNull(left.Members[0].LeftAt);

        var all = await host.GetJsonAsync<DiscordMemberListResponse>("/api/discord/members?state=all", cookie, Ct);
        Assert.Equal(3, all.Total);

        var staff = await host.GetJsonAsync<DiscordMemberListResponse>("/api/discord/members?role=202", cookie, Ct);
        Assert.Equal(["1"], staff.Members.Select(m => m.UserId));

        var brave = await host.GetJsonAsync<DiscordMemberListResponse>("/api/discord/members?search=BRAVE", cookie, Ct);
        Assert.Equal(["1"], brave.Members.Select(m => m.UserId));

        // A % typed in a search is a percent sign, not "anything".
        var percent = await host.GetJsonAsync<DiscordMemberListResponse>("/api/discord/members?state=all&search=%25", cookie, Ct);
        Assert.Equal(["3"], percent.Members.Select(m => m.UserId));

        var paged = await host.GetJsonAsync<DiscordMemberListResponse>("/api/discord/members?pageSize=1&page=2", cookie, Ct);
        Assert.Equal(["1"], paged.Members.Select(m => m.UserId));
        Assert.Equal(2, paged.Total);

        Assert.Equal(HttpStatusCode.BadRequest, (await host.GetAsync("/api/discord/members?state=gone", cookie, Ct)).StatusCode);
    }

    [Fact]
    public async Task SeveralRoles_NotRole_NoRole_AndTheCounts()
    {
        await using var host = await StartAsync(_db);
        var cookie = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);

        var either = await host.GetJsonAsync<DiscordMemberListResponse>("/api/discord/members?role=201&role=202", cookie, Ct);
        Assert.Equal(["2", "1"], either.Members.Select(m => m.UserId));

        var notStaff = await host.GetJsonAsync<DiscordMemberListResponse>("/api/discord/members?notRole=202", cookie, Ct);
        Assert.Equal(["2"], notStaff.Members.Select(m => m.UserId));

        var roleless = await host.GetJsonAsync<DiscordMemberListResponse>("/api/discord/members?noRole=true&state=all", cookie, Ct);
        Assert.Equal(["3"], roleless.Members.Select(m => m.UserId));

        // Two people in the server hold Member, one holds Staff; the person who left counts for nothing.
        Assert.Equal(2, either.Roles.Single(r => r.Id == "201").Members);
        Assert.Equal(1, either.Roles.Single(r => r.Id == "202").Members);
    }

    [Fact]
    public async Task Bots_Timeouts_Boosts_JoinedStretch_AndSort()
    {
        await using var host = await StartAsync(_db);
        var at = host.Clock.UtcNow;

        using (var scope = host.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<ModbotContext>();
            context.DiscordMembers.AddRange(
                new DiscordMember
                {
                    GuildId = Guild, UserId = "4", Username = "beep", DisplayName = "Beep", IsBot = true,
                    JoinedAt = at.AddDays(-100), FirstSeenAt = at, UpdatedAt = at,
                },
                new DiscordMember
                {
                    GuildId = Guild, UserId = "5", Username = "dee", DisplayName = "Dee", TimedOutUntil = at.AddHours(1),
                    BoostingSince = at.AddDays(-3), JoinedAt = at.AddDays(-1), FirstSeenAt = at, UpdatedAt = at,
                },
                new DiscordMember
                {
                    GuildId = Guild, UserId = "6", Username = "eve", DisplayName = "Eve", TimedOutUntil = at.AddHours(-1),
                    JoinedAt = at.AddDays(-10), FirstSeenAt = at, UpdatedAt = at,
                });
            await context.SaveChangesAsync(Ct);
        }

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);

        var bots = await host.GetJsonAsync<DiscordMemberListResponse>("/api/discord/members?bot=true", cookie, Ct);
        Assert.Equal(["4"], bots.Members.Select(m => m.UserId));

        var people = await host.GetJsonAsync<DiscordMemberListResponse>("/api/discord/members?bot=false", cookie, Ct);
        Assert.DoesNotContain("4", people.Members.Select(m => m.UserId));

        // A timeout that has already ended is not a timeout.
        var timedOut = await host.GetJsonAsync<DiscordMemberListResponse>("/api/discord/members?timedOut=true", cookie, Ct);
        Assert.Equal(["5"], timedOut.Members.Select(m => m.UserId));

        var boosting = await host.GetJsonAsync<DiscordMemberListResponse>("/api/discord/members?boosting=true", cookie, Ct);
        Assert.Equal(["5"], boosting.Members.Select(m => m.UserId));

        var lastWeek = await host.GetJsonAsync<DiscordMemberListResponse>(
            $"/api/discord/members?joinedFrom={Uri.EscapeDataString(at.AddDays(-7).ToString("o"))}", cookie, Ct);
        Assert.Equal(["5", "2"], lastWeek.Members.Select(m => m.UserId));

        var byName = await host.GetJsonAsync<DiscordMemberListResponse>("/api/discord/members?sort=name", cookie, Ct);
        Assert.Equal(["Ada the Brave", "Beep", "Bo", "Dee", "Eve"], byName.Members.Select(m => m.DisplayName));

        var oldest = await host.GetJsonAsync<DiscordMemberListResponse>("/api/discord/members?sort=oldest&pageSize=1", cookie, Ct);
        Assert.Equal(["4"], oldest.Members.Select(m => m.UserId));

        Assert.Equal(HttpStatusCode.BadRequest, (await host.GetAsync("/api/discord/members?sort=height", cookie, Ct)).StatusCode);
    }

    [Fact]
    public async Task OneMember_IsFoundById_WhetherInTheServerOrNot()
    {
        await using var host = await StartAsync(_db);
        var cookie = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);

        var cy = await host.GetJsonAsync<DiscordMemberView>("/api/discord/members/3", cookie, Ct);
        Assert.Equal("cy_100%", cy.Username);
        Assert.NotNull(cy.LeftAt);

        Assert.Equal(HttpStatusCode.NotFound, (await host.GetAsync("/api/discord/members/404", cookie, Ct)).StatusCode);

        // Only the server in settings.
        Assert.Equal(HttpStatusCode.NotFound, (await host.GetAsync("/api/discord/members/9", cookie, Ct)).StatusCode);
    }

    [Theory]
    [InlineData("/api/discord/members")]
    [InlineData("/api/discord/members/1")]
    public async Task BothNeedViewMembers(string path)
    {
        await using var host = await StartAsync(_db);

        var settings = await host.SignedInAsync(ModbotPermissions.ManageSettings | ModbotPermissions.ViewAuditLog, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.GetAsync(path, settings, Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.GetAsync(path, Ct)).StatusCode);

        var members = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);
        Assert.Equal(HttpStatusCode.OK, (await host.GetAsync(path, members, Ct)).StatusCode);
    }

    // ── Linked VRChat accounts ──────────────────────────────────────────────────────────────────

    /// <summary>Ada has linked; Bo linked and unlinked, which is not linked; Cy never did.</summary>
    private static async Task LinkAsync(ReadSurfaceTestHost host)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        var at = host.Clock.UtcNow;

        db.VRChatUsers.Add(new VRChatUser
        {
            UserId = "usr_ada", DisplayName = "Ada in VRChat", CurrentAvatarThumbnailImageUrl = "https://img/ada",
            FirstSeenAt = at, LastSeenAt = at, LastRefreshedAt = at,
        });

        db.DiscordAccountLinks.AddRange(
            new DiscordAccountLink { DiscordUserId = "1", DiscordUsername = "ada", VRChatUserId = "usr_ada", VRChatDisplayName = "Ada then", LinkedAt = at.AddDays(-3) },
            new DiscordAccountLink { DiscordUserId = "2", DiscordUsername = "bo", VRChatUserId = "usr_bo", LinkedAt = at.AddDays(-9), UnlinkedAt = at.AddDays(-8), UnlinkedBy = LinkEndedBy.Member });

        await db.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task TheLinkedFilter_AndTheLinkedAccount_ComeFromActiveLinks()
    {
        await using var host = await StartAsync(_db);
        await LinkAsync(host);
        var cookie = await host.SignedInAsync(ModbotPermissions.ViewMembers | ModbotPermissions.ViewProfile, Ct);

        var linked = await host.GetJsonAsync<DiscordMemberListResponse>("/api/discord/members?state=all&linked=linked", cookie, Ct);
        Assert.Equal(["1"], linked.Members.Select(m => m.UserId));
        Assert.Equal(1, linked.Total);

        // The name VRChat has now, not the one saved with the link.
        var ada = linked.Members[0].LinkedVRChat;
        Assert.NotNull(ada);
        Assert.Equal("usr_ada", ada.UserId);
        Assert.Equal("Ada in VRChat", ada.DisplayName);
        Assert.Equal("https://img/ada", ada.AvatarUrl);

        var notLinked = await host.GetJsonAsync<DiscordMemberListResponse>("/api/discord/members?state=all&linked=not-linked", cookie, Ct);
        Assert.Equal(["2", "3"], notLinked.Members.Select(m => m.UserId));
        Assert.All(notLinked.Members, m => Assert.Null(m.LinkedVRChat));

        var one = await host.GetJsonAsync<DiscordMemberView>("/api/discord/members/1", cookie, Ct);
        Assert.Equal("usr_ada", one.LinkedVRChat?.UserId);

        Assert.Equal(HttpStatusCode.BadRequest, (await host.GetAsync("/api/discord/members?linked=maybe", cookie, Ct)).StatusCode);
    }

    /// <summary>Seeing a link needs See profiles, so a caller with only See members gets neither.</summary>
    [Fact]
    public async Task Links_NeedViewProfile()
    {
        await using var host = await StartAsync(_db);
        await LinkAsync(host);
        var cookie = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);

        var list = await host.GetJsonAsync<DiscordMemberListResponse>("/api/discord/members", cookie, Ct);
        Assert.All(list.Members, m => Assert.Null(m.LinkedVRChat));

        Assert.Null((await host.GetJsonAsync<DiscordMemberView>("/api/discord/members/1", cookie, Ct)).LinkedVRChat);

        Assert.Equal(HttpStatusCode.Forbidden, (await host.GetAsync("/api/discord/members?linked=linked", cookie, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.GetAsync("/api/discord/members?linked=not-linked", cookie, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.GetAsync("/api/discord/members?linked=all", cookie, Ct)).StatusCode);
    }

    [Fact]
    public async Task TheLinkLookup_WorksFromTheDiscordSide()
    {
        await using var host = await StartAsync(_db);
        await LinkAsync(host);
        var cookie = await host.SignedInAsync(ModbotPermissions.ViewProfile, Ct);

        var found = await host.GetJsonAsync<DiscordLinkLookup>("/api/discord-links?discordUserId=1", cookie, Ct);
        Assert.Equal("usr_ada", found.Link?.VRChatUserId);
        Assert.Equal("Ada in VRChat", found.Link?.VRChatDisplayName);

        // An ended link is no link.
        Assert.Null((await host.GetJsonAsync<DiscordLinkLookup>("/api/discord-links?discordUserId=2", cookie, Ct)).Link);

        Assert.Equal(HttpStatusCode.BadRequest, (await host.GetAsync("/api/discord-links", cookie, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.GetAsync("/api/discord-links?discordUserId=1&vrchatUserId=usr_ada", cookie, Ct)).StatusCode);
    }

    // ── Messages ────────────────────────────────────────────────────────────────────────────────

    private static async Task MessagesAsync(ReadSurfaceTestHost host)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        var at = host.Clock.UtcNow;

        db.DiscordChannels.Add(new DiscordChannel { ChannelId = "500", GuildId = Guild, Name = "general", FirstSeenAt = at, UpdatedAt = at });
        db.DiscordReadBacks.Add(new DiscordReadBack { ChannelId = "600", GuildId = Guild, ParentChannelId = "500", Name = "events thread", StartedAt = at, UpdatedAt = at });
        await db.SaveChangesAsync(Ct);

        var times = new[] { at.AddDays(-40), at.AddDays(-2), at.AddDays(-1), at.AddHours(-1) };
        await new MessagePartitionMaintainer(db).EnsureForAsync(times, Ct);

        DiscordMessage Message(string id, DateTimeOffset sent, string author = "1", string guild = Guild) => new()
        {
            MessageId = id, SentAt = sent, GuildId = guild, ChannelId = "500", AuthorId = author, AuthorName = author,
            Text = "message " + id, StoredAt = at,
        };

        var edited = Message("11", times[1]);
        edited.EditedAt = times[1].AddMinutes(5);
        var deleted = Message("12", times[2]);
        deleted.DeletedAt = times[2].AddMinutes(1);
        var inThread = Message("13", times[3]);
        inThread.ThreadId = "600";
        inThread.Attachments = """[{"name":"cat.png","type":"image/png","size":1024,"url":"https://cdn/cat.png"}]""";

        db.DiscordMessages.AddRange(
            Message("10", times[0]),
            edited,
            deleted,
            inThread,
            Message("20", times[3], author: "2"),
            Message("30", times[3], author: "9", guild: "777"));

        await db.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task Messages_AreNewestFirst_WithDeletedOnesMarked_AndPaged()
    {
        await using var host = await StartAsync(_db);
        await MessagesAsync(host);
        var cookie = await host.SignedInAsync(ModbotPermissions.ReadDiscordMessages, Ct);

        var body = await host.GetJsonAsync<DiscordMemberMessagesResponse>("/api/discord/members/1/messages", cookie, Ct);

        Assert.Equal(4, body.Total);
        Assert.Equal(["13", "12", "11", "10"], body.Messages.Select(m => m.MessageId));

        var thread = body.Messages[0];
        Assert.Equal("general", thread.ChannelName);
        Assert.Equal("600", thread.ThreadId);
        Assert.Equal("events thread", thread.ThreadName);
        var file = Assert.Single(thread.Attachments);
        Assert.Equal("cat.png", file.Name);
        Assert.Equal(1024, file.Size);

        Assert.NotNull(body.Messages[1].DeletedAt);
        Assert.Equal("message 12", body.Messages[1].Text);
        Assert.NotNull(body.Messages[2].EditedAt);
        Assert.Null(body.Messages[2].DeletedAt);

        var second = await host.GetJsonAsync<DiscordMemberMessagesResponse>("/api/discord/members/1/messages?pageSize=3&page=2", cookie, Ct);
        Assert.Equal(["10"], second.Messages.Select(m => m.MessageId));
        Assert.Equal(4, second.Total);
    }

    [Fact]
    public async Task Messages_NeedReadDiscordMessages()
    {
        await using var host = await StartAsync(_db);
        const string Path = "/api/discord/members/1/messages";

        var everythingElse = await host.SignedInAsync(
            ModbotPermissions.ViewMembers | ModbotPermissions.ViewProfile | ModbotPermissions.ViewAnalytics | ModbotPermissions.ViewAuditLog, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.GetAsync(Path, everythingElse, Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.GetAsync(Path, Ct)).StatusCode);

        var reader = await host.SignedInAsync(ModbotPermissions.ReadDiscordMessages, Ct);
        Assert.Equal(HttpStatusCode.OK, (await host.GetAsync(Path, reader, Ct)).StatusCode);
    }

    // ── Metrics ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Metrics_CountMessagesAndVoicePerDay_AndListJoinsAndLeaves()
    {
        await using var host = await StartAsync(_db);
        await MessagesAsync(host);

        var now = host.Clock.UtcNow;

        FactRecord Fact(string type, DateTimeOffset at, DateTimeOffset? before = null) => new()
        {
            Type = type, OccurredAt = at, OccurredBefore = before, SubjectPlatform = FactPlatform.Discord, SubjectId = "1", Source = FactSource.Discord,
        };

        await host.WriteFactAsync(Fact(FactType.DiscordMemberJoined, now.AddDays(-30)), Ct);
        await host.WriteFactAsync(Fact(FactType.DiscordMemberLeft, now.AddDays(-20), now.AddDays(-19)), Ct);
        await host.WriteFactAsync(Fact(FactType.DiscordMemberJoined, now.AddDays(-10)), Ct);
        await host.WriteFactAsync(Fact(FactType.DiscordVoiceJoined, now.AddDays(-2).AddHours(-12)), Ct);
        await host.WriteFactAsync(Fact(FactType.DiscordVoiceLeft, now.AddDays(-2).AddHours(-11).AddMinutes(-30)), Ct);
        await host.RebuildDailyTotalsAsync(Ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewProfile, Ct);
        var body = await host.GetJsonAsync<DiscordMemberMetrics>("/api/discord/members/1/metrics", cookie, Ct);

        Assert.Equal(DiscordMemberActivity.Days, body.MessagesPerDay.Count);
        Assert.Equal(DateOnly.FromDateTime(now.UtcDateTime), body.MessagesPerDay[^1].Day);

        // Three messages in the last thirty days, a deleted one included; the one forty days ago counts all time.
        Assert.Equal(3m, body.MessagesPerDay.Sum(d => d.Value));
        Assert.Equal(4m, body.MessagesAllTime);
        Assert.Equal(30m, body.VoiceMinutesPerDay.Sum(d => d.Value));
        Assert.Equal(30m, body.VoiceMinutesAllTime);

        Assert.Equal(["joined", "left", "joined"], body.History.Select(h => h.Change));
        Assert.Equal(now.AddDays(-19), body.History[1].Before);
        Assert.Equal(now.AddDays(-30), body.FirstSeenAt);
    }

    [Fact]
    public async Task Metrics_NeedViewProfile()
    {
        await using var host = await StartAsync(_db);
        const string Path = "/api/discord/members/1/metrics";

        var members = await host.SignedInAsync(ModbotPermissions.ViewMembers | ModbotPermissions.ReadDiscordMessages, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.GetAsync(Path, members, Ct)).StatusCode);

        var profiles = await host.SignedInAsync(ModbotPermissions.ViewProfile, Ct);
        Assert.Equal(HttpStatusCode.OK, (await host.GetAsync(Path, profiles, Ct)).StatusCode);
    }

    // ── Names in the log ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A Discord person in a fact is named from the stored member list, and never from a VRChat
    /// profile that happens to share the id.
    /// </summary>
    [Fact]
    public async Task TheAuditLog_NamesDiscordPeople_FromTheMemberList()
    {
        await using var host = await StartAsync(_db);

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
            db.VRChatUsers.Add(new VRChatUser { UserId = "3", DisplayName = "Not Cy", FirstSeenAt = host.Clock.UtcNow, LastSeenAt = host.Clock.UtcNow });
            await db.SaveChangesAsync(Ct);
        }

        await host.WriteFactAsync(new FactRecord
        {
            Type = FactType.DiscordMemberKicked,
            OccurredAt = host.Clock.UtcNow.AddMinutes(-5),
            SubjectPlatform = FactPlatform.Discord,
            SubjectId = "3",
            ActorPlatform = FactPlatform.Discord,
            ActorId = "1",
            Source = FactSource.Discord,
        }, Ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAuditLog, Ct);
        var page = await host.GetJsonAsync<AuditPage>("/api/audit?subject=3&subjectPlatform=Discord", cookie, Ct);

        var entry = Assert.Single(page.Entries);
        Assert.Equal(SubjectKind.Person, entry.SubjectKind);
        Assert.Equal("Discord", entry.SubjectPlatform);
        Assert.Equal("Cy", entry.SubjectName);
        Assert.Equal("Ada the Brave", entry.ActorName);
    }

    /// <summary>
    /// Every one of the four names is searched in its plain spelling as well as as written, and
    /// the row carries the plain spelling of the name the server shows (names design).
    /// </summary>
    [Fact]
    public async Task Search_FindsFancyNamesByTheirPlainSpelling_AndTheRowCarriesIt()
    {
        await using var host = await StartAsync(_db);

        using (var scope = host.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<ModbotContext>();
            var at = host.Clock.UtcNow;
            context.DiscordMembers.AddRange(
                Member("4", "dragon135_racer", "𝕯𝖗𝖆𝖌𝖔𝖓135_𝕽𝖆𝖈𝖊𝖗", at.AddDays(-3), at: at),
                Member("5", "venus", "Venus", at.AddDays(-4), nickname: "Vᴇɴᴜs ᴅᴇ Gʀᴀᴀɴ", at: at),
                Member("6", "printsessa", "|~Принцесса Кира~|", at.AddDays(-5), at: at));
            await context.SaveChangesAsync(Ct);
        }

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);

        // The global name in a font, found by its plain spelling; digits stay digits.
        var fraktur = await host.GetJsonAsync<DiscordMemberListResponse>("/api/discord/members?search=dragon135", cookie, Ct);
        Assert.Equal(["4"], fraktur.Members.Select(m => m.UserId));
        Assert.Equal("Dragon135_Racer", fraktur.Members[0].PlainName);

        // The nickname in small capitals, which is also the name the server shows.
        var smallCaps = await host.GetJsonAsync<DiscordMemberListResponse>("/api/discord/members?search=de%20graan", cookie, Ct);
        Assert.Equal(["5"], smallCaps.Members.Select(m => m.UserId));
        Assert.Equal("Venus de Graan", smallCaps.Members[0].PlainName);

        // A name written in Russian is not folded: it is found in Russian and has no second spelling.
        var russian = await host.GetJsonAsync<DiscordMemberListResponse>("/api/discord/members?search=%D0%BA%D0%B8%D1%80%D0%B0", cookie, Ct);
        Assert.Equal(["6"], russian.Members.Select(m => m.UserId));
        Assert.Null(russian.Members[0].PlainName);
        var latin = await host.GetJsonAsync<DiscordMemberListResponse>("/api/discord/members?search=kira", cookie, Ct);
        Assert.DoesNotContain(latin.Members, m => m.UserId == "6");

        // Plain names stay plain.
        var ada = await host.GetJsonAsync<DiscordMemberListResponse>("/api/discord/members?search=ada", cookie, Ct);
        Assert.Null(ada.Members.Single(m => m.UserId == "1").PlainName);
    }
}
