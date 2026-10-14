using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Data.Entities;
using Modbot.Discord.Gateway;
using Modbot.Discord.Votes;
using Modbot.TestSupport;

namespace Modbot.Discord.Tests.Votes;

/// <summary>
/// Pressing a vote's button or list: the pick is saved or changed, one row for each person, a vote
/// that is closed, gone or limited to a role refuses, and a vote that shows counts asks for its
/// message to be rewritten once however many people press.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class VotePressTests
{
    private const string Ada = "6001";
    private const string Bea = "6002";
    private const string Staff = "6003";

    private readonly PostgresFixture _db;

    public VotePressTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(TestServices Services, Vote Vote)> SetUpAsync(
        string? role = null, string status = VoteStatuses.Open, bool showCounts = false, bool anonymous = true)
    {
        var services = await TestServices.CreateAsync(_db, Ct);
        var staff = await services.LinkedAccountAsync(Staff, ModbotPermissions.ManagePosts, ct: Ct);

        var vote = new Vote
        {
            Question = "Which world?",
            ChannelId = "900",
            MessageId = "1234",
            RoleId = role,
            Status = status,
            ShowCounts = showCounts,
            Anonymous = anonymous,
            CreatedBy = staff.Id,
            CreatedAt = services.Clock.UtcNow,
        };

        vote.Options =
        [
            new VoteOption { VoteId = vote.Id, Label = "Forest", Position = 0 },
            new VoteOption { VoteId = vote.Id, Label = "Desert", Position = 1 },
            new VoteOption { VoteId = vote.Id, Label = "Ocean", Position = 2 },
        ];

        await using var db = services.Database.NewContext();

        if (role is not null)
        {
            var now = services.Clock.UtcNow;
            db.DiscordRoles.Add(new DiscordRole { RoleId = role, GuildId = "1", Name = "Regulars", FirstSeenAt = now, UpdatedAt = now });
        }

        db.Votes.Add(vote);
        await db.SaveChangesAsync(Ct);

        return (services, vote);
    }

    private static async Task<string> PressAsync(
        TestServices services,
        string user,
        string buttonId,
        IReadOnlyList<string>? held = null,
        IReadOnlyList<string>? values = null)
    {
        var press = new DiscordButtonPress(user, "someone", buttonId, (_, _) => Task.CompletedTask)
        {
            MemberRoleIds = held ?? [],
            Values = values ?? [],
        };

        using var scope = services.Scope();
        var reply = await scope.ServiceProvider.GetRequiredService<VotePress>().HandleAsync(press, Ct);
        return reply.Text ?? string.Empty;
    }

    private static async Task<List<VotePick>> PicksAsync(TestServices services)
    {
        await using var db = services.Database.NewContext();
        return await db.VotePicks.AsNoTracking().OrderBy(p => p.DiscordUserId).ToListAsync(Ct);
    }

    [Fact]
    public async Task A_button_saves_the_members_pick_and_tells_only_them()
    {
        var (services, vote) = await SetUpAsync();
        await using var _ = services;

        var said = await PressAsync(services, Ada, VoteMessage.ButtonId(vote.Id, vote.Options[1].Id));

        Assert.Equal("You voted for “Desert”.", said);
        var pick = Assert.Single(await PicksAsync(services));
        Assert.Equal((Ada, vote.Options[1].Id), (pick.DiscordUserId, pick.OptionId));
    }

    [Fact]
    public async Task A_second_press_on_another_option_changes_the_same_row()
    {
        var (services, vote) = await SetUpAsync();
        await using var _ = services;

        await PressAsync(services, Ada, VoteMessage.ButtonId(vote.Id, vote.Options[0].Id));
        var said = await PressAsync(services, Ada, VoteMessage.ButtonId(vote.Id, vote.Options[2].Id));

        Assert.Equal("Changed your vote from “Forest” to “Ocean”.", said);
        var pick = Assert.Single(await PicksAsync(services));
        Assert.Equal(vote.Options[2].Id, pick.OptionId);
    }

    [Fact]
    public async Task Pressing_the_same_option_again_changes_nothing()
    {
        var (services, vote) = await SetUpAsync();
        await using var _ = services;
        var id = VoteMessage.ButtonId(vote.Id, vote.Options[0].Id);

        await PressAsync(services, Ada, id);
        var said = await PressAsync(services, Ada, id);

        Assert.Equal("You already voted for “Forest”.", said);
        Assert.Single(await PicksAsync(services));
    }

    [Fact]
    public async Task Two_people_each_have_their_own_pick()
    {
        var (services, vote) = await SetUpAsync();
        await using var _ = services;

        await PressAsync(services, Ada, VoteMessage.ButtonId(vote.Id, vote.Options[0].Id));
        await PressAsync(services, Bea, VoteMessage.ButtonId(vote.Id, vote.Options[0].Id));

        Assert.Equal([Ada, Bea], (await PicksAsync(services)).Select(p => p.DiscordUserId));
    }

    [Fact]
    public async Task A_pick_from_a_drop_down_list_is_read_from_its_values()
    {
        var (services, vote) = await SetUpAsync();
        await using var _ = services;

        var said = await PressAsync(
            services,
            Ada,
            VoteMessage.ListId(vote.Id, 0),
            values: [vote.Options[2].Id.ToString("N")]);

        Assert.Equal("You voted for “Ocean”.", said);
    }

    [Fact]
    public async Task A_closed_vote_takes_no_more_picks()
    {
        var (services, vote) = await SetUpAsync(status: VoteStatuses.Closed);
        await using var _ = services;

        var said = await PressAsync(services, Ada, VoteMessage.ButtonId(vote.Id, vote.Options[0].Id));

        Assert.Equal(VotePress.ClosedMessage, said);
        Assert.Empty(await PicksAsync(services));
    }

    [Fact]
    public async Task A_vote_that_is_gone_says_so()
    {
        var (services, vote) = await SetUpAsync();
        await using var _ = services;

        var said = await PressAsync(services, Ada, VoteMessage.ButtonId(Guid.NewGuid(), vote.Options[0].Id));

        Assert.Equal(VotePress.GoneMessage, said);
    }

    [Fact]
    public async Task An_option_that_is_not_on_the_vote_is_refused()
    {
        var (services, vote) = await SetUpAsync();
        await using var _ = services;

        var said = await PressAsync(services, Ada, VoteMessage.ButtonId(vote.Id, Guid.NewGuid()));

        Assert.Equal(VotePress.NoOptionMessage, said);
        Assert.Empty(await PicksAsync(services));
    }

    [Fact]
    public async Task A_vote_limited_to_a_role_refuses_a_member_without_it_and_names_the_role()
    {
        var (services, vote) = await SetUpAsync(role: "811");
        await using var _ = services;

        var said = await PressAsync(services, Ada, VoteMessage.ButtonId(vote.Id, vote.Options[0].Id), held: ["999"]);

        Assert.Equal("Only people with the “Regulars” role can vote on this.", said);
        Assert.Empty(await PicksAsync(services));
    }

    [Fact]
    public async Task A_vote_limited_to_a_role_takes_a_member_who_holds_it()
    {
        var (services, vote) = await SetUpAsync(role: "811");
        await using var _ = services;

        await PressAsync(services, Ada, VoteMessage.ButtonId(vote.Id, vote.Options[0].Id), held: ["811"]);

        Assert.Single(await PicksAsync(services));
    }

    [Fact]
    public async Task A_vote_limited_to_a_role_refuses_when_the_roles_could_not_be_read()
    {
        var (services, vote) = await SetUpAsync(role: "811");
        await using var _ = services;

        var press = new DiscordButtonPress(Ada, "someone", VoteMessage.ButtonId(vote.Id, vote.Options[0].Id), (_, _) => Task.CompletedTask);

        using var scope = services.Scope();
        var reply = await scope.ServiceProvider.GetRequiredService<VotePress>().HandleAsync(press, Ct);

        Assert.Equal(VotePress.NoRolesMessage, reply.Text);
        Assert.Empty(await PicksAsync(services));
    }

    [Fact]
    public async Task A_vote_that_shows_counts_gets_one_rewrite_for_several_presses()
    {
        var (services, vote) = await SetUpAsync(showCounts: true);
        await using var _ = services;

        await PressAsync(services, Ada, VoteMessage.ButtonId(vote.Id, vote.Options[0].Id));
        await PressAsync(services, Bea, VoteMessage.ButtonId(vote.Id, vote.Options[0].Id));

        // The refresher is a singleton that waits a moment; asking again hands back the wait that is
        // already running, so this returns when the one rewrite has been made.
        await services.Provider.GetRequiredService<VoteRefresher>().Request(vote.Id);

        var refresh = Assert.Single(services.Votes.Refreshed);
        Assert.Equal(vote.Id, refresh.VoteId);
        Assert.Equal(2, refresh.Counts[vote.Options[0].Id]);
    }

    [Fact]
    public async Task A_vote_that_hides_counts_is_never_rewritten_by_a_press()
    {
        var (services, vote) = await SetUpAsync(showCounts: false);
        await using var _ = services;

        await PressAsync(services, Ada, VoteMessage.ButtonId(vote.Id, vote.Options[0].Id));
        await Task.Delay(300, Ct);

        Assert.Empty(services.Votes.Refreshed);
    }

    [Fact]
    public async Task A_press_that_changes_nothing_does_not_ask_for_a_rewrite()
    {
        var (services, vote) = await SetUpAsync(showCounts: true);
        await using var _ = services;
        var id = VoteMessage.ButtonId(vote.Id, vote.Options[0].Id);

        await PressAsync(services, Ada, id);
        await services.Provider.GetRequiredService<VoteRefresher>().Request(vote.Id);
        Assert.Single(services.Votes.Refreshed);

        await PressAsync(services, Ada, id);
        await Task.Delay(300, Ct);

        Assert.Single(services.Votes.Refreshed);
    }

    [Fact]
    public async Task The_rewrite_includes_picks_made_during_the_wait_and_a_later_press_starts_the_next()
    {
        var (services, vote) = await SetUpAsync(showCounts: true);
        await using var _ = services;
        var refresher = services.Provider.GetRequiredService<VoteRefresher>();

        await PressAsync(services, Ada, VoteMessage.ButtonId(vote.Id, vote.Options[0].Id));
        await refresher.Request(vote.Id);

        await PressAsync(services, Bea, VoteMessage.ButtonId(vote.Id, vote.Options[1].Id));
        await refresher.Request(vote.Id);

        Assert.Equal(2, services.Votes.Refreshed.Count);
        Assert.Equal(1, services.Votes.Refreshed[1].Counts[vote.Options[1].Id]);
    }

    [Fact]
    public async Task A_vote_closed_during_the_wait_is_not_rewritten()
    {
        var (services, vote) = await SetUpAsync(showCounts: true);
        await using var _ = services;

        await PressAsync(services, Ada, VoteMessage.ButtonId(vote.Id, vote.Options[0].Id));

        await using (var db = services.Database.NewContext())
        {
            var stored = await db.Votes.SingleAsync(v => v.Id == vote.Id, Ct);
            stored.Status = VoteStatuses.Closed;
            await db.SaveChangesAsync(Ct);
        }

        await services.Provider.GetRequiredService<VoteRefresher>().Request(vote.Id);

        Assert.Empty(services.Votes.Refreshed);
    }
}
