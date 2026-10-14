using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Votes;
using Modbot.TestSupport;

namespace Modbot.Discord.Tests.Votes;

/// <summary>
/// Making and closing a vote, the one path the page and <c>/vote</c> share: what is refused and in
/// what words, a vote saved only after its message is posted, the choices kept, and a close that
/// holds even when Discord refuses the rewrite.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class VoteServiceTests
{
    private const string Guild = "1";
    private const string Regulars = "811";

    private readonly PostgresFixture _db;

    public VoteServiceTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(TestServices Services, Guid Staff)> SetUpAsync()
    {
        var services = await TestServices.CreateAsync(_db, Ct);
        var staff = await services.LinkedAccountAsync("6003", ModbotPermissions.ManagePosts, ct: Ct);
        await services.ConfigureAsync(s => s.DiscordGuildId = Guild, Ct);

        await using var db = services.Database.NewContext();
        var now = services.Clock.UtcNow;
        db.DiscordRoles.Add(new DiscordRole { RoleId = Regulars, GuildId = Guild, Name = "Regulars", FirstSeenAt = now, UpdatedAt = now });
        await db.SaveChangesAsync(Ct);

        return (services, staff.Id);
    }

    private static VoteDraft Draft(
        string question = "Which world?",
        string[]? options = null,
        string? role = null,
        bool showCounts = false,
        bool anonymous = true)
        => new(question, "900", options ?? ["Forest", "Desert", "Ocean"], role, showCounts, anonymous);

    private static async Task<VoteCreated> CreateAsync(TestServices services, Guid staff, VoteDraft draft)
    {
        using var scope = services.Scope();
        return await scope.ServiceProvider.GetRequiredService<VoteService>().CreateAsync(draft, staff, Ct);
    }

    private static async Task<VoteClosed> CloseAsync(TestServices services, Guid id)
    {
        using var scope = services.Scope();
        return await scope.ServiceProvider.GetRequiredService<VoteService>().CloseAsync(id, Ct);
    }

    private static async Task<List<Vote>> SavedAsync(TestServices services)
    {
        await using var db = services.Database.NewContext();
        return await db.Votes.AsNoTracking().Include(v => v.Options).ToListAsync(Ct);
    }

    [Fact]
    public void Lines_splits_on_new_lines_trims_and_drops_blank_ones()
    {
        Assert.Equal(["Forest", "Desert", "Ocean"], VoteService.Lines(" Forest \r\n\n  Desert\n\t\nOcean  "));
        Assert.Empty(VoteService.Lines(null));
        Assert.Empty(VoteService.Lines("  \n "));
    }

    [Fact]
    public async Task A_vote_is_posted_first_and_saved_with_its_message_options_in_order_and_choices()
    {
        var (services, staff) = await SetUpAsync();
        await using var _ = services;

        var made = await CreateAsync(services, staff, Draft(role: Regulars, showCounts: true, anonymous: false));

        Assert.True(made.Done);
        Assert.Null(made.Problem);
        Assert.Single(services.Votes.Posted);

        var saved = Assert.Single(await SavedAsync(services));
        Assert.Equal("777", saved.MessageId);
        Assert.Equal("900", saved.ChannelId);
        Assert.Equal(Regulars, saved.RoleId);
        Assert.True(saved.ShowCounts);
        Assert.False(saved.Anonymous);
        Assert.Equal(VoteStatuses.Open, saved.Status);
        Assert.Equal(staff, saved.CreatedBy);
        Assert.Equal(["Forest", "Desert", "Ocean"], saved.Options.OrderBy(o => o.Position).Select(o => o.Label));
    }

    [Fact]
    public async Task The_choices_left_out_make_a_vote_that_is_anonymous_and_hides_its_counts()
    {
        var (services, staff) = await SetUpAsync();
        await using var _ = services;

        await CreateAsync(services, staff, Draft());

        var saved = Assert.Single(await SavedAsync(services));
        Assert.False(saved.ShowCounts);
        Assert.True(saved.Anonymous);
        Assert.Null(saved.RoleId);
    }

    [Fact]
    public async Task Text_around_the_question_options_and_channel_is_trimmed()
    {
        var (services, staff) = await SetUpAsync();
        await using var _ = services;

        await CreateAsync(services, staff, new VoteDraft("  Which world?  ", " 900 ", [" Forest ", "", "Desert"], "  ", false, true));

        var saved = Assert.Single(await SavedAsync(services));
        Assert.Equal("Which world?", saved.Question);
        Assert.Equal("900", saved.ChannelId);
        Assert.Null(saved.RoleId);
        Assert.Equal(["Forest", "Desert"], saved.Options.OrderBy(o => o.Position).Select(o => o.Label));
    }

    [Fact]
    public async Task A_draft_that_is_not_allowed_is_refused_in_words_and_nothing_is_posted()
    {
        var (services, staff) = await SetUpAsync();
        await using var _ = services;

        var tooManyOptions = Enumerable.Range(0, VoteLimits.MostOptions + 1).Select(i => $"Option {i}").ToArray();
        var cases = new (VoteDraft Draft, string Problem)[]
        {
            (Draft(question: ""), "Write the question."),
            (Draft(question: new string('x', VoteLimits.MostQuestionLength + 1)), $"The question is one line of at most {VoteLimits.MostQuestionLength} characters."),
            (Draft(question: "One\nTwo"), $"The question is one line of at most {VoteLimits.MostQuestionLength} characters."),
            (Draft(options: ["Only one"]), "Add at least two options."),
            (Draft(options: tooManyOptions), $"At most {VoteLimits.MostOptions} options."),
            (Draft(options: ["Forest", new string('x', VoteLimits.MostLabelLength + 1)]), $"An option is at most {VoteLimits.MostLabelLength} characters."),
            (Draft(options: ["Forest", "FOREST"]), "Two options say the same thing."),
            (Draft(role: "99999"), "That role is not in the Discord server."),
            (new VoteDraft("Which world?", "", ["Forest", "Desert"], null), "Pick the channel."),
        };

        foreach (var (draft, problem) in cases)
        {
            var made = await CreateAsync(services, staff, draft);
            Assert.Null(made.Vote);
            Assert.Equal(problem, made.Problem);
        }

        Assert.Empty(services.Votes.Posted);
        Assert.Empty(await SavedAsync(services));
    }

    [Fact]
    public async Task A_role_that_was_removed_from_the_server_is_refused()
    {
        var (services, staff) = await SetUpAsync();
        await using var _ = services;

        await using (var db = services.Database.NewContext())
        {
            var role = await db.DiscordRoles.SingleAsync(r => r.RoleId == Regulars, Ct);
            role.RemovedAt = services.Clock.UtcNow;
            await db.SaveChangesAsync(Ct);
        }

        var made = await CreateAsync(services, staff, Draft(role: Regulars));

        Assert.Equal("That role is not in the Discord server.", made.Problem);
        Assert.Empty(services.Votes.Posted);
    }

    [Fact]
    public async Task A_message_that_could_not_be_posted_leaves_no_vote()
    {
        var (services, staff) = await SetUpAsync();
        await using var _ = services;
        services.Votes.PostOutcome = VoteSiteOutcome.Failed("Missing permissions.");

        var made = await CreateAsync(services, staff, Draft());

        Assert.Null(made.Vote);
        Assert.Equal("Missing permissions.", made.Problem);
        Assert.False(made.BotOffline);
        Assert.Empty(await SavedAsync(services));
    }

    [Fact]
    public async Task A_bot_that_is_offline_is_said_so_and_leaves_no_vote()
    {
        var (services, staff) = await SetUpAsync();
        await using var _ = services;
        services.Votes.PostOutcome = VoteSiteOutcome.Offline;

        var made = await CreateAsync(services, staff, Draft());

        Assert.True(made.BotOffline);
        Assert.Equal("The Discord bot is not connected.", made.Problem);
        Assert.Empty(await SavedAsync(services));
    }

    [Fact]
    public async Task Closing_marks_the_vote_closed_and_asks_for_the_result_with_the_counts()
    {
        var (services, staff) = await SetUpAsync();
        await using var _ = services;
        var vote = (await CreateAsync(services, staff, Draft())).Vote!;

        await using (var db = services.Database.NewContext())
        {
            var option = vote.Options.OrderBy(o => o.Position).ToList();
            db.VotePicks.AddRange(
                new VotePick { VoteId = vote.Id, OptionId = option[1].Id, DiscordUserId = "6001", VotedAt = services.Clock.UtcNow },
                new VotePick { VoteId = vote.Id, OptionId = option[1].Id, DiscordUserId = "6002", VotedAt = services.Clock.UtcNow });
            await db.SaveChangesAsync(Ct);
        }

        var closed = await CloseAsync(services, vote.Id);

        Assert.False(closed.AlreadyClosed);
        Assert.Null(closed.MessageProblem);

        var saved = Assert.Single(await SavedAsync(services));
        Assert.Equal(VoteStatuses.Closed, saved.Status);
        Assert.NotNull(saved.ClosedAt);

        var (resultId, counts) = Assert.Single(services.Votes.Results);
        Assert.Equal(vote.Id, resultId);
        Assert.Equal(2, counts[saved.Options.Single(o => o.Label == "Desert").Id]);
        Assert.Single(counts);
    }

    [Fact]
    public async Task Closing_a_closed_vote_changes_nothing_and_does_not_rewrite_the_message_again()
    {
        var (services, staff) = await SetUpAsync();
        await using var _ = services;
        var vote = (await CreateAsync(services, staff, Draft())).Vote!;

        await CloseAsync(services, vote.Id);
        var closedAt = (await SavedAsync(services)).Single().ClosedAt;

        var again = await CloseAsync(services, vote.Id);

        Assert.True(again.AlreadyClosed);
        Assert.Equal(closedAt, (await SavedAsync(services)).Single().ClosedAt);
        Assert.Single(services.Votes.Results);
    }

    [Fact]
    public async Task Closing_a_vote_that_is_not_there_says_so()
    {
        var (services, _) = await SetUpAsync();
        await using var _ = services;

        var closed = await CloseAsync(services, Guid.NewGuid());

        Assert.Null(closed.Vote);
        Assert.Empty(services.Votes.Results);
    }

    [Fact]
    public async Task A_rewrite_Discord_refuses_still_closes_the_vote_and_gives_the_reason()
    {
        var (services, staff) = await SetUpAsync();
        await using var _ = services;
        var vote = (await CreateAsync(services, staff, Draft())).Vote!;
        services.Votes.ResultOutcome = VoteSiteOutcome.Failed("The message is gone.");

        var closed = await CloseAsync(services, vote.Id);

        Assert.Equal("The message is gone.", closed.MessageProblem);
        Assert.Equal(VoteStatuses.Closed, (await SavedAsync(services)).Single().Status);
    }

    [Fact]
    public async Task Counts_are_for_the_votes_asked_about_and_leave_out_options_nobody_picked()
    {
        var (services, staff) = await SetUpAsync();
        await using var _ = services;
        var one = (await CreateAsync(services, staff, Draft("One?"))).Vote!;
        var two = (await CreateAsync(services, staff, Draft("Two?"))).Vote!;

        await using (var db = services.Database.NewContext())
        {
            db.VotePicks.AddRange(
                new VotePick { VoteId = one.Id, OptionId = one.Options[0].Id, DiscordUserId = "6001", VotedAt = services.Clock.UtcNow },
                new VotePick { VoteId = two.Id, OptionId = two.Options[2].Id, DiscordUserId = "6001", VotedAt = services.Clock.UtcNow });
            await db.SaveChangesAsync(Ct);
        }

        using var scope = services.Scope();
        var counts = await scope.ServiceProvider.GetRequiredService<VoteService>().CountsAsync([one.Id], Ct);

        Assert.Equal(new Dictionary<Guid, int> { [one.Options[0].Id] = 1 }, counts);
    }
}
