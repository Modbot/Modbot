using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Configuration;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Settings;

/// <summary>
/// Settings → Discord → Account linking: the secret is write-only, the redirect URL and invite link
/// are built from what is stored, and roles the bot cannot hand out are refused.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class DiscordLinkingSettingsTests
{
    private const string Path = "/api/settings/discord-linking";

    private readonly PostgresFixture _db;

    public DiscordLinkingSettingsTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static object Update(
        string? clientId = "1234567890",
        string? clientSecret = null,
        bool removeClientSecret = false,
        bool promptNewMembers = false,
        string? backupChannelId = null,
        string? linkedRoleId = null,
        string? eighteenPlusRoleId = null)
        => new { clientId, clientSecret, removeClientSecret, promptNewMembers, backupChannelId, linkedRoleId, eighteenPlusRoleId };

    [Fact]
    public async Task WithoutChangeSettings_ItIsRefused()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ViewProfile, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(HttpMethod.Get, Path, null, cookie, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(HttpMethod.Put, Path, Update(), cookie, Ct)).StatusCode);
    }

    [Fact]
    public async Task TheSecretIsNeverReturned_AndTheLinksAreBuiltFromWhatIsStored()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
            (await db.GetSettingsAsync(Ct)).PublicAddress = "https://modbot.example.com";
            await db.SaveChangesAsync(Ct);
        }

        var response = await host.SendJsonAsync(
            HttpMethod.Put, Path, Update(clientSecret: "very-secret", promptNewMembers: true, linkedRoleId: "801"), cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain("very-secret", text, StringComparison.Ordinal);

        var body = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, Path, null, cookie, Ct), Ct);
        Assert.True(body.GetProperty("clientSecretStored").GetBoolean());
        Assert.True(body.GetProperty("available").GetBoolean());
        Assert.True(body.GetProperty("promptNewMembers").GetBoolean());
        Assert.Equal("https://modbot.example.com/api/discord-link/callback", body.GetProperty("redirectUrl").GetString());

        // Manage Roles is asked for because a linked role is set.
        var invite = body.GetProperty("inviteUrl").GetString()!;
        Assert.StartsWith("https://discord.com/oauth2/authorize?client_id=1234567890&scope=bot+applications.commands&permissions=", invite, StringComparison.Ordinal);
        var permissions = long.Parse(invite[(invite.LastIndexOf('=') + 1)..], System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(DiscordInvite.ManageRoles, permissions & DiscordInvite.ManageRoles);

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
            var stored = await db.Settings.AsNoTracking().SingleAsync(s => s.Id == 1, Ct);
            Assert.NotEqual("very-secret", stored.DiscordOAuthClientSecretEncrypted);
        }

        // Saving again with the secret left blank keeps it.
        await host.SendJsonAsync(HttpMethod.Put, Path, Update(), cookie, Ct);
        body = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, Path, null, cookie, Ct), Ct);
        Assert.True(body.GetProperty("clientSecretStored").GetBoolean());

        // Without a role, no Manage Roles.
        invite = body.GetProperty("inviteUrl").GetString()!;
        permissions = long.Parse(invite[(invite.LastIndexOf('=') + 1)..], System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(0, permissions & DiscordInvite.ManageRoles);

        // A different client id without a new secret forgets the old one.
        await host.SendJsonAsync(HttpMethod.Put, Path, Update(clientId: "999"), cookie, Ct);
        body = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, Path, null, cookie, Ct), Ct);
        Assert.False(body.GetProperty("clientSecretStored").GetBoolean());
        Assert.False(body.GetProperty("available").GetBoolean());
    }

    [Fact]
    public async Task ARoleTheBotCannotAssign_OrTheSameRoleTwice_IsRefused()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);
        var roleId = Guid.NewGuid().ToString("n");

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
            db.DiscordRoles.Add(new DiscordRole { RoleId = roleId, GuildId = "700", Name = "Too High", BotCanAssign = false });
            await db.SaveChangesAsync(Ct);
        }

        var unassignable = await host.SendJsonAsync(HttpMethod.Put, Path, Update(linkedRoleId: roleId), cookie, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, unassignable.StatusCode);
        Assert.Contains("Too High", await unassignable.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        var twice = await host.SendJsonAsync(HttpMethod.Put, Path, Update(linkedRoleId: "5", eighteenPlusRoleId: "5"), cookie, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, twice.StatusCode);
    }

    // ── /me (Discord /me design §5) ─────────────────────────────────────────────────────────

    [Fact]
    public async Task MeIsOffByDefault_TurningItOnIsSaved_AndRecorded()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var before = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, Path, null, cookie, Ct), Ct);
        Assert.False(before.GetProperty("meCommand").GetBoolean());

        var on = new { clientId = "1234567890", removeClientSecret = false, promptNewMembers = false, meCommand = true };
        Assert.Equal(HttpStatusCode.OK, (await host.SendJsonAsync(HttpMethod.Put, Path, on, cookie, Ct)).StatusCode);

        var after = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, Path, null, cookie, Ct), Ct);
        Assert.True(after.GetProperty("meCommand").GetBoolean());

        var entry = (await host.FactsAsync(FactType.SettingsChanged, "settings", Ct))
            .Select(ApiTestHost.DataOf)
            .Last(data => data.GetProperty("setting").GetString() == "discordLinking");
        var changed = entry.GetProperty("changed").GetProperty("meCommand");
        Assert.False(changed.GetProperty("old").GetBoolean());
        Assert.True(changed.GetProperty("new").GetBoolean());

        // A save that does not mention it (an older page) keeps it on.
        Assert.Equal(HttpStatusCode.OK, (await host.SendJsonAsync(HttpMethod.Put, Path, Update(), cookie, Ct)).StatusCode);
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        Assert.True((await db.Settings.AsNoTracking().SingleAsync(s => s.Id == 1, Ct)).DiscordMeCommand);

        // Put back for the next test, which shares the settings row.
        var off = new { clientId = "1234567890", removeClientSecret = false, promptNewMembers = false, meCommand = false };
        Assert.Equal(HttpStatusCode.OK, (await host.SendJsonAsync(HttpMethod.Put, Path, off, cookie, Ct)).StatusCode);
    }

    [Fact]
    public async Task InADemo_MeCannotBeTurnedOn()
    {
        var demo = new DemoMode { Requested = true };
        demo.Decide(hasStaffAccount: false, onboardingComplete: false, holdsDemoData: true);

        await using var host = await ApiTestHost.StartAsync(_db, configure: services => services.AddSingleton(demo));
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var on = new { clientId = "1234567890", removeClientSecret = false, promptNewMembers = false, meCommand = true };
        var response = await host.SendJsonAsync(HttpMethod.Put, Path, on, cookie, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("demo", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        var stored = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, Ct);
        Assert.False(stored?.DiscordMeCommand ?? false);
    }
}
