using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Auth;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;
using Modbot.VRChat.RateLimiting;

namespace Modbot.Api.Tests.Features.Auth;

/// <summary>
/// "Keep me signed in" (accounts and access design §5): which sign-in survives closing the
/// browser, how long a kept one may live, and what still ends one early.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class KeepSignedInTests
{
    private readonly PostgresFixture _db;

    public KeepSignedInTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string UniqueName() => $"u_{Guid.NewGuid():N}";

    /// <summary>Signs in with the box ticked or not, and hands back the whole Set-Cookie line.</summary>
    private static async Task<string> SignInAsync(
        ApiTestHost host, string username, bool keepSignedIn, object? extra = null)
    {
        var body = extra ?? new { username, password = TestAccounts.Password, keepSignedIn };

        var response = await host.SendJsonAsync(HttpMethod.Post, "/api/auth/login", body, null, Ct);
        response.EnsureSuccessStatusCode();

        return Assert.Single(
            response.Headers.GetValues("Set-Cookie"),
            v => v.StartsWith(ModbotAuth.CookieName, StringComparison.Ordinal));
    }

    private static string CookieOf(string setCookie) => setCookie.Split(';')[0];

    /// <summary>Takes the waits instead of waiting them, so the slowdown does not slow the test.</summary>
    private sealed class NoWaiting : IDelayScheduler
    {
        public Task DelayAsync(TimeSpan delay, CancellationToken ct = default) => Task.CompletedTask;
    }

    private async Task<(ApiTestHost Host, ModbotUser User)> WithAccountAsync(
        Action<IServiceCollection>? configure = null)
    {
        var host = await ApiTestHost.StartAsync(_db, configure: configure);
        var user = await host.CreateUserAsync(
            UniqueName(), TestAccounts.Password, ModbotPermissions.ViewMembers, Ct);

        return (host, user);
    }

    [Fact]
    public async Task Ticked_TheCookieOutlivesTheBrowser()
    {
        var (host, user) = await WithAccountAsync();
        await using var _ = host;

        var setCookie = await SignInAsync(host, user.Username, keepSignedIn: true);

        // An expiry date is the whole of what makes a browser keep a cookie past being closed.
        Assert.Contains("expires=", setCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unticked_TheCookieIsThrownAwayWhenTheBrowserCloses()
    {
        var (host, user) = await WithAccountAsync();
        await using var _ = host;

        var setCookie = await SignInAsync(host, user.Username, keepSignedIn: false);

        // No expiry and no max-age: a session cookie, which is what every sign-in was before this
        // feature and what one still is when nobody asks otherwise.
        Assert.DoesNotContain("expires=", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("max-age=", setCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AskingForNothing_IsTheSameAsNotTicking()
    {
        var (host, user) = await WithAccountAsync();
        await using var _ = host;

        // Every client written before the field existed sends exactly this.
        var setCookie = await SignInAsync(
            host, user.Username, keepSignedIn: false,
            extra: new { username = user.Username, password = TestAccounts.Password });

        Assert.DoesNotContain("expires=", setCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AKeptSession_EndsThirtyDaysAfterSigningIn()
    {
        var (host, user) = await WithAccountAsync();
        await using var _ = host;

        var cookie = CookieOf(await SignInAsync(host, user.Username, keepSignedIn: true));

        host.Clock.Advance(ModbotAuth.KeepSignedInLength - TimeSpan.FromHours(1));
        var justInside = await host.SendJsonAsync(HttpMethod.Get, "/api/auth/me", null, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, justInside.StatusCode);

        // Being used right up to the limit does not buy it another day: the thirty days are counted
        // from signing in, so a session forgotten on somebody else's machine does end.
        host.Clock.Advance(TimeSpan.FromHours(2));
        var past = await host.SendJsonAsync(HttpMethod.Get, "/api/auth/me", null, cookie, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, past.StatusCode);
    }

    [Fact]
    public async Task AnOrdinarySession_IsNotEndedByItsAge()
    {
        var (host, user) = await WithAccountAsync();
        await using var _ = host;

        var cookie = CookieOf(await SignInAsync(host, user.Username, keepSignedIn: false));

        // The limit belongs to the kept session only. An ordinary one is already ended by closing
        // the browser, and cutting a moderator off mid-incident for the crime of not closing it is
        // the wrong direction to fail in.
        host.Clock.Advance(ModbotAuth.KeepSignedInLength * 2);

        var response = await host.SendJsonAsync(HttpMethod.Get, "/api/auth/me", null, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task TheLength_IsModbotsAndNotTheCallers()
    {
        var (host, user) = await WithAccountAsync();
        await using var _ = host;

        // The sign-in route is open to anybody, so the body is an attacker's to write. Asking for a
        // decade, in every spelling a future field might use, still buys thirty days.
        var cookie = CookieOf(await SignInAsync(
            host, user.Username, keepSignedIn: true,
            extra: new
            {
                username = user.Username,
                password = TestAccounts.Password,
                keepSignedIn = true,
                keepSignedInLength = "3650.00:00:00",
                keepSignedInDays = 3650,
                expiresUtc = "2099-01-01T00:00:00Z",
                sessionLength = "3650.00:00:00",
            }));

        host.Clock.Advance(ModbotAuth.KeepSignedInLength + TimeSpan.FromHours(1));

        var response = await host.SendJsonAsync(HttpMethod.Get, "/api/auth/me", null, cookie, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task SigningOutEverywhere_EndsAKeptSessionToo()
    {
        var (host, user) = await WithAccountAsync();
        await using var _ = host;

        var kept = CookieOf(await SignInAsync(host, user.Username, keepSignedIn: true));
        var here = CookieOf(await SignInAsync(host, user.Username, keepSignedIn: false));

        host.Clock.Advance(TimeSpan.FromMinutes(1));

        var signedOut = await host.SendJsonAsync(
            HttpMethod.Post, "/api/auth/sign-out-everywhere", null, here, Ct);
        Assert.Equal(HttpStatusCode.NoContent, signedOut.StatusCode);

        // The cut-off reaches a kept session the same way it reaches any other: both carry the same
        // signed-in-at stamp, and nothing about surviving a browser close survives this.
        var after = await host.SendJsonAsync(HttpMethod.Get, "/api/auth/me", null, kept, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    [Fact]
    public async Task ChangingYourPassword_EndsAKeptSessionElsewhere()
    {
        var (host, user) = await WithAccountAsync();
        await using var _ = host;

        var elsewhere = CookieOf(await SignInAsync(host, user.Username, keepSignedIn: true));
        var here = CookieOf(await SignInAsync(host, user.Username, keepSignedIn: false));

        host.Clock.Advance(TimeSpan.FromMinutes(1));

        var changed = await host.SendJsonAsync(
            HttpMethod.Put, "/api/auth/password",
            new
            {
                currentPassword = TestAccounts.Password,
                newPassword = "a-brand-new-password",
                confirmPassword = "a-brand-new-password",
            },
            here, Ct);
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);

        var after = await host.SendJsonAsync(HttpMethod.Get, "/api/auth/me", null, elsewhere, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    [Fact]
    public async Task ChangingYourPassword_LeavesTheBrowserYouDidItInStillKept()
    {
        var (host, user) = await WithAccountAsync();
        await using var _ = host;

        var kept = CookieOf(await SignInAsync(host, user.Username, keepSignedIn: true));

        host.Clock.Advance(TimeSpan.FromMinutes(1));

        var changed = await host.SendJsonAsync(
            HttpMethod.Put, "/api/auth/password",
            new
            {
                currentPassword = TestAccounts.Password,
                newPassword = "a-brand-new-password",
                confirmPassword = "a-brand-new-password",
            },
            kept, Ct);
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);

        var reissued = Assert.Single(
            changed.Headers.GetValues("Set-Cookie"),
            v => v.StartsWith(ModbotAuth.CookieName, StringComparison.Ordinal));

        // Handing back an ordinary cookie here would quietly cancel a choice the person made and
        // never told them, and they would find themselves signed out the next time they closed the
        // browser.
        Assert.Contains("expires=", reissued, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ChangingYourUsername_LeavesTheSessionStillKept()
    {
        var (host, user) = await WithAccountAsync();
        await using var _ = host;

        var kept = CookieOf(await SignInAsync(host, user.Username, keepSignedIn: true));

        var changed = await host.SendJsonAsync(
            HttpMethod.Put, "/api/auth/username",
            new { username = UniqueName(), currentPassword = TestAccounts.Password },
            kept, Ct);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);

        var reissued = Assert.Single(
            changed.Headers.GetValues("Set-Cookie"),
            v => v.StartsWith(ModbotAuth.CookieName, StringComparison.Ordinal));

        Assert.Contains("expires=", reissued, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ARenamedKeptSession_StillEndsOnItsOriginalThirtyDays()
    {
        var (host, user) = await WithAccountAsync();
        await using var _ = host;

        var kept = CookieOf(await SignInAsync(host, user.Username, keepSignedIn: true));

        host.Clock.Advance(TimeSpan.FromDays(20));

        var changed = await host.SendJsonAsync(
            HttpMethod.Put, "/api/auth/username",
            new { username = UniqueName(), currentPassword = TestAccounts.Password },
            kept, Ct);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);

        var renamed = CookieOf(Assert.Single(
            changed.Headers.GetValues("Set-Cookie"),
            v => v.StartsWith(ModbotAuth.CookieName, StringComparison.Ordinal)));

        // A rename is not a sign-in. Re-issuing with today's date would be a way to keep a session
        // alive for ever by renaming yourself once a month.
        host.Clock.Advance(TimeSpan.FromDays(11));

        var after = await host.SendJsonAsync(HttpMethod.Get, "/api/auth/me", null, renamed, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    [Fact]
    public async Task ADisabledAccount_LosesAKeptSessionOnItsNextRequest()
    {
        var (host, user) = await WithAccountAsync();
        await using var _ = host;

        var kept = CookieOf(await SignInAsync(host, user.Username, keepSignedIn: true));

        using (var scope = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
                   .CreateScope(host.Services))
        {
            var db = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
                .GetRequiredService<Core.Data.ModbotContext>(scope.ServiceProvider);
            var tracked = await db.Users.FindAsync([user.Id], Ct);
            tracked!.IsDisabled = true;
            await db.SaveChangesAsync(Ct);
        }

        var after = await host.SendJsonAsync(HttpMethod.Get, "/api/auth/me", null, kept, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    [Fact]
    public async Task AFailedSignIn_AnswersTheSameWhicheverWayTheBoxIsTicked()
    {
        var (host, user) = await WithAccountAsync(
            services => services.AddSingleton<IDelayScheduler>(new NoWaiting()));
        await using var _ = host;

        var ticked = await host.SendJsonAsync(
            HttpMethod.Post, "/api/auth/login",
            new { username = user.Username, password = "wrong", keepSignedIn = true }, null, Ct);

        var unticked = await host.SendJsonAsync(
            HttpMethod.Post, "/api/auth/login",
            new { username = user.Username, password = "wrong", keepSignedIn = false }, null, Ct);

        // The one answer for every kind of failure is one answer for this too. A tick that changed
        // the reply -- or handed back a cookie -- would be a new way to ask the form questions.
        Assert.Equal(HttpStatusCode.Unauthorized, ticked.StatusCode);
        Assert.Equal(ticked.StatusCode, unticked.StatusCode);
        Assert.Equal(
            await ticked.Content.ReadAsStringAsync(Ct),
            await unticked.Content.ReadAsStringAsync(Ct));

        Assert.DoesNotContain("Set-Cookie", ticked.Headers.Select(h => h.Key));
        Assert.DoesNotContain("Set-Cookie", unticked.Headers.Select(h => h.Key));
    }
}
