using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Configuration;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Profiles;
using Modbot.Discord.Commands;
using Modbot.Discord.Gateway;
using Modbot.TestSupport;

namespace Modbot.Discord.Tests.Commands;

/// <summary>
/// <c>/profile</c> (member profiles design §4): a member's own card with Edit and Who sees what,
/// another member's card cut down to what the viewer may read, staff seeing all, and the forms.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class ProfileCommandTests
{
    private const string Guild = "424242";
    private const string Ada = "7001";
    private const string Bea = "7002";
    private const string Staff = "7003";

    private readonly PostgresFixture _db;

    public ProfileCommandTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<TestServices> ServicesAsync(bool on = true)
    {
        var services = await TestServices.CreateAsync(_db, Ct);
        await services.ConfigureAsync(s =>
        {
            s.SwitchCommand("profile", on);
            s.DiscordGuildId = Guild;
        }, Ct);
        return services;
    }

    private static DiscordCommandCall Call(string who, string? member = null)
        => new(
            who,
            "kiri",
            DiscordCommands.Profile,
            member is null ? new Dictionary<string, string>() : new Dictionary<string, string> { [DiscordCommands.MemberOption] = member },
            (_, _) => Task.CompletedTask);

    private static async Task<DiscordReply> RunAsync(TestServices services, DiscordCommandCall call)
    {
        using var scope = services.Scope();
        return await scope.ServiceProvider.GetRequiredService<DiscordCommandHandler>().HandleAsync(call, Ct);
    }

    private static async Task<(DiscordReply? Reply, DiscordForm? Form)> PressAsync(TestServices services, string who, string buttonId)
    {
        DiscordForm? shown = null;
        var press = new DiscordButtonPress(
            who,
            "kiri",
            buttonId,
            (_, _) => Task.CompletedTask,
            (form, _) =>
            {
                shown = form;
                return Task.CompletedTask;
            });

        using var scope = services.Scope();
        var reply = await scope.ServiceProvider.GetRequiredService<ProfileCommand>().HandleButtonAsync(press, Ct);
        return (reply, shown);
    }

    private static async Task<DiscordReply> SubmitAsync(
        TestServices services, string who, string formId, Dictionary<string, IReadOnlyList<string>> values)
    {
        using var scope = services.Scope();
        return await scope.ServiceProvider.GetRequiredService<ProfileCommand>().HandleFormAsync(
            new DiscordFormSubmit(who, "kiri", formId, values, (_, _) => Task.CompletedTask), Ct);
    }

    private static async Task AddMemberAsync(TestServices services, string userId, string name, bool isBot = false)
    {
        await using var db = services.Database.NewContext();
        db.DiscordMembers.Add(new DiscordMember
        {
            GuildId = Guild,
            UserId = userId,
            Username = name.ToLowerInvariant(),
            DisplayName = name,
            IsBot = isBot,
            FirstSeenAt = services.Clock.UtcNow,
            UpdatedAt = services.Clock.UtcNow,
        });
        await db.SaveChangesAsync(Ct);
    }

    /// <summary>Adds text fields; the first is a pick-list. Returns their ids in order.</summary>
    private static async Task<List<ProfileField>> AddFieldsAsync(TestServices services, int count, ProfileVisibility level = ProfileVisibility.Staff)
    {
        var at = services.Clock.UtcNow;
        var fields = new List<ProfileField>();

        for (var i = 0; i < count; i++)
        {
            fields.Add(new ProfileField
            {
                Name = i == 0 ? "Pronouns" : $"Field {i + 1}",
                Kind = i == 0 ? ProfileFieldKind.PickList : ProfileFieldKind.Text,
                DefaultVisibility = level,
                SortOrder = i,
                CreatedAt = at,
                UpdatedAt = at,
                Options = i == 0
                    ? [new ProfileOption { Label = "she/her", SortOrder = 0 }, new ProfileOption { Label = "he/him", SortOrder = 1 }]
                    : [],
            });
        }

        await using var db = services.Database.NewContext();
        db.ProfileFields.AddRange(fields);
        await db.SaveChangesAsync(Ct);
        return fields;
    }

    private static async Task AnswerAsync(TestServices services, string userId, ProfileField field, string answer)
    {
        await using var db = services.Database.NewContext();
        await MemberProfiles.SetAnswersAsync(db, userId, new Dictionary<Guid, string?> { [field.Id] = answer }, services.Clock.UtcNow, Ct);
    }

    private static string KeyOf(ProfileField field) => field.Id.ToString("N");

    [Fact]
    public async Task TheCommandIsOffUntilTheOperatorSwitchesItOn()
    {
        await using var services = await ServicesAsync(on: false);
        await AddFieldsAsync(services, 1);

        // The switch is checked by RunAsync, which is what the bot calls; HandleAsync skips it.
        using var scope = services.Scope();
        var reply = await scope.ServiceProvider.GetRequiredService<DiscordCommandHandler>().RunAsync(Call(Ada), Ct);

        Assert.NotNull(reply);
        Assert.Empty(reply.Embeds);
        Assert.False(string.IsNullOrWhiteSpace(reply.Text));
    }

    [Fact]
    public async Task AMembersOwnCardShowsEveryFieldWithTheLevelAndTheTwoButtons()
    {
        await using var services = await ServicesAsync();
        var fields = await AddFieldsAsync(services, 2);
        await AddMemberAsync(services, Ada, "Ada");
        await AnswerAsync(services, Ada, fields[1], "Pottery");

        var reply = await RunAsync(services, Call(Ada));

        var card = Assert.Single(reply.Embeds);
        Assert.Equal("Ada", card.Title);
        Assert.Equal("Not set", card.Fields.Single(f => f.Name == "Pronouns").Value);
        Assert.Contains("Pottery", card.Fields.Single(f => f.Name == "Field 2").Value);
        Assert.Contains("Staff", card.Fields.Single(f => f.Name == "Field 2").Value);
        Assert.Equal(["Edit", "Who sees what", "Findable: Off", "Spotlight: Off"], reply.Actions!.Select(a => a.Label));
    }

    [Fact]
    public async Task TheSpotlightButtonFlipsTheSwitchAndRewritesTheCardWithTheNewState()
    {
        await using var services = await ServicesAsync();
        await AddFieldsAsync(services, 1);
        await AddMemberAsync(services, Ada, "Ada");

        for (var times = 1; times <= 2; times++)
        {
            DiscordReply? updated = null;
            var press = new DiscordButtonPress(
                Ada,
                "kiri",
                ProfileCommand.SpotlightButton,
                (_, _) => Task.CompletedTask,
                update: (reply, _) =>
                {
                    updated = reply;
                    return Task.CompletedTask;
                });

            using (var scope = services.Scope())
                Assert.Null(await scope.ServiceProvider.GetRequiredService<ProfileCommand>().HandleButtonAsync(press, Ct));

            Assert.Equal(times == 1 ? "Spotlight: On" : "Spotlight: Off", updated!.Actions!.Last().Label);

            await using var db = services.Database.NewContext();
            Assert.Equal(times == 1, await MemberProfiles.IsSpotlightAsync(db, Ada, Ct));
        }
    }

    [Fact]
    public async Task OnlyTheMemberWhoPressedSpotlightChangesTheirOwnSwitch_AndFindableIsLeftAlone()
    {
        await using var services = await ServicesAsync();
        await AddFieldsAsync(services, 1);
        await AddMemberAsync(services, Ada, "Ada");
        await AddMemberAsync(services, Bea, "Bea");

        await PressAsync(services, Ada, ProfileCommand.SpotlightButton);

        await using var db = services.Database.NewContext();
        Assert.True(await MemberProfiles.IsSpotlightAsync(db, Ada, Ct));
        Assert.False(await MemberProfiles.IsSpotlightAsync(db, Bea, Ct));
        Assert.False(await MemberProfiles.IsFindableAsync(db, Ada, Ct));
    }

    [Fact]
    public async Task TheFindableButtonFlipsTheSwitchAndRewritesTheCardWithTheNewState()
    {
        await using var services = await ServicesAsync();
        await AddFieldsAsync(services, 1);
        await AddMemberAsync(services, Ada, "Ada");

        for (var times = 1; times <= 2; times++)
        {
            DiscordReply? updated = null;
            var press = new DiscordButtonPress(
                Ada,
                "kiri",
                ProfileCommand.FindableButton,
                (_, _) => Task.CompletedTask,
                update: (reply, _) =>
                {
                    updated = reply;
                    return Task.CompletedTask;
                });

            using (var scope = services.Scope())
                Assert.Null(await scope.ServiceProvider.GetRequiredService<ProfileCommand>().HandleButtonAsync(press, Ct));

            Assert.Equal(times == 1 ? "Findable: On" : "Findable: Off", updated!.Actions!.Single(a => a.Id == ProfileCommand.FindableButton).Label);

            await using var db = services.Database.NewContext();
            Assert.Equal(times == 1, await MemberProfiles.IsFindableAsync(db, Ada, Ct));
        }
    }

    [Fact]
    public async Task OnlyTheMemberWhoPressedFindableChangesTheirOwnSwitch()
    {
        await using var services = await ServicesAsync();
        await AddFieldsAsync(services, 1);
        await AddMemberAsync(services, Ada, "Ada");
        await AddMemberAsync(services, Bea, "Bea");

        await PressAsync(services, Ada, ProfileCommand.FindableButton);

        await using var db = services.Database.NewContext();
        Assert.True(await MemberProfiles.IsFindableAsync(db, Ada, Ct));
        Assert.False(await MemberProfiles.IsFindableAsync(db, Bea, Ct));
    }

    [Fact]
    public async Task WithNoFieldsSetUpTheCardSaysSo()
    {
        await using var services = await ServicesAsync();

        var reply = await RunAsync(services, Call(Ada));

        Assert.Equal(ProfileCommand.NoFieldsMessage, reply.Text);
    }

    [Fact]
    public async Task AnotherMembersCardShowsOnlyWhatWasOpenedToMembers()
    {
        await using var services = await ServicesAsync();
        var fields = await AddFieldsAsync(services, 3);
        await AddMemberAsync(services, Bea, "Bea");
        await AnswerAsync(services, Bea, fields[1], "Pottery");
        await AnswerAsync(services, Bea, fields[2], "Chess");

        await using (var db = services.Database.NewContext())
        {
            await MemberProfiles.SetVisibilityAsync(db, Bea, new Dictionary<Guid, ProfileVisibility> { [fields[2].Id] = ProfileVisibility.Members }, services.Clock.UtcNow, Ct);
        }

        var reply = await RunAsync(services, Call(Ada, Bea));

        var card = Assert.Single(reply.Embeds);
        Assert.Equal(["Field 3"], card.Fields.Select(f => f.Name));
        Assert.Equal("Chess", card.Fields[0].Value);
    }

    [Fact]
    public async Task StaffSeeEveryAnswerOnAnotherMembersCardEvenOnesKeptToTheMember()
    {
        await using var services = await ServicesAsync();
        var fields = await AddFieldsAsync(services, 2, ProfileVisibility.OnlyMe);
        await AddMemberAsync(services, Bea, "Bea");
        await AnswerAsync(services, Bea, fields[1], "Pottery");
        await services.LinkedAccountAsync(Staff, ModbotPermissions.ViewMembers, ct: Ct);

        var reply = await RunAsync(services, Call(Staff, Bea));

        var card = Assert.Single(reply.Embeds);
        Assert.Equal(["Field 2"], card.Fields.Select(f => f.Name));
        Assert.Contains("Pottery", card.Fields[0].Value);
        Assert.Contains("Only me", card.Fields[0].Value);
    }

    [Fact]
    public async Task AModbotAccountWithoutSeeMembersIsNotStaffHere()
    {
        await using var services = await ServicesAsync();
        var fields = await AddFieldsAsync(services, 2, ProfileVisibility.Staff);
        await AddMemberAsync(services, Bea, "Bea");
        await AnswerAsync(services, Bea, fields[1], "Pottery");
        await services.LinkedAccountAsync(Staff, ModbotPermissions.ViewCalendar, ct: Ct);

        var reply = await RunAsync(services, Call(Staff, Bea));

        Assert.Empty(reply.Embeds);
        Assert.Contains("nothing to show", reply.Text);
    }

    [Fact]
    public async Task AnEmptyProfileHasNothingToShowToOthers()
    {
        await using var services = await ServicesAsync();
        await AddFieldsAsync(services, 2);
        await AddMemberAsync(services, Bea, "Bea");

        var reply = await RunAsync(services, Call(Ada, Bea));

        Assert.Equal("Bea has nothing to show.", reply.Text);
    }

    [Fact]
    public async Task EditOpensOneFormWithASelectForAPickListAndABoxForText()
    {
        await using var services = await ServicesAsync();
        await AddFieldsAsync(services, 2);

        var (reply, form) = await PressAsync(services, Ada, ProfileCommand.EditButton);

        Assert.Null(reply);
        Assert.NotNull(form);
        Assert.Equal(DiscordFormFieldKind.Choice, form.Fields[0].Kind);
        Assert.Equal(["she/her", "he/him"], form.Fields[0].Choices!.Select(c => c.Label));
        Assert.Equal(DiscordFormFieldKind.LongText, form.Fields[1].Kind);
        Assert.All(form.Fields, f => Assert.False(f.Required));
    }

    [Fact]
    public async Task WhoSeesWhatOpensAFormWithTheLevelInForceSelected()
    {
        await using var services = await ServicesAsync();
        await AddFieldsAsync(services, 2, ProfileVisibility.Members);

        var (_, form) = await PressAsync(services, Ada, ProfileCommand.SeeButton);

        Assert.NotNull(form);
        Assert.All(form.Fields, f => Assert.Equal("Members", f.Choices!.Single(c => c.IsDefault).Label));
    }

    [Fact]
    public async Task MoreThanFiveFieldsAskWhichFieldsFirst()
    {
        await using var services = await ServicesAsync();
        await AddFieldsAsync(services, 7);

        var (reply, form) = await PressAsync(services, Ada, ProfileCommand.EditButton);

        Assert.Null(form);
        Assert.Equal("Which fields?", reply!.Text);
        Assert.Equal(["Fields 1 to 5", "Fields 6 to 7"], reply.Actions!.Select(a => a.Label));

        var (_, second) = await PressAsync(services, Ada, reply.Actions![1].Id);

        Assert.Equal(2, second!.Fields.Count);
        Assert.Equal("Field 6", second.Fields[0].Label);
    }

    [Fact]
    public async Task SavingTheFormStoresTheAnswersAndClearingABoxRemovesIt()
    {
        await using var services = await ServicesAsync();
        var fields = await AddFieldsAsync(services, 2);
        var option = fields[0].Options.First(o => o.Label == "he/him");
        var (_, form) = await PressAsync(services, Ada, ProfileCommand.EditButton);

        var saved = await SubmitAsync(services, Ada, form!.Id, new Dictionary<string, IReadOnlyList<string>>
        {
            [KeyOf(fields[0])] = [option.Id.ToString()],
            [KeyOf(fields[1])] = ["Pottery"],
        });

        var card = Assert.Single(saved.Embeds);
        Assert.Contains("he/him", card.Fields[0].Value);
        Assert.Contains("Pottery", card.Fields[1].Value);

        var cleared = await SubmitAsync(services, Ada, form.Id, new Dictionary<string, IReadOnlyList<string>>
        {
            [KeyOf(fields[0])] = [],
            [KeyOf(fields[1])] = [""],
        });

        Assert.Equal(["Not set", "Not set"], Assert.Single(cleared.Embeds).Fields.Select(f => f.Value));

        await using var db = services.Database.NewContext();
        Assert.Empty(await db.ProfileValues.AsNoTracking().Where(v => v.DiscordUserId == Ada).ToListAsync(Ct));
    }

    [Fact]
    public async Task TheLevelsFormChangesOnlyTheCallersOwnProfile()
    {
        await using var services = await ServicesAsync();
        var fields = await AddFieldsAsync(services, 2);
        await AddMemberAsync(services, Ada, "Ada");
        await AddMemberAsync(services, Bea, "Bea");
        await AnswerAsync(services, Ada, fields[1], "Pottery");
        await AnswerAsync(services, Bea, fields[1], "Chess");
        var (_, form) = await PressAsync(services, Ada, ProfileCommand.SeeButton);

        await SubmitAsync(services, Ada, form!.Id, new Dictionary<string, IReadOnlyList<string>>
        {
            [KeyOf(fields[1])] = ["members"],
        });

        var ada = Assert.Single((await RunAsync(services, Call(Bea, Ada))).Embeds);
        Assert.Equal("Pottery", ada.Fields.Single().Value);

        var bea = await RunAsync(services, Call(Ada, Bea));
        Assert.Empty(bea.Embeds);
    }

    [Fact]
    public async Task AFormForFieldsThatWereRemovedSaysSo()
    {
        await using var services = await ServicesAsync();
        await AddFieldsAsync(services, 7);
        var (pages, _) = await PressAsync(services, Ada, ProfileCommand.EditButton);

        await using (var db = services.Database.NewContext())
        {
            db.ProfileFields.RemoveRange(await db.ProfileFields.Where(f => f.SortOrder >= 5).ToListAsync(Ct));
            await db.SaveChangesAsync(Ct);
        }

        var (reply, form) = await PressAsync(services, Ada, pages!.Actions![1].Id);

        Assert.Null(form);
        Assert.Equal(ProfileCommand.FieldsChangedMessage, reply!.Text);
    }
}
