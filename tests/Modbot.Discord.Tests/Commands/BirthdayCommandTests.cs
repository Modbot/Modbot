using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Discord.Commands;
using Modbot.Discord.Gateway;
using Modbot.Discord.Tests.Fakes;
using Modbot.TestSupport;

namespace Modbot.Discord.Tests.Commands;

/// <summary>
/// <c>/birthday</c>: a member sets, shows and removes their own birthday, a month and a day and never a
/// year; a date that does not exist is refused; removing it takes the birthday role off at once, or
/// leaves it to the next pass when Discord cannot be asked.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class BirthdayCommandTests
{
    private const string Guild = "424242";
    private const string Ada = "7001";
    private const string Bea = "7002";
    private const string Role = "8001";

    private readonly PostgresFixture _db;

    public BirthdayCommandTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<TestServices> ServicesAsync(bool on = true)
    {
        var services = await TestServices.CreateAsync(_db, Ct);
        await services.ConfigureAsync(s =>
        {
            s.SwitchCommand("birthday", on);
            s.DiscordGuildId = Guild;
        }, Ct);
        return services;
    }

    private static DiscordCommandCall Call(string who, string subcommand, params (string Option, string Value)[] options)
        => new(
            who,
            "kiri",
            DiscordCommands.Birthday,
            options.ToDictionary(o => o.Option, o => o.Value),
            (_, _) => Task.CompletedTask)
        {
            Subcommand = subcommand,
        };

    private static DiscordCommandCall Set(string who, string month, string day)
        => Call(who, DiscordCommands.BirthdaySet, (DiscordCommands.BirthdayMonthOption, month), (DiscordCommands.BirthdayDayOption, day));

    private static async Task<DiscordReply> RunAsync(TestServices services, DiscordCommandCall call, FakeGateway? gateway = null)
    {
        using var scope = services.Scope();
        return await scope.ServiceProvider.GetRequiredService<DiscordCommandHandler>().HandleAsync(call, Ct, gateway);
    }

    private static async Task<List<MemberBirthday>> RowsAsync(TestServices services)
    {
        await using var db = services.Database.NewContext();
        return await db.MemberBirthdays.AsNoTracking().OrderBy(b => b.DiscordUserId).ToListAsync(Ct);
    }

    [Fact]
    public void TheCommand_IsForEveryone_AndOffUntilTheOperatorTurnsItOn()
    {
        Assert.True(DiscordCommands.IsForEveryone(DiscordCommands.Birthday));
        Assert.False(DiscordCommandSwitches.Find("birthday")!.OnByDefault);
    }

    [Fact]
    public async Task Set_KeepsAMonthAndADay_AndShowSaysThemBack()
    {
        await using var services = await ServicesAsync();

        var reply = await RunAsync(services, Set(Ada, "3", "14"));
        Assert.Equal("Your birthday is set to 14 March.", reply.Text);

        var row = Assert.Single(await RowsAsync(services));
        Assert.Equal((Ada, 3, 14), (row.DiscordUserId, row.Month, row.Day));
        Assert.Equal(services.Clock.UtcNow, row.SetAt);

        var shown = await RunAsync(services, Call(Ada, DiscordCommands.BirthdayShow));
        Assert.Equal("Your birthday is 14 March.", shown.Text);
    }

    [Fact]
    public async Task Set_AgainChangesTheDate_InTheSameRow()
    {
        await using var services = await ServicesAsync();

        await RunAsync(services, Set(Ada, "3", "14"));
        var reply = await RunAsync(services, Set(Ada, "11", "2"));

        Assert.Equal("Your birthday is set to 2 November.", reply.Text);
        var row = Assert.Single(await RowsAsync(services));
        Assert.Equal((11, 2), (row.Month, row.Day));
    }

    [Fact]
    public async Task Set_29February_IsAllowed()
    {
        await using var services = await ServicesAsync();

        var reply = await RunAsync(services, Set(Ada, "2", "29"));

        Assert.Equal("Your birthday is set to 29 February.", reply.Text);
    }

    [Theory]
    [InlineData("2", "30")]
    [InlineData("4", "31")]
    [InlineData("13", "1")]
    [InlineData("0", "5")]
    [InlineData("5", "0")]
    [InlineData("march", "14")]
    [InlineData("3", "-1")]
    [InlineData("3", "")]
    public async Task Set_ADateThatDoesNotExist_IsRefused_AndNothingIsKept(string month, string day)
    {
        await using var services = await ServicesAsync();

        var reply = await RunAsync(services, Set(Ada, month, day));

        Assert.Equal(BirthdayCommand.NotADateMessage, reply.Text);
        Assert.Empty(await RowsAsync(services));
    }

    [Fact]
    public async Task Set_WithNoMonthOrDay_IsRefused()
    {
        await using var services = await ServicesAsync();

        var reply = await RunAsync(services, Call(Ada, DiscordCommands.BirthdaySet));

        Assert.Equal(BirthdayCommand.NotADateMessage, reply.Text);
        Assert.Empty(await RowsAsync(services));
    }

    [Fact]
    public async Task Show_WithNothingSet_SaysSo()
    {
        await using var services = await ServicesAsync();

        var reply = await RunAsync(services, Call(Ada, DiscordCommands.BirthdayShow));

        Assert.Equal(BirthdayCommand.NoneMessage, reply.Text);
    }

    [Fact]
    public async Task Remove_DeletesTheRow_AndLeavesEverybodyElsesAlone()
    {
        await using var services = await ServicesAsync();
        await RunAsync(services, Set(Ada, "3", "14"));
        await RunAsync(services, Set(Bea, "5", "6"));

        var reply = await RunAsync(services, Call(Ada, DiscordCommands.BirthdayRemove));

        Assert.Equal(BirthdayCommand.RemovedMessage, reply.Text);
        Assert.Equal(Bea, Assert.Single(await RowsAsync(services)).DiscordUserId);

        var again = await RunAsync(services, Call(Ada, DiscordCommands.BirthdayRemove));
        Assert.Equal(BirthdayCommand.NothingToRemoveMessage, again.Text);
    }

    [Fact]
    public async Task Remove_WhileTheRoleIsHeld_TakesItOffAtOnce_ThroughTheBotsSession()
    {
        await using var services = await ServicesAsync();
        await RunAsync(services, Set(Ada, "3", "14"));
        await HoldRoleAsync(services, Ada);
        var gateway = new FakeGateway();

        var reply = await RunAsync(services, Call(Ada, DiscordCommands.BirthdayRemove), gateway);

        Assert.Equal(BirthdayCommand.RemovedMessage, reply.Text);
        Assert.Equal((false, Guild, Ada, Role), Assert.Single(gateway.RoleChanges));
        Assert.Empty(await RowsAsync(services));
    }

    [Fact]
    public async Task Remove_WhileTheRoleIsHeld_WithNoSession_ForgetsTheDateButKeepsTheRoleForTheNextPass()
    {
        await using var services = await ServicesAsync();
        await RunAsync(services, Set(Ada, "3", "14"));
        await HoldRoleAsync(services, Ada);

        var reply = await RunAsync(services, Call(Ada, DiscordCommands.BirthdayRemove));

        Assert.Equal(BirthdayCommand.RemovedMessage, reply.Text);
        var row = Assert.Single(await RowsAsync(services));
        Assert.True(row.IsForgotten);
        Assert.Equal(Role, row.RoleGivenId);

        // To the member it is gone.
        var shown = await RunAsync(services, Call(Ada, DiscordCommands.BirthdayShow));
        Assert.Equal(BirthdayCommand.NoneMessage, shown.Text);
        var removed = await RunAsync(services, Call(Ada, DiscordCommands.BirthdayRemove));
        Assert.Equal(BirthdayCommand.NothingToRemoveMessage, removed.Text);
    }

    [Fact]
    public async Task Remove_WhenDiscordRefusesToTakeTheRole_KeepsTheRoleForTheNextPass()
    {
        await using var services = await ServicesAsync();
        await RunAsync(services, Set(Ada, "3", "14"));
        await HoldRoleAsync(services, Ada);
        var gateway = new FakeGateway { RoleError = "Missing permissions." };

        await RunAsync(services, Call(Ada, DiscordCommands.BirthdayRemove), gateway);

        var row = Assert.Single(await RowsAsync(services));
        Assert.True(row.IsForgotten);
        Assert.Equal(Role, row.RoleGivenId);
    }

    [Fact]
    public async Task TooManyTriesInAMinute_AreRefused()
    {
        await using var services = await ServicesAsync();

        for (var i = 0; i < MemberCommandLimits.PerMinute; i++)
            await RunAsync(services, Call(Ada, DiscordCommands.BirthdayShow));

        var reply = await RunAsync(services, Call(Ada, DiscordCommands.BirthdayShow));

        Assert.Equal(BirthdayCommand.TooFastMessage, reply.Text);
    }

    [Fact]
    public async Task WhenSwitchedOff_TheCommandIsRefused_AndNothingIsKept()
    {
        await using var services = await ServicesAsync(on: false);

        using var scope = services.Scope();
        var reply = await scope.ServiceProvider.GetRequiredService<DiscordCommandHandler>().RunAsync(Set(Ada, "3", "14"), Ct);

        Assert.Equal("/birthday is turned off on this server.", reply?.Text);
        Assert.Empty(await RowsAsync(services));
    }

    private static async Task HoldRoleAsync(TestServices services, string userId)
    {
        await using var db = services.Database.NewContext();
        var row = await db.MemberBirthdays.SingleAsync(b => b.DiscordUserId == userId, Ct);
        row.RoleGivenId = Role;
        row.RoleGivenOn = DateOnly.FromDateTime(services.Clock.UtcNow.UtcDateTime);
        await db.SaveChangesAsync(Ct);
    }
}
