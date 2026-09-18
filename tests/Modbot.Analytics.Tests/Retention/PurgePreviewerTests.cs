using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.DailyTotals;
using Modbot.Analytics.Facts;
using Modbot.Analytics.Retention;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Analytics.Tests.Retention;

/// <summary>
/// The counts a moderator reads before erasing somebody. The point of the suite is that they
/// agree with what the purge then removes: a screen that says 41,208 and destroys 41,209 is worse
/// than a screen that said nothing.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class PurgePreviewerTests : AnalyticsTestBase
{
    private const string CountedMetric = "test.counted";
    private const string Subject = "usr_preview_me";
    private const string Bystander = "usr_someone_else";

    public PurgePreviewerTests(PostgresFixture fixture) : base(fixture) { }

    private PurgePreviewer NewPreviewer(ModbotContext context) => new(context, NewJob(context));

    private UserPurger NewPurger(ModbotContext context) => new(
        context,
        Clock,
        NewJob(context),
        new FactWriter(context, Clock),
        new EventPartitionMaintainer(context, Clock));

    [Fact]
    public async Task ItCountsTheFactsAboutThem_AndNobodyElses()
    {
        await WriteAsync(
            Fact(FactType.MemberJoined, Start.AddDays(-90), subjectId: Subject),
            Fact(FactType.InstanceJoined, Start.AddDays(-40), subjectId: Subject),
            Fact(FactType.InstanceLeft, Start, subjectId: Subject),
            Fact(FactType.MemberJoined, Start, subjectId: Bystander));

        await using var context = Database.NewContext();
        var preview = await NewPreviewer(context).PreviewAsync(FactPlatform.VRChat, Subject, Ct);

        Assert.Equal(3, preview.Facts);
        Assert.Equal(Subject, preview.SubjectId);
        Assert.Equal(FactPlatform.VRChat, preview.Platform);
    }

    /// <summary>
    /// Facts where the person acted on somebody else are not theirs to erase (spec 5.8), so they
    /// are not counted as something a purge would take either.
    /// </summary>
    [Fact]
    public async Task FactsTheyWereTheActorOfAreNotCounted()
    {
        await WriteAsync(
            Fact(FactType.MemberBanned, Start, subjectId: Bystander, actorId: Subject),
            Fact(FactType.InstanceJoined, Start, subjectId: Subject));

        await using var context = Database.NewContext();
        var preview = await NewPreviewer(context).PreviewAsync(FactPlatform.VRChat, Subject, Ct);

        Assert.Equal(1, preview.Facts);
    }

    /// <summary>
    /// The number on the screen and the number the purge reports have to be the same number, or
    /// the screen is a guess with a confirmation box attached.
    /// </summary>
    [Fact]
    public async Task TheCountsMatchWhatThePurgeThenRemoves()
    {
        await WriteAsync(
            Fact(FactType.MemberJoined, Start.AddDays(-30), subjectId: Subject),
            Fact(FactType.InstanceJoined, Start.AddDays(-2), subjectId: Subject),
            Fact(FactType.InstanceLeft, Start, subjectId: Subject));

        await using var context = Database.NewContext();

        await new DailyTotalCounter(context, Clock).IncrementAsync(
            CountedMetric, DailyTotalDimensions.ForUser(FactPlatform.VRChat, Subject), 12m, ct: Ct);

        var preview = await NewPreviewer(context).PreviewAsync(FactPlatform.VRChat, Subject, Ct);
        var result = await NewPurger(context).PurgeAsync(FactPlatform.VRChat, Subject, ct: Ct);

        Assert.Equal(preview.Facts, result.FactsDeleted);
        Assert.Equal(preview.CountedDailyTotals, result.CountedDailyTotalsDeleted);
        Assert.Equal(preview.Days, result.DaysRecomputed);
    }

    /// <summary>
    /// Counting must not delete. Obvious, and exactly the bug that would be discovered by a
    /// moderator rather than by a test.
    /// </summary>
    [Fact]
    public async Task LookingSomebodyUpChangesNothing()
    {
        await WriteAsync(
            Fact(FactType.InstanceJoined, Start, subjectId: Subject),
            Fact(FactType.InstanceLeft, Start, subjectId: Subject));

        await using var context = Database.NewContext();

        await NewPreviewer(context).PreviewAsync(FactPlatform.VRChat, Subject, Ct);
        await NewPreviewer(context).PreviewAsync(FactPlatform.VRChat, Subject, Ct);

        Assert.Equal(
            2, await context.Events.AsNoTracking().CountAsync(e => e.SubjectId == Subject, Ct));
        Assert.Empty(await context.Events.AsNoTracking()
            .Where(e => e.Type == FactType.UserPurged).ToListAsync(Ct));
    }

    /// <summary>
    /// A case file about somebody survives their purge (evidence storage design §15.1), so it is
    /// counted under what is kept rather than under what goes.
    /// </summary>
    [Fact]
    public async Task CaseFilesAndTheirEvidenceAreCountedAsKept()
    {
        await using var context = Database.NewContext();

        var caseFile = new CaseFile
        {
            UserId = Subject,
            AuthorUserId = Guid.CreateVersion7(),
            AuthorUsername = "mod",
            WrittenReason = "Why.",
            CreatedAt = Start,
            UpdatedAt = Start,
            SnapshotTakenAt = Start,
        };

        context.CaseFiles.Add(caseFile);
        context.EvidenceBlobs.Add(new EvidenceBlob
        {
            Hash = new string('a', 64),
            ByteSize = 10,
            ContentType = "image/png",
            FirstStoredAt = Start,
            ReportId = caseFile.Id.ToString(),
        });

        // Somebody else's case file, and a file on it, so a wrong join shows up as a wrong count.
        var other = new CaseFile
        {
            UserId = Bystander,
            AuthorUserId = Guid.CreateVersion7(),
            AuthorUsername = "mod",
            WrittenReason = "Different person.",
            CreatedAt = Start,
            UpdatedAt = Start,
            SnapshotTakenAt = Start,
        };

        context.CaseFiles.Add(other);
        context.EvidenceBlobs.Add(new EvidenceBlob
        {
            Hash = new string('b', 64),
            ByteSize = 10,
            ContentType = "image/png",
            FirstStoredAt = Start,
            ReportId = other.Id.ToString(),
        });

        await context.SaveChangesAsync(Ct);

        var preview = await NewPreviewer(context).PreviewAsync(FactPlatform.VRChat, Subject, Ct);

        Assert.Equal(1, preview.CaseFilesKept);
        Assert.Equal(1, preview.EvidenceFilesKept);
    }

    /// <summary>
    /// A purge really does leave the case file behind, which is the half of the receipt the
    /// evidence design insists is stated rather than implied.
    /// </summary>
    [Fact]
    public async Task ThePurgeLeavesTheCaseFileWhereItWas()
    {
        await WriteAsync(Fact(FactType.MemberBanned, Start, subjectId: Subject));

        await using var context = Database.NewContext();

        context.CaseFiles.Add(new CaseFile
        {
            UserId = Subject,
            AuthorUserId = Guid.CreateVersion7(),
            AuthorUsername = "mod",
            WrittenReason = "The group's own record of its own decision.",
            CreatedAt = Start,
            UpdatedAt = Start,
            SnapshotTakenAt = Start,
        });

        await context.SaveChangesAsync(Ct);

        await NewPurger(context).PurgeAsync(FactPlatform.VRChat, Subject, ct: Ct);

        var kept = await context.CaseFiles.AsNoTracking().SingleAsync(c => c.UserId == Subject, Ct);
        Assert.Equal("The group's own record of its own decision.", kept.WrittenReason);
    }

    /// <summary>
    /// Where Modbot cannot answer, it says so rather than answering zero. A Discord account has
    /// no group ban list, and a person nobody ever swept has no membership either way.
    /// </summary>
    [Fact]
    public async Task WhatModbotCannotSayIsLeftUnsaid()
    {
        await using var context = Database.NewContext();

        var vrchat = await NewPreviewer(context).PreviewAsync(FactPlatform.VRChat, "usr_unknown", Ct);
        var discord = await NewPreviewer(context).PreviewAsync(FactPlatform.Discord, "900", Ct);

        Assert.Null(vrchat.IsMember);
        Assert.False(vrchat.IsBanned);
        Assert.Null(discord.IsMember);
        Assert.Null(discord.IsBanned);
        Assert.Null(vrchat.Name);
    }

    /// <summary>
    /// The linked account is named so an operator knows there is a second one, and a purge of the
    /// first leaves it alone -- a destructive action never widens itself past what was typed.
    /// </summary>
    [Fact]
    public async Task ALinkedAccountIsNamed_AndNotFollowed()
    {
        await WriteAsync(
            Fact(FactType.InstanceJoined, Start, subjectId: Subject),
            new FactRecord
            {
                Type = FactType.DiscordMemberJoined,
                OccurredAt = Start,
                SubjectPlatform = FactPlatform.Discord,
                SubjectId = "555",
                Source = FactSource.Discord,
            });

        await using var context = Database.NewContext();

        context.DiscordAccountLinks.Add(new DiscordAccountLink
        {
            DiscordUserId = "555",
            DiscordUsername = "someone",
            VRChatUserId = Subject,
            LinkedAt = Start,
        });

        await context.SaveChangesAsync(Ct);

        var preview = await NewPreviewer(context).PreviewAsync(FactPlatform.VRChat, Subject, Ct);

        Assert.Equal(FactPlatform.Discord, preview.LinkedAccount?.Platform);
        Assert.Equal("555", preview.LinkedAccount?.SubjectId);

        await NewPurger(context).PurgeAsync(FactPlatform.VRChat, Subject, ct: Ct);

        Assert.Equal(
            1, await context.Events.AsNoTracking().CountAsync(e => e.SubjectId == "555", Ct));
    }

    [Fact]
    public async Task SomebodyModbotHasNeverSeenCountsAsNothing()
    {
        await using var context = Database.NewContext();
        var preview = await NewPreviewer(context).PreviewAsync(FactPlatform.VRChat, "usr_nobody", Ct);

        Assert.Equal(0, preview.Facts);
        Assert.Equal(0, preview.Messages);
        Assert.Equal(0, preview.Days);
        Assert.Equal(0, preview.CaseFilesKept);
        Assert.Equal(0, preview.EvidenceFilesKept);
        Assert.Null(preview.LinkedAccount);
    }
}
