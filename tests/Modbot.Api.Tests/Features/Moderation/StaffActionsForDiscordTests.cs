using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Tests.Fakes;
using Modbot.Api.Tests.Features.Audit;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.TestSupport;

using VRChatGroupMember = VRChat.API.Model.GroupMember;
using VRChatSuccess = VRChat.API.Model.Success;

namespace Modbot.Api.Tests.Features.Moderation;

/// <summary>
/// The bot's way into the moderation and note services (acting from Discord design §5): the same
/// rules, the same facts and the same case file as the web app's endpoints, and one key acting once.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class StaffActionsForDiscordTests
{
    private const string Group = "grp_1";
    private const string Person = "usr_troublemaker";
    private const string ModbotAccount = "usr_modbot";

    private static readonly DateTimeOffset Day = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _db;

    public StaffActionsForDiscordTests(PostgresFixture db) => _db = db;

    private static FakeVRChatGate Accepting()
        => new FakeVRChatGate()
            .SignedInAs()
            .Returns("KickGroupMember", new VRChatSuccess())
            .Returns("BanGroupMember", new VRChatGroupMember())
            .Returns("UnbanGroupMember", new VRChatGroupMember());

    private static async Task<StaffMember> SeedAsync(ReadSurfaceTestHost host, ModbotPermissions held, CancellationToken ct)
    {
        host.Clock.UtcNow = Day;

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
            var settings = await db.GetSettingsAsync(ct);
            settings.ManagedGroupId = Group;
            settings.VRChatSessionUserId = ModbotAccount;
            await db.SaveChangesAsync(ct);
        }

        var user = await host.CreateUserAsync($"u_{Guid.NewGuid():N}", "hunter2", held, ct);
        return new StaffMember(user.Id, user.Username, held);
    }

    private static async Task<T> InScopeAsync<T>(ReadSurfaceTestHost host, Func<IStaffActions, Task<T>> work)
    {
        using var scope = host.Services.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<IStaffActions>());
    }

    private static async Task<List<ModbotEvent>> FactsAboutAsync(ReadSurfaceTestHost host, string userId, CancellationToken ct)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        return await db.Events.AsNoTracking().Where(e => e.SubjectId == userId).OrderBy(e => e.Id).ToListAsync(ct);
    }

    [Fact]
    public async Task ABanFromDiscord_IsTheWebAppsBan_AndOneKeyActsOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db, Accepting());
        await host.ResetAsync(ct);
        var by = await SeedAsync(host, ModbotPermissions.Ban, ct);

        var reasons = await InScopeAsync(host, s => s.ReasonsAsync("ban", ct));
        var harassment = reasons.Reasons.First(r => r.Label == "Harassment").Id;

        var first = await InScopeAsync(host, s => s.RunAsync("ban", "discord:abc", Person, [harassment], "Slurs in voice.", by, ct));
        var second = await InScopeAsync(host, s => s.RunAsync("ban", "discord:abc", Person, [harassment], "Slurs in voice.", by, ct));

        Assert.True(first.Done);
        Assert.NotNull(first.CaseId);
        Assert.True(second.Repeat);

        var fact = Assert.Single(await FactsAboutAsync(host, Person, ct), e => e.Type == FactType.ActionBan);
        Assert.Equal(by.UserId.ToString(), fact.ActorId);
        Assert.Equal(FactPlatform.Modbot, fact.ActorPlatform);
        Assert.Equal("Harassment", JsonDocument.Parse(fact.Data).RootElement.GetProperty("reasonLabels")[0].GetString());

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        var caseFile = await db.CaseFiles.AsNoTracking().SingleAsync(ct);
        Assert.Equal(fact.Id, caseFile.BanFactId);
        Assert.Equal(by.UserId, caseFile.AuthorUserId);
    }

    [Fact]
    public async Task WithoutThePermission_NothingIsSent()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db, Accepting());
        await host.ResetAsync(ct);
        var by = await SeedAsync(host, ModbotPermissions.Kick, ct);

        var answer = await InScopeAsync(host, s => s.RunAsync("ban", "discord:x", Person, [], string.Empty, by, ct));

        Assert.False(answer.Done);
        Assert.True(answer.Refused);
        Assert.Empty(await FactsAboutAsync(host, Person, ct));
    }

    [Fact]
    public async Task TheCheck_RefusesWhatTheServiceWouldRefuse_AndSendsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db, Accepting());
        await host.ResetAsync(ct);
        var by = await SeedAsync(host, ModbotPermissions.Ban | ModbotPermissions.Kick, ct);

        // A ban always needs a reason.
        Assert.Equal("Pick at least one reason.", await InScopeAsync(host, s => s.CheckAsync("ban", Person, [], string.Empty, by, ct)));

        // Never on the account Modbot signs in as.
        Assert.Equal(
            "That is the account Modbot signs in as. Modbot will not act on itself.",
            await InScopeAsync(host, s => s.CheckAsync("kick", ModbotAccount, [], string.Empty, by, ct)));

        // "Other" needs a note.
        var reasons = await InScopeAsync(host, s => s.ReasonsAsync("ban", ct));
        var other = reasons.Reasons.First(r => r.NeedsNote).Id;
        Assert.Contains("needs a note", await InScopeAsync(host, s => s.CheckAsync("ban", Person, [other], string.Empty, by, ct)), StringComparison.Ordinal);

        // Fine, and still nothing was sent or written.
        Assert.Null(await InScopeAsync(host, s => s.CheckAsync("kick", Person, [], string.Empty, by, ct)));
        Assert.Empty(await FactsAboutAsync(host, Person, ct));
    }

    [Fact]
    public async Task TheReasons_AreTheOnesMarkedForTheAction_AndTheSwitchDecidesWhenOneIsNeeded()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db, Accepting());
        await host.ResetAsync(ct);
        await SeedAsync(host, ModbotPermissions.Ban, ct);

        var ban = await InScopeAsync(host, s => s.ReasonsAsync("ban", ct));
        Assert.True(ban.Required);
        Assert.DoesNotContain(ban.Reasons, r => r.Label == "Mistake");

        var kick = await InScopeAsync(host, s => s.ReasonsAsync("kick", ct));
        Assert.False(kick.Required);

        Assert.Empty((await InScopeAsync(host, s => s.ReasonsAsync("approve", ct))).Reasons);

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
            (await db.GetSettingsAsync(ct)).RequireModerationClassification = true;
            await db.SaveChangesAsync(ct);
        }

        Assert.True((await InScopeAsync(host, s => s.ReasonsAsync("kick", ct))).Required);
    }

    [Fact]
    public async Task ANoteFromDiscord_IsTheSameNoteFact()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db, Accepting());
        await host.ResetAsync(ct);
        var by = await SeedAsync(host, ModbotPermissions.WriteNotes, ct);

        var written = await InScopeAsync(host, s => s.WriteNoteAsync(FactPlatform.Discord, "445566", "Spam.", by, ct));

        Assert.True(written.Written);

        var fact = Assert.Single(await FactsAboutAsync(host, "445566", ct));
        Assert.Equal(FactType.NoteAdded, fact.Type);
        Assert.Equal(FactPlatform.Discord, fact.SubjectPlatform);

        var data = JsonDocument.Parse(fact.Data).RootElement;
        Assert.Equal("Spam.", data.GetProperty("text").GetString());
    }

    [Fact]
    public async Task ANoteWithoutThePermission_IsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db, Accepting());
        await host.ResetAsync(ct);
        var by = await SeedAsync(host, ModbotPermissions.ViewAuditLog, ct);

        var written = await InScopeAsync(host, s => s.WriteNoteAsync(FactPlatform.VRChat, Person, "Hi.", by, ct));

        Assert.False(written.Written);
        Assert.Empty(await FactsAboutAsync(host, Person, ct));
    }
}
