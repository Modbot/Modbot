using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Configuration;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Profiles;
using Modbot.Discord.Commands;
using Modbot.Discord.Gateway;
using Modbot.TestSupport;

namespace Modbot.Discord.Tests.Commands;

/// <summary>
/// <c>/find</c> (find design): members found by what their profiles say, only the ones who turned
/// Findable on, only through answers the searcher may read, never the searcher, at most 25, and a
/// View profile button that shows the same card <c>/profile</c> would.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class FindCommandTests
{
    private const string Guild = "424242";
    private const string Ada = "7001";
    private const string Bea = "7002";
    private const string Cy = "7003";
    private const string Staff = "7009";

    private readonly PostgresFixture _db;

    public FindCommandTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<TestServices> ServicesAsync(bool on = true)
    {
        var services = await TestServices.CreateAsync(_db, Ct);
        await services.ConfigureAsync(s =>
        {
            s.SwitchCommand("find", on);
            s.SwitchCommand("profile", true);
            s.DiscordGuildId = Guild;
        }, Ct);
        return services;
    }

    private static DiscordCommandCall Call(string who, params (string Option, string Value)[] options)
        => new(
            who,
            "kiri",
            DiscordCommands.Find,
            options.ToDictionary(o => o.Option, o => o.Value),
            (_, _) => Task.CompletedTask);

    private static DiscordCommandCall Find(string who, string field, string value)
        => Call(who, (DiscordCommands.FindFieldOption, field), (DiscordCommands.FindValueOption, value));

    private static async Task<DiscordReply> RunAsync(TestServices services, DiscordCommandCall call)
    {
        using var scope = services.Scope();
        return await scope.ServiceProvider.GetRequiredService<DiscordCommandHandler>().HandleAsync(call, Ct);
    }

    private static async Task AddMemberAsync(
        TestServices services, string userId, string name, bool isBot = false, bool left = false)
    {
        await using var db = services.Database.NewContext();
        db.DiscordMembers.Add(new DiscordMember
        {
            GuildId = Guild,
            UserId = userId,
            Username = name.ToLowerInvariant(),
            DisplayName = name,
            IsBot = isBot,
            LeftAt = left ? services.Clock.UtcNow : null,
            FirstSeenAt = services.Clock.UtcNow,
            UpdatedAt = services.Clock.UtcNow,
        });
        await db.SaveChangesAsync(Ct);
    }

    /// <summary>A pick-list "Role" (Singer, Dancer, Singer-songwriter) and a text "Zone".</summary>
    private static async Task<(ProfileField Role, ProfileField Zone)> AddFieldsAsync(
        TestServices services, ProfileVisibility level = ProfileVisibility.Members)
    {
        var at = services.Clock.UtcNow;

        var role = new ProfileField
        {
            Name = "Role",
            Kind = ProfileFieldKind.PickList,
            DefaultVisibility = level,
            SortOrder = 0,
            CreatedAt = at,
            UpdatedAt = at,
            Options =
            [
                new ProfileOption { Label = "Singer", SortOrder = 0 },
                new ProfileOption { Label = "Dancer", SortOrder = 1 },
                new ProfileOption { Label = "Singer-songwriter", SortOrder = 2 },
            ],
        };

        var zone = new ProfileField
        {
            Name = "Zone",
            Kind = ProfileFieldKind.Text,
            DefaultVisibility = level,
            SortOrder = 1,
            CreatedAt = at,
            UpdatedAt = at,
        };

        await using var db = services.Database.NewContext();
        db.ProfileFields.AddRange(role, zone);
        await db.SaveChangesAsync(Ct);
        return (role, zone);
    }

    private static string OptionOf(ProfileField field, string label) => field.Options.Single(o => o.Label == label).Id.ToString();

    private static async Task AnswerAsync(TestServices services, string userId, ProfileField field, string answer)
    {
        await using var db = services.Database.NewContext();
        await MemberProfiles.SetAnswersAsync(db, userId, new Dictionary<Guid, string?> { [field.Id] = answer }, services.Clock.UtcNow, Ct);
    }

    private static async Task SetLevelAsync(TestServices services, string userId, ProfileField field, ProfileVisibility level)
    {
        await using var db = services.Database.NewContext();
        await MemberProfiles.SetVisibilityAsync(db, userId, new Dictionary<Guid, ProfileVisibility> { [field.Id] = level }, services.Clock.UtcNow, Ct);
    }

    private static async Task FindableAsync(TestServices services, string userId)
    {
        await using var db = services.Database.NewContext();
        await MemberProfiles.SetFindableAsync(db, userId, true, services.Clock.UtcNow, Ct);
    }

    /// <summary>A findable member who is a singer in UTC-5, with both answers opened to members.</summary>
    private async Task<(TestServices Services, ProfileField Role, ProfileField Zone)> WorldAsync()
    {
        var services = await ServicesAsync();
        var (role, zone) = await AddFieldsAsync(services);
        await AddMemberAsync(services, Ada, "Ada");
        return (services, role, zone);
    }

    private static string[] Names(DiscordReply reply)
        => [.. Assert.Single(reply.Embeds).Description!.Split('\n').Select(l => l[(l.IndexOf(' ') + 1)..])];

    private static async Task<JsonElement> LastCommandFactAsync(TestServices services)
    {
        var facts = await services.FactsOfTypeAsync(FactType.DiscordCommandRun, Ct);
        return JsonDocument.Parse(facts[^1].Data).RootElement;
    }

    // ── What is registered ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Find_IsForEveryone_OffByDefault_AndTakesTwoPairsOfFieldAndValue()
    {
        var find = Assert.Single(DiscordCommands.All, c => c.Name == DiscordCommands.Find);

        Assert.True(DiscordCommands.IsForEveryone(DiscordCommands.Find));
        Assert.False(DiscordCommandSwitches.Find("find")!.OnByDefault);
        Assert.Equal(
            ["field", "value", "also-field", "also-value"],
            find.Options.Select(o => o.Name));
        Assert.All(find.Options, o => Assert.True(o.Suggests));
        Assert.Equal([true, true, false, false], find.Options.Select(o => o.Required));
    }

    // ── Who is listed ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task OnlyMembersWhoTurnedFindableOnAreListed()
    {
        var (services, role, _) = await WorldAsync();
        await using var disposing = services;
        await AddMemberAsync(services, Bea, "Bea");
        await AddMemberAsync(services, Cy, "Cy");
        await AnswerAsync(services, Bea, role, OptionOf(role, "Dancer"));
        await AnswerAsync(services, Cy, role, OptionOf(role, "Dancer"));
        await FindableAsync(services, Bea);

        var reply = await RunAsync(services, Find(Ada, "Role", "Dancer"));

        Assert.Equal(["Bea"], Names(reply));
        Assert.Equal("Found 1", reply.Embeds[0].Title);
    }

    [Fact]
    public async Task NobodyFindableMeansNobodyFound_WithNoButtons()
    {
        var (services, role, _) = await WorldAsync();
        await using var disposing = services;
        await AddMemberAsync(services, Bea, "Bea");
        await AnswerAsync(services, Bea, role, OptionOf(role, "Dancer"));

        var reply = await RunAsync(services, Find(Ada, "Role", "Dancer"));

        Assert.Equal(FindCommand.NobodyMessage, reply.Text);
        Assert.Empty(reply.Embeds);
        Assert.Null(reply.Actions);
    }

    [Fact]
    public async Task TheSearcherIsNeverListed_EvenWhenFindableAndMatching()
    {
        var (services, role, _) = await WorldAsync();
        await using var disposing = services;
        await AddMemberAsync(services, Bea, "Bea");
        await AnswerAsync(services, Ada, role, OptionOf(role, "Dancer"));
        await AnswerAsync(services, Bea, role, OptionOf(role, "Dancer"));
        await FindableAsync(services, Ada);
        await FindableAsync(services, Bea);

        var reply = await RunAsync(services, Find(Ada, "Role", "Dancer"));

        Assert.Equal(["Bea"], Names(reply));
    }

    [Fact]
    public async Task BotsAndMembersWhoLeftAreNotListed()
    {
        var (services, role, _) = await WorldAsync();
        await using var disposing = services;
        await AddMemberAsync(services, Bea, "Bea", isBot: true);
        await AddMemberAsync(services, Cy, "Cy", left: true);

        foreach (var id in new[] { Bea, Cy })
        {
            await AnswerAsync(services, id, role, OptionOf(role, "Dancer"));
            await FindableAsync(services, id);
        }

        var reply = await RunAsync(services, Find(Ada, "Role", "Dancer"));

        Assert.Equal(FindCommand.NobodyMessage, reply.Text);
    }

    // ── Hidden answers ──────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(ProfileVisibility.Staff)]
    [InlineData(ProfileVisibility.OnlyMe)]
    public async Task AnAnswerNotOpenedToMembersNeverMatchesForAMember(ProfileVisibility level)
    {
        var (services, role, zone) = await WorldAsync();
        await using var disposing = services;
        await AddMemberAsync(services, Bea, "Bea");
        await AnswerAsync(services, Bea, role, OptionOf(role, "Singer"));
        await AnswerAsync(services, Bea, zone, "UTC-5");
        await SetLevelAsync(services, Bea, role, level);
        await FindableAsync(services, Bea);

        // The role is hidden, so a search on it finds nothing; the zone is open, so that one does.
        var hidden = await RunAsync(services, Find(Ada, "Role", "Singer"));
        var open = await RunAsync(services, Find(Ada, "Zone", "UTC-5"));
        var both = await RunAsync(services, Call(
            Ada,
            (DiscordCommands.FindFieldOption, "Zone"),
            (DiscordCommands.FindValueOption, "UTC-5"),
            (DiscordCommands.FindAlsoFieldOption, "Role"),
            (DiscordCommands.FindAlsoValueOption, "Singer")));

        Assert.Equal(FindCommand.NobodyMessage, hidden.Text);
        Assert.Equal(["Bea"], Names(open));
        Assert.Equal(FindCommand.NobodyMessage, both.Text);
    }

    [Fact]
    public async Task AHiddenAnswerAndNoAnswerAtAllGiveTheSameReply()
    {
        var (services, role, _) = await WorldAsync();
        await using var disposing = services;
        await AddMemberAsync(services, Bea, "Bea");
        await AddMemberAsync(services, Cy, "Cy");
        await AnswerAsync(services, Bea, role, OptionOf(role, "Singer"));
        await SetLevelAsync(services, Bea, role, ProfileVisibility.OnlyMe);
        await FindableAsync(services, Bea);
        await FindableAsync(services, Cy);

        var reply = await RunAsync(services, Find(Ada, "Role", "Singer"));

        Assert.Equal(FindCommand.NobodyMessage, reply.Text);
        Assert.Empty(reply.Embeds);
    }

    [Fact]
    public async Task StaffMatchOnEveryAnswerEvenOnesKeptToTheMember()
    {
        var (services, role, zone) = await WorldAsync();
        await using var disposing = services;
        await AddMemberAsync(services, Staff, "Moderator");
        await services.LinkedAccountAsync(Staff, ModbotPermissions.ViewMembers, ct: Ct);
        await AddMemberAsync(services, Bea, "Bea");
        await AnswerAsync(services, Bea, role, OptionOf(role, "Singer"));
        await AnswerAsync(services, Bea, zone, "UTC-5");
        await SetLevelAsync(services, Bea, role, ProfileVisibility.OnlyMe);
        await SetLevelAsync(services, Bea, zone, ProfileVisibility.Staff);
        await FindableAsync(services, Bea);

        var reply = await RunAsync(services, Call(
            Staff,
            (DiscordCommands.FindFieldOption, "Role"),
            (DiscordCommands.FindValueOption, "Singer"),
            (DiscordCommands.FindAlsoFieldOption, "Zone"),
            (DiscordCommands.FindAlsoValueOption, "UTC-5")));

        Assert.Equal(["Bea"], Names(reply));
    }

    [Fact]
    public async Task StaffStillOnlyFindMembersWhoTurnedFindableOn()
    {
        var (services, role, _) = await WorldAsync();
        await using var disposing = services;
        await AddMemberAsync(services, Staff, "Moderator");
        await services.LinkedAccountAsync(Staff, ModbotPermissions.ViewMembers, ct: Ct);
        await AddMemberAsync(services, Bea, "Bea");
        await AnswerAsync(services, Bea, role, OptionOf(role, "Singer"));

        var reply = await RunAsync(services, Find(Staff, "Role", "Singer"));

        Assert.Equal(FindCommand.NobodyMessage, reply.Text);
    }

    [Fact]
    public async Task AModbotAccountWithoutSeeMembersIsNotStaffHere()
    {
        var (services, role, _) = await WorldAsync();
        await using var disposing = services;
        await AddMemberAsync(services, Staff, "Moderator");
        await services.LinkedAccountAsync(Staff, ModbotPermissions.ViewCalendar, ct: Ct);
        await AddMemberAsync(services, Bea, "Bea");
        await AnswerAsync(services, Bea, role, OptionOf(role, "Singer"));
        await SetLevelAsync(services, Bea, role, ProfileVisibility.OnlyMe);
        await FindableAsync(services, Bea);

        var reply = await RunAsync(services, Find(Staff, "Role", "Singer"));

        Assert.Equal(FindCommand.NobodyMessage, reply.Text);
    }

    // ── Matching ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task APickListNamedExactlyMatchesThatOptionOnly_AndAPartMatchesEveryOptionHoldingIt()
    {
        var (services, role, _) = await WorldAsync();
        await using var disposing = services;
        await AddMemberAsync(services, Bea, "Bea");
        await AddMemberAsync(services, Cy, "Cy");
        await AnswerAsync(services, Bea, role, OptionOf(role, "Singer"));
        await AnswerAsync(services, Cy, role, OptionOf(role, "Singer-songwriter"));
        await FindableAsync(services, Bea);
        await FindableAsync(services, Cy);

        var exact = await RunAsync(services, Find(Ada, "role", "SINGER"));
        var part = await RunAsync(services, Find(Ada, "Role", "song"));

        Assert.Equal(["Bea"], Names(exact));
        Assert.Equal(["Cy"], Names(part));
    }

    [Fact]
    public async Task ATextFieldMatchesWordsInsideTheAnswerWithoutRegardToCase()
    {
        var (services, _, zone) = await WorldAsync();
        await using var disposing = services;
        await AddMemberAsync(services, Bea, "Bea");
        await AnswerAsync(services, Bea, zone, "Usually UTC-5, sometimes UTC-4");
        await FindableAsync(services, Bea);

        Assert.Equal(["Bea"], Names(await RunAsync(services, Find(Ada, "Zone", "utc-5"))));
        Assert.Equal(FindCommand.NobodyMessage, (await RunAsync(services, Find(Ada, "Zone", "utc-9"))).Text);
    }

    [Fact]
    public async Task TextWithPercentOrUnderscoreIsLookedForAsTypedNotAsAPattern()
    {
        var (services, _, zone) = await WorldAsync();
        await using var disposing = services;
        await AddMemberAsync(services, Bea, "Bea");
        await AnswerAsync(services, Bea, zone, "UTC-5");
        await FindableAsync(services, Bea);

        Assert.Equal(FindCommand.NobodyMessage, (await RunAsync(services, Find(Ada, "Zone", "%"))).Text);
        Assert.Equal(FindCommand.NobodyMessage, (await RunAsync(services, Find(Ada, "Zone", "U_C"))).Text);
    }

    [Fact]
    public async Task TwoFiltersMustBothMatch()
    {
        var (services, role, zone) = await WorldAsync();
        await using var disposing = services;
        await AddMemberAsync(services, Bea, "Bea");
        await AddMemberAsync(services, Cy, "Cy");
        await AnswerAsync(services, Bea, role, OptionOf(role, "Singer"));
        await AnswerAsync(services, Bea, zone, "UTC-5");
        await AnswerAsync(services, Cy, role, OptionOf(role, "Singer"));
        await AnswerAsync(services, Cy, zone, "UTC+1");
        await FindableAsync(services, Bea);
        await FindableAsync(services, Cy);

        var one = await RunAsync(services, Find(Ada, "Role", "Singer"));
        var two = await RunAsync(services, Call(
            Ada,
            (DiscordCommands.FindFieldOption, "Role"),
            (DiscordCommands.FindValueOption, "Singer"),
            (DiscordCommands.FindAlsoFieldOption, "Zone"),
            (DiscordCommands.FindAlsoValueOption, "UTC-5")));

        Assert.Equal(["Bea", "Cy"], Names(one));
        Assert.Equal(["Bea"], Names(two));
    }

    [Fact]
    public async Task AFieldWithoutItsValue_OrAnUnknownField_IsToldSo()
    {
        var (services, _, _) = await WorldAsync();
        await using var disposing = services;

        var half = await RunAsync(services, Call(
            Ada,
            (DiscordCommands.FindFieldOption, "Role"),
            (DiscordCommands.FindValueOption, "Singer"),
            (DiscordCommands.FindAlsoFieldOption, "Zone")));
        var unknown = await RunAsync(services, Find(Ada, "Shoe size", "9"));
        var unknownSecond = await RunAsync(services, Call(
            Ada,
            (DiscordCommands.FindFieldOption, "Role"),
            (DiscordCommands.FindValueOption, "Singer"),
            (DiscordCommands.FindAlsoFieldOption, "Shoe size"),
            (DiscordCommands.FindAlsoValueOption, "9")));

        Assert.Equal(FindCommand.HalfAPairMessage, half.Text);
        Assert.Equal(FindCommand.NoFieldMessage, unknown.Text);
        Assert.Equal(FindCommand.NoFieldMessage, unknownSecond.Text);
    }

    // ── The reply ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheReplyListsAtMost25_SaysHowManyMoreThereAre_AndHasAViewProfileButtonForEach()
    {
        var (services, role, _) = await WorldAsync();
        await using var disposing = services;

        for (var i = 0; i < 27; i++)
        {
            var id = (8000 + i).ToString(CultureInfo.InvariantCulture);
            await AddMemberAsync(services, id, $"Member {i:00}");
            await AnswerAsync(services, id, role, OptionOf(role, "Dancer"));
            await FindableAsync(services, id);
        }

        var reply = await RunAsync(services, Find(Ada, "Role", "Dancer"));

        var card = Assert.Single(reply.Embeds);
        Assert.Equal("Found 27", card.Title);
        var lines = card.Description!.Split('\n');
        Assert.Equal(26, lines.Length);
        Assert.Equal("1. Member 00", lines[0]);
        Assert.Equal("25. Member 24", lines[24]);
        Assert.Equal("and 2 more", lines[25]);
        Assert.Equal(25, reply.Actions!.Count);
        Assert.Equal("View profile 1", reply.Actions[0].Label);
        Assert.Equal(FindCommand.ViewButton + "8000", reply.Actions[0].Id);
        Assert.All(reply.Actions, a => Assert.True(a.Id.Length <= 100));
    }

    [Fact]
    public async Task OneResultHasAPlainViewProfileButton()
    {
        var (services, role, _) = await WorldAsync();
        await using var disposing = services;
        await AddMemberAsync(services, Bea, "Bea");
        await AnswerAsync(services, Bea, role, OptionOf(role, "Dancer"));
        await FindableAsync(services, Bea);

        var reply = await RunAsync(services, Find(Ada, "Role", "Dancer"));

        var button = Assert.Single(reply.Actions!);
        Assert.Equal("View profile", button.Label);
        Assert.Equal(FindCommand.ViewButton + Bea, button.Id);
    }

    [Fact]
    public async Task ViewProfileShowsTheSameCardAsProfileDoesForThatSearcher()
    {
        var (services, role, zone) = await WorldAsync();
        await using var disposing = services;
        await AddMemberAsync(services, Bea, "Bea");
        await AnswerAsync(services, Bea, role, OptionOf(role, "Dancer"));
        await AnswerAsync(services, Bea, zone, "UTC-5");
        await SetLevelAsync(services, Bea, zone, ProfileVisibility.Staff);
        await FindableAsync(services, Bea);

        DiscordReply? pressed = null;
        var press = new DiscordButtonPress(Ada, "kiri", FindCommand.ViewButton + Bea, (_, _) => Task.CompletedTask);

        using (var scope = services.Scope())
            pressed = await scope.ServiceProvider.GetRequiredService<FindCommand>().HandleButtonAsync(press, Ct);

        var profile = await RunAsync(services, new DiscordCommandCall(
            Ada,
            "kiri",
            DiscordCommands.Profile,
            new Dictionary<string, string> { [DiscordCommands.MemberOption] = Bea },
            (_, _) => Task.CompletedTask));

        var card = Assert.Single(pressed.Embeds);
        Assert.Equal(
            Assert.Single(profile.Embeds).Fields.Select(f => (f.Name, f.Value)),
            card.Fields.Select(f => (f.Name, f.Value)));
        Assert.Equal(Assert.Single(profile.Embeds).Title, card.Title);
        Assert.Equal("Bea", card.Title);
        Assert.Equal(["Role"], card.Fields.Select(f => f.Name));
    }

    [Fact]
    public async Task ViewProfileFollowsTheCommandsSwitch_AndRefusesAnId_ThatIsNotADiscordId()
    {
        await using var off = await ServicesAsync(on: false);
        using (var scope = off.Scope())
        {
            var reply = await scope.ServiceProvider.GetRequiredService<FindCommand>().HandleButtonAsync(
                new DiscordButtonPress(Ada, "kiri", FindCommand.ViewButton + Bea, (_, _) => Task.CompletedTask), Ct);
            Assert.Contains("turned off", reply.Text);
        }

        await using var on = await ServicesAsync();
        using (var scope = on.Scope())
        {
            var reply = await scope.ServiceProvider.GetRequiredService<FindCommand>().HandleButtonAsync(
                new DiscordButtonPress(Ada, "kiri", FindCommand.ViewButton + "12;drop", (_, _) => Task.CompletedTask), Ct);
            Assert.Equal("Modbot does not know that button.", reply.Text);
        }
    }

    // ── The switch, the limit and the record ────────────────────────────────────────────────

    [Fact]
    public async Task OffUntilTheOperatorSwitchesItOn()
    {
        await using var services = await ServicesAsync(on: false);
        await AddFieldsAsync(services);

        using var scope = services.Scope();
        var reply = await scope.ServiceProvider.GetRequiredService<DiscordCommandHandler>().RunAsync(Find(Ada, "Role", "Singer"), Ct);

        Assert.Equal("/find is turned off on this server.", reply?.Text);
    }

    [Fact]
    public async Task WithNoFieldsSetUpItSaysSo()
    {
        await using var services = await ServicesAsync();

        var reply = await RunAsync(services, Find(Ada, "Role", "Singer"));

        Assert.Equal(ProfileCommand.NoFieldsMessage, reply.Text);
    }

    [Fact]
    public async Task AMemberOverTheLimitIsToldToSlowDown_AndTheRefusedCallIsNotRecorded()
    {
        var (services, _, _) = await WorldAsync();
        await using var disposing = services;

        for (var i = 0; i < MemberCommandLimits.PerMinute; i++)
            await RunAsync(services, Find(Ada, "Role", "Singer"));

        var reply = await RunAsync(services, Find(Ada, "Role", "Singer"));
        var facts = await services.FactsOfTypeAsync(FactType.DiscordCommandRun, Ct);

        Assert.Equal(FindCommand.TooFastMessage, reply.Text);
        Assert.Equal(MemberCommandLimits.PerMinute, facts.Count);
    }

    [Fact]
    public async Task ASearchIsRecordedLikeEveryCommand_WithoutTheWordsSearchedOrWhoWasFound()
    {
        var (services, role, _) = await WorldAsync();
        await using var disposing = services;
        await AddMemberAsync(services, Bea, "Bea");
        await AnswerAsync(services, Bea, role, OptionOf(role, "Dancer"));
        await FindableAsync(services, Bea);

        await RunAsync(services, Find(Ada, "Role", "Dancer"));

        var fact = Assert.Single(await services.FactsOfTypeAsync(FactType.DiscordCommandRun, Ct));
        Assert.Equal(Ada, fact.SubjectId);
        Assert.Null(fact.ActorId);

        var data = await LastCommandFactAsync(services);
        Assert.Equal("find", data.GetProperty("command").GetString());
        Assert.Equal("answered", data.GetProperty("outcome").GetString());
        Assert.DoesNotContain("Dancer", fact.Data);
        Assert.DoesNotContain(Bea, fact.Data);
        Assert.DoesNotContain("Bea", fact.Data);
    }

    // ── Suggestions ─────────────────────────────────────────────────────────────────────────

    private static async Task<IReadOnlyList<DiscordSuggestion>> SuggestAsync(
        TestServices services, string option, string typed, params (string Option, string Value)[] others)
    {
        using var scope = services.Scope();
        return await scope.ServiceProvider.GetRequiredService<DiscordCommandHandler>().SuggestAsync(
            new DiscordSuggestionAsk(
                Ada,
                DiscordCommands.Find,
                option,
                typed,
                (_, _) => Task.CompletedTask,
                options: others.ToDictionary(o => o.Option, o => o.Value)),
            Ct);
    }

    [Fact]
    public async Task FieldsAreSuggestedByName_AndAPickListsOptionsByLabel_ForBothPairs()
    {
        var (services, _, _) = await WorldAsync();
        await using var disposing = services;

        Assert.Equal(["Role", "Zone"], (await SuggestAsync(services, DiscordCommands.FindFieldOption, string.Empty)).Select(s => s.Value));
        Assert.Equal(["Zone"], (await SuggestAsync(services, DiscordCommands.FindAlsoFieldOption, "zo")).Select(s => s.Value));
        Assert.Equal(
            ["Singer", "Singer-songwriter"],
            (await SuggestAsync(services, DiscordCommands.FindValueOption, "sing", (DiscordCommands.FindFieldOption, "Role"))).Select(s => s.Name));
        Assert.Equal(
            ["Dancer"],
            (await SuggestAsync(services, DiscordCommands.FindAlsoValueOption, "dan", (DiscordCommands.FindAlsoFieldOption, "role"))).Select(s => s.Value));

        // A text field, a field not yet picked, or the wrong pair's field suggests no value.
        Assert.Empty(await SuggestAsync(services, DiscordCommands.FindValueOption, "utc", (DiscordCommands.FindFieldOption, "Zone")));
        Assert.Empty(await SuggestAsync(services, DiscordCommands.FindValueOption, "sing"));
        Assert.Empty(await SuggestAsync(services, DiscordCommands.FindValueOption, "sing", (DiscordCommands.FindAlsoFieldOption, "Role")));
    }

    [Fact]
    public async Task NothingIsSuggestedWhileTheCommandIsOff_AndNothingIsRecorded()
    {
        await using var services = await ServicesAsync(on: false);
        await AddFieldsAsync(services);

        Assert.Empty(await SuggestAsync(services, DiscordCommands.FindFieldOption, string.Empty));
        Assert.Empty(await services.FactsOfTypeAsync(FactType.DiscordCommandRun, Ct));
    }
}
