using System.Net;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Users;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Users;

/// <summary>
/// Letters, numbers and underscores, wherever a username is set (username rules and deleting
/// accounts design §2). Every way into the account table is checked here, because the rule is only
/// a rule if none of them lets something else through.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class UsernameCharactersTests
{
    private readonly PostgresFixture _db;

    public UsernameCharactersTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string UniqueEmail() => $"u_{Guid.NewGuid():N}@example.com";

    /// <summary>One of each kind of character the rule keeps out.</summary>
    public static TheoryData<string> Refused =>
    [
        "alice smith",        // a space
        "alice.smith",        // a dot
        "alice-smith",        // a dash
        "alice@example.com",  // an address
        "alice!",             // punctuation
        "alice#1234",         // a Discord tag
        "renée",              // an accent
        "аlice",              // a Cyrillic а
        "アリス",               // another script
    ];

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task CreatingAnAccountWithAnythingElseIsRefused(string username)
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.Administrator, Ct);

        var response = await host.SendJsonAsync(
            HttpMethod.Post,
            "/api/users",
            new
            {
                username,
                password = "a-long-enough-password",
                email = UniqueEmail(),
                roleIds = Array.Empty<Guid>(),
            },
            cookie,
            Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(
            "letters, numbers and underscores",
            await response.Content.ReadAsStringAsync(Ct),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("alice")]
    [InlineData("Alice_99")]
    [InlineData("_alice_")]
    [InlineData("ALICE")]
    public async Task LettersNumbersAndUnderscoresAreAccepted(string shape)
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.Administrator, Ct);

        var response = await host.SendJsonAsync(
            HttpMethod.Post,
            "/api/users",
            new
            {
                username = $"{shape}_{Guid.NewGuid():N}",
                password = "a-long-enough-password",
                email = UniqueEmail(),
                roleIds = Array.Empty<Guid>(),
            },
            cookie,
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ChangingYourOwnUsernameIsRefusedToo()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);

        var response = await host.SendJsonAsync(
            HttpMethod.Put,
            "/api/auth/username",
            new { username = "new name", currentPassword = TestAccounts.Password },
            cookie,
            Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(
            "letters, numbers and underscores",
            await response.Content.ReadAsStringAsync(Ct),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AcceptingAnInviteIsRefusedToo()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.Administrator, Ct);

        var made = await host.SendJsonAsync(
            HttpMethod.Post, "/api/invites", new { roleIds = Array.Empty<Guid>() }, cookie, Ct);
        made.EnsureSuccessStatusCode();
        var path = "/api" + (await ApiTestHost.BodyOf(made, Ct)).GetProperty("path").GetString()!;

        var response = await host.SendJsonAsync(
            HttpMethod.Post,
            path,
            new
            {
                username = "new person",
                password = "a-long-enough-password",
                confirmPassword = "a-long-enough-password",
                email = UniqueEmail(),
            },
            null,
            Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(
            "letters, numbers and underscores",
            await response.Content.ReadAsStringAsync(Ct),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// An account made before the rule existed keeps working. The rule applies when a username is
    /// set, never when one is read, so a name with a space in it still signs in.
    /// </summary>
    [Fact]
    public async Task AnOldStyleUsernameCanStillSignIn()
    {
        await using var host = await ApiTestHost.StartAsync(_db);

        var name = $"Old Name {Guid.NewGuid():N}";

        // Straight into the table, the way a deployment from before the rule holds it. Nothing
        // that makes an account will make this one any more, which is the point.
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
            var user = new ModbotUser
            {
                Username = name,
                UsernameNormalized = UserAccountService.Normalize(name),
                Email = UniqueEmail(),
                CreatedAt = host.Clock.UtcNow,
            };
            user.PasswordHash = new PasswordHasher<ModbotUser>().HashPassword(user, TestAccounts.Password);
            db.Users.Add(user);
            await db.SaveChangesAsync(Ct);
        }

        var login = await host.Client.PostAsJsonAsync(
            "/api/auth/login", new { username = name, password = TestAccounts.Password }, Ct);

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }
}
