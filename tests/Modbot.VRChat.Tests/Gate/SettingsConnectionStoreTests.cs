using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Data;
using Modbot.Core.Security;
using Modbot.TestSupport;
using Modbot.VRChat.Session;

namespace Modbot.VRChat.Tests.Gate;

/// <summary>
/// The VRChat account, read from and written to the settings row.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class SettingsConnectionStoreTests(PostgresFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheSessionRoundTripsThroughTheDatabase()
    {
        var (store, protector) = await NewStoreAsync();

        await using (var context = db.NewContext())
        {
            var settings = await context.GetSettingsAsync(Ct);
            settings.VRChatUsername = "modbot@example.com";
            settings.VRChatPasswordEncrypted = protector.Protect("hunter2");
            settings.VRChatTotpSecretEncrypted = protector.Protect("JBSWY3DPEHPK3PXP");
            await context.SaveChangesAsync(Ct);
        }

        await store.SaveSessionAsync("authValue", "twoFactorValue", Ct);

        var connection = await store.ReadAsync(Ct);

        Assert.True(connection.IsConfigured);
        Assert.Equal("hunter2", connection.Password);
        Assert.Equal("JBSWY3DPEHPK3PXP", connection.TotpSecret);
        Assert.Equal("authValue", connection.AuthCookie);
        Assert.Equal("twoFactorValue", connection.TwoFactorAuthCookie);
    }

    [Fact]
    public async Task TheStoredSessionIsEncryptedAtRest()
    {
        var (store, _) = await NewStoreAsync();
        await store.SaveSessionAsync("supersecretcookie", null, Ct);

        await using var context = db.NewContext();
        var settings = await context.GetSettingsAsync(Ct);

        // A live session cookie is a credential (spec 8.3). It is not protection against someone
        // holding the database -- they have the key too -- but a dump must not read as plaintext.
        Assert.DoesNotContain("supersecretcookie", settings.VRChatAuthCookieEncrypted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASessionWrittenBeforeBothCookiesWereStoredStillReads()
    {
        var (store, protector) = await NewStoreAsync();

        await using (var context = db.NewContext())
        {
            var settings = await context.GetSettingsAsync(Ct);

            // The column used to hold the auth cookie on its own. Reading that as a missing
            // session would log in again on every deploy of the upgrade.
            settings.VRChatAuthCookieEncrypted = protector.Protect("bareCookieValue");
            await context.SaveChangesAsync(Ct);
        }

        var connection = await store.ReadAsync(Ct);

        Assert.Equal("bareCookieValue", connection.AuthCookie);
        Assert.Null(connection.TwoFactorAuthCookie);
    }

    [Fact]
    public async Task ClearingTheSessionLeavesTheAccountAlone()
    {
        var (store, _) = await NewStoreAsync();
        await store.SaveSessionAsync("authValue", "twoFactorValue", Ct);

        await store.SaveSessionAsync(null, null, Ct);

        var connection = await store.ReadAsync(Ct);
        Assert.Null(connection.AuthCookie);
        Assert.Equal("modbot@example.com", connection.Username);
    }

    [Fact]
    public async Task ASignInStoresWhoTheSessionBelongsTo()
    {
        var (store, _) = await NewStoreAsync();

        await store.SaveSignInAsync(
            "authValue", "twoFactorValue", new VRChatSignedInAccount("modbot@example.com", "usr_bot", "ModbotBot"), Ct);

        var connection = await store.ReadAsync(Ct);

        Assert.Equal("authValue", connection.AuthCookie);
        Assert.Equal("usr_bot", connection.SessionUserId);
        Assert.Equal("ModbotBot", connection.DisplayName);
    }

    /// <summary>
    /// A session issued to another username is not offered, so the gate can never present one
    /// account's session while holding another account's password.
    /// </summary>
    [Fact]
    public async Task ASessionForADifferentUsernameIsNotOffered()
    {
        var (store, _) = await NewStoreAsync();

        await store.SaveSignInAsync(
            "authValue", "twoFactorValue", new VRChatSignedInAccount("someone-else@example.com", "usr_other", null), Ct);

        var connection = await store.ReadAsync(Ct);

        Assert.Null(connection.AuthCookie);
        Assert.Null(connection.TwoFactorAuthCookie);
        Assert.Null(connection.SessionUserId);
    }

    private async Task<(IVRChatConnectionStore Store, ISecretProtector Protector)> NewStoreAsync()
    {
        await using (var context = db.NewContext())
        {
            var settings = await context.GetSettingsAsync(Ct);
            settings.VRChatUsername = "modbot@example.com";
            settings.VRChatPasswordEncrypted = null;
            settings.VRChatAuthCookieEncrypted = null;
            settings.VRChatSessionAccount = null;
            settings.VRChatSessionUserId = null;
            await context.SaveChangesAsync(Ct);
        }

        await using var keyContext = db.NewContext();
        var protector = await AesGcmSecretProtector.CreateAsync(keyContext, Ct);

        var services = new ServiceCollection();
        services.AddDbContext<ModbotContext>(options => options.UseNpgsql(db.ConnectionString));

        return (new SettingsConnectionStore(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), protector),
            protector);
    }
}
