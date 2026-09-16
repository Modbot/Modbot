using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Modbot.Cloud.Common;
using Modbot.Cloud.Data;
using Modbot.Cloud.Features.Mail;
using Npgsql;

namespace Modbot.Cloud.Features.Accounts;

public sealed record RegisterAccountRequest(
    [property: JsonPropertyName("email")] string? Email,
    [property: JsonPropertyName("password")] string? Password);

public sealed record TokenRequest([property: JsonPropertyName("token")] string? Token);

public sealed record EmailRequest([property: JsonPropertyName("email")] string? Email);

public sealed record SignInRequest(
    [property: JsonPropertyName("email")] string? Email,
    [property: JsonPropertyName("password")] string? Password);

public sealed record ResetPasswordRequest(
    [property: JsonPropertyName("token")] string? Token,
    [property: JsonPropertyName("password")] string? Password);

public sealed record ChangePasswordRequest(
    [property: JsonPropertyName("currentPassword")] string? CurrentPassword,
    [property: JsonPropertyName("password")] string? Password);

public sealed record ChangeEmailRequest(
    [property: JsonPropertyName("email")] string? Email,
    [property: JsonPropertyName("password")] string? Password);

/// <param name="PendingEmail">An address the account is moving to, once its link is used.</param>
public sealed record AccountView(
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("emailVerified")] bool EmailVerified,
    [property: JsonPropertyName("pendingEmail")] string? PendingEmail,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt);

/// <summary>
/// Registering, verifying, signing in and out, and changing a password or an address
/// (Cloud accounts and registry spec 2).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Every endpoint that takes an address answers the same whether or not the address exists.</strong>
/// Registering an address that is already taken, asking to resend a verification mail and asking for
/// a password reset all return <c>202</c> and say nothing, because any other answer turns the
/// endpoint into a way to ask whether a given person has an account.
/// </para>
/// <para>
/// An unverified account cannot sign in. That is what stops somebody registering with an address
/// that is not theirs and using it to claim a Modbot server.
/// </para>
/// </remarks>
public static class AccountEndpoints
{
    public const int MinPasswordLength = 12;
    public const int MaxPasswordLength = 200;

    public static IEndpointRouteBuilder MapAccounts(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/accounts", RegisterAsync);
        app.MapPost("/api/v1/accounts/verify", VerifyAsync);
        app.MapPost("/api/v1/accounts/resend-verification", ResendVerificationAsync);
        app.MapPost("/api/v1/accounts/session", SignInAsync);
        app.MapDelete("/api/v1/accounts/session", SignOutAsync);
        app.MapPost("/api/v1/accounts/forgot-password", ForgotPasswordAsync);
        app.MapPost("/api/v1/accounts/reset-password", ResetPasswordAsync);

        // Cast, because a method taking only HttpContext and returning a Task looks like a
        // RequestDelegate, which would discard the result instead of writing it.
        app.MapGet("/api/v1/accounts/me", (Delegate)MeAsync).RequireAccount();
        app.MapPost("/api/v1/accounts/password", ChangePasswordAsync).RequireAccount();
        app.MapPost("/api/v1/accounts/email", ChangeEmailAsync).RequireAccount();
        app.MapPost("/api/v1/accounts/verify-email-change", VerifyEmailChangeAsync);

        return app;
    }

    internal static async Task<IResult> RegisterAsync(
        [FromBody] RegisterAccountRequest? request,
        [FromServices] CloudContext db,
        [FromServices] AccountTokens tokens,
        [FromServices] AccountLimits limits,
        [FromServices] ICloudMailer mailer,
        [FromServices] MailSettings mail,
        [FromServices] IPasswordHasher<Account> hasher,
        [FromServices] TimeProvider time,
        HttpContext http,
        CancellationToken ct)
    {
        if (!mailer.CanSend)
            return NoMail();

        var ip = Address(http);
        if (limits.Registrations.TryTake(ip) is { } wait)
            return CloudError.TooMany(http, wait, "Too many accounts from this address.");

        if (!EmailAddress.TryNormalise(request?.Email, out var email))
            return Refuse("That does not look like an email address.");

        if (PasswordProblem(request?.Password) is { } problem)
            return Refuse(problem);

        var now = time.GetUtcNow();
        var account = new Account
        {
            Id = Guid.NewGuid(),
            Email = email,
            CreatedAt = now,
        };
        account.PasswordHash = hasher.HashPassword(account, request!.Password!);

        db.Accounts.Add(account);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e)
            when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // The address is taken. Answer exactly as if it were not, and send nothing: a different
            // answer here is a way to ask whether somebody has an account.
            db.ChangeTracker.Clear();
            return Results.Accepted();
        }

        await SendVerificationAsync(db, tokens, mailer, mail, account, ct);
        return Results.Accepted();
    }

    internal static async Task<IResult> VerifyAsync(
        [FromBody] TokenRequest? request,
        [FromServices] CloudContext db,
        [FromServices] AccountTokens tokens,
        [FromServices] TimeProvider time,
        CancellationToken ct)
    {
        var token = await tokens.SpendAsync(db, request?.Token, TokenPurpose.VerifyEmail, ct);
        if (token is null)
            return Refuse("That link is no longer good. Ask for a new one.");

        var account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == token.AccountId, ct);
        if (account is null)
            return Refuse("That link is no longer good. Ask for a new one.");

        account.EmailVerifiedAt ??= time.GetUtcNow();
        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }

    internal static async Task<IResult> ResendVerificationAsync(
        [FromBody] EmailRequest? request,
        [FromServices] CloudContext db,
        [FromServices] AccountTokens tokens,
        [FromServices] AccountLimits limits,
        [FromServices] ICloudMailer mailer,
        [FromServices] MailSettings mail,
        HttpContext http,
        CancellationToken ct)
    {
        if (!mailer.CanSend)
            return NoMail();

        if (limits.Mails.TryTake(Address(http)) is { } wait)
            return CloudError.TooMany(http, wait, "Too many messages to this address.");

        if (EmailAddress.TryNormalise(request?.Email, out var email))
        {
            var account = await db.Accounts.FirstOrDefaultAsync(a => a.Email == email, ct);
            if (account is { EmailVerifiedAt: null })
                await SendVerificationAsync(db, tokens, mailer, mail, account, ct);
        }

        return Results.Accepted();
    }

    internal static async Task<IResult> SignInAsync(
        [FromBody] SignInRequest? request,
        [FromServices] CloudContext db,
        [FromServices] AccountSessions sessions,
        [FromServices] AccountLimits limits,
        [FromServices] IPasswordHasher<Account> hasher,
        [FromServices] TimeProvider time,
        HttpContext http,
        CancellationToken ct)
    {
        if (limits.SignIns.TryTake(Address(http)) is { } wait)
            return CloudError.TooMany(http, wait, "Too many attempts from this address.");

        var account = EmailAddress.TryNormalise(request?.Email, out var email)
            ? await db.Accounts.FirstOrDefaultAsync(a => a.Email == email, ct)
            : null;

        // Hashed either way, so a missing address and a wrong password take the same time.
        var verified = hasher.VerifyHashedPassword(
            account ?? new Account(),
            account?.PasswordHash ?? UnusableHash(hasher),
            request?.Password ?? string.Empty);

        if (account is null || verified == PasswordVerificationResult.Failed)
            return Results.Json(new { error = "Wrong address or password." }, statusCode: StatusCodes.Status401Unauthorized);

        if (account.EmailVerifiedAt is null)
            return Results.Json(new { error = "Confirm your address first." }, statusCode: StatusCodes.Status403Forbidden);

        if (verified == PasswordVerificationResult.SuccessRehashNeeded)
            account.PasswordHash = hasher.HashPassword(account, request!.Password!);

        account.LastSignedInAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);

        var (token, expiresAt) = await sessions.StartAsync(db, account.Id, ct);
        http.Response.Cookies.Append(AccountSessions.CookieName, token, AccountSessions.CookieOptions(expiresAt));

        return Results.NoContent();
    }

    internal static async Task<IResult> SignOutAsync(
        [FromServices] CloudContext db,
        [FromServices] AccountSessions sessions,
        HttpContext http,
        CancellationToken ct)
    {
        if (http.Request.Cookies.TryGetValue(AccountSessions.CookieName, out var token))
            await sessions.EndAsync(db, token, ct);

        http.Response.Cookies.Delete(
            AccountSessions.CookieName, AccountSessions.CookieOptions(DateTimeOffset.UnixEpoch));

        return Results.NoContent();
    }

    internal static async Task<IResult> ForgotPasswordAsync(
        [FromBody] EmailRequest? request,
        [FromServices] CloudContext db,
        [FromServices] AccountTokens tokens,
        [FromServices] AccountLimits limits,
        [FromServices] ICloudMailer mailer,
        [FromServices] MailSettings mail,
        HttpContext http,
        CancellationToken ct)
    {
        if (!mailer.CanSend)
            return NoMail();

        if (limits.Mails.TryTake(Address(http)) is { } wait)
            return CloudError.TooMany(http, wait, "Too many messages to this address.");

        if (EmailAddress.TryNormalise(request?.Email, out var email)
            && await db.Accounts.FirstOrDefaultAsync(a => a.Email == email, ct) is { } account)
        {
            var token = await tokens.IssueAsync(
                db, account.Id, TokenPurpose.ResetPassword, AccountTokens.ResetLifetime, null, ct);

            var (subject, body) = AccountMail.ResetPassword(mail.PublicAddress, token);
            await mailer.SendAsync(account.Email, subject, body, ct);
        }

        return Results.Accepted();
    }

    internal static async Task<IResult> ResetPasswordAsync(
        [FromBody] ResetPasswordRequest? request,
        [FromServices] CloudContext db,
        [FromServices] AccountTokens tokens,
        [FromServices] AccountSessions sessions,
        [FromServices] IPasswordHasher<Account> hasher,
        [FromServices] TimeProvider time,
        CancellationToken ct)
    {
        if (PasswordProblem(request?.Password) is { } problem)
            return Refuse(problem);

        var token = await tokens.SpendAsync(db, request?.Token, TokenPurpose.ResetPassword, ct);
        if (token is null)
            return Refuse("That link is no longer good. Ask for a new one.");

        var account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == token.AccountId, ct);
        if (account is null)
            return Refuse("That link is no longer good. Ask for a new one.");

        account.PasswordHash = hasher.HashPassword(account, request!.Password!);

        // Somebody who used a reset link proved they read the mail at that address, which is the same
        // proof the verification link asks for.
        account.EmailVerifiedAt ??= time.GetUtcNow();
        await db.SaveChangesAsync(ct);

        // Every session, including any an attacker left behind. That is the point of a reset.
        await sessions.EndAllAsync(db, account.Id, ct);

        return Results.NoContent();
    }

    internal static async Task<IResult> MeAsync(HttpContext http) =>
        Results.Ok(View(await AccountAccess.RequiredAsync(http)));

    internal static async Task<IResult> ChangePasswordAsync(
        [FromBody] ChangePasswordRequest? request,
        [FromServices] CloudContext db,
        [FromServices] AccountSessions sessions,
        [FromServices] IPasswordHasher<Account> hasher,
        HttpContext http,
        CancellationToken ct)
    {
        var signedIn = await AccountAccess.RequiredAsync(http);

        if (PasswordProblem(request?.Password) is { } problem)
            return Refuse(problem);

        var account = await db.Accounts.FirstAsync(a => a.Id == signedIn.Id, ct);

        if (hasher.VerifyHashedPassword(account, account.PasswordHash, request?.CurrentPassword ?? string.Empty)
            == PasswordVerificationResult.Failed)
        {
            return Refuse("That is not the current password.");
        }

        account.PasswordHash = hasher.HashPassword(account, request!.Password!);
        await db.SaveChangesAsync(ct);

        // Every other browser is signed out, and this one is signed back in.
        await sessions.EndAllAsync(db, account.Id, ct);
        var (token, expiresAt) = await sessions.StartAsync(db, account.Id, ct);
        http.Response.Cookies.Append(AccountSessions.CookieName, token, AccountSessions.CookieOptions(expiresAt));

        return Results.NoContent();
    }

    internal static async Task<IResult> ChangeEmailAsync(
        [FromBody] ChangeEmailRequest? request,
        [FromServices] CloudContext db,
        [FromServices] AccountTokens tokens,
        [FromServices] AccountLimits limits,
        [FromServices] ICloudMailer mailer,
        [FromServices] MailSettings mail,
        [FromServices] IPasswordHasher<Account> hasher,
        HttpContext http,
        CancellationToken ct)
    {
        var signedIn = await AccountAccess.RequiredAsync(http);

        if (!mailer.CanSend)
            return NoMail();

        if (limits.Mails.TryTake(Address(http)) is { } wait)
            return CloudError.TooMany(http, wait, "Too many messages to this address.");

        if (!EmailAddress.TryNormalise(request?.Email, out var email))
            return Refuse("That does not look like an email address.");

        var account = await db.Accounts.FirstAsync(a => a.Id == signedIn.Id, ct);

        // The password, because an unattended browser must not be enough to move the account
        // somewhere its owner cannot reach.
        if (hasher.VerifyHashedPassword(account, account.PasswordHash, request?.Password ?? string.Empty)
            == PasswordVerificationResult.Failed)
        {
            return Refuse("That is not the current password.");
        }

        if (account.Email == email)
            return Refuse("That is already this account's address.");

        // The account keeps its old address until the new one answers, so a typo locks nobody out.
        account.PendingEmail = email;
        await db.SaveChangesAsync(ct);

        var token = await tokens.IssueAsync(
            db, account.Id, TokenPurpose.ChangeEmail, AccountTokens.VerifyLifetime, email, ct);

        var (subject, body) = AccountMail.ChangeEmail(mail.PublicAddress, token);
        await mailer.SendAsync(email, subject, body, ct);

        return Results.Accepted();
    }

    internal static async Task<IResult> VerifyEmailChangeAsync(
        [FromBody] TokenRequest? request,
        [FromServices] CloudContext db,
        [FromServices] AccountTokens tokens,
        [FromServices] TimeProvider time,
        CancellationToken ct)
    {
        var token = await tokens.SpendAsync(db, request?.Token, TokenPurpose.ChangeEmail, ct);
        if (token is null || token.Email is null)
            return Refuse("That link is no longer good. Ask for a new one.");

        var account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == token.AccountId, ct);
        if (account is null)
            return Refuse("That link is no longer good. Ask for a new one.");

        account.Email = token.Email;
        account.PendingEmail = null;
        account.EmailVerifiedAt = time.GetUtcNow();

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e)
            when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Somebody registered that address between the request and the confirmation.
            db.ChangeTracker.Clear();
            return Refuse("That address is already in use.");
        }

        return Results.NoContent();
    }

    private static async Task SendVerificationAsync(
        CloudContext db,
        AccountTokens tokens,
        ICloudMailer mailer,
        MailSettings mail,
        Account account,
        CancellationToken ct)
    {
        var token = await tokens.IssueAsync(
            db, account.Id, TokenPurpose.VerifyEmail, AccountTokens.VerifyLifetime, null, ct);

        var (subject, body) = AccountMail.VerifyEmail(mail.PublicAddress, token);
        await mailer.SendAsync(account.Email, subject, body, ct);
    }

    internal static AccountView View(Account account)
    {
        ArgumentNullException.ThrowIfNull(account);

        return new AccountView(
            account.Email, account.EmailVerifiedAt is not null, account.PendingEmail, account.CreatedAt);
    }

    /// <summary>
    /// The one password rule: long enough. Composition rules make people write worse passwords down.
    /// </summary>
    private static string? PasswordProblem(string? password) => password switch
    {
        null or "" => "A password is required.",
        { Length: < MinPasswordLength } => $"A password must be at least {MinPasswordLength} characters.",
        { Length: > MaxPasswordLength } => $"A password must be at most {MaxPasswordLength} characters.",
        _ => null,
    };

    /// <summary>A hash nothing verifies against, so a missing account costs the same work as a wrong password.</summary>
    private static string UnusableHash(IPasswordHasher<Account> hasher) =>
        hasher.HashPassword(new Account(), "no account matched");

    private static string Address(HttpContext http) => ClientAddress.From(http)?.ToString() ?? "unknown";

    private static IResult Refuse(string error) => Results.BadRequest(new { error });

    private static IResult NoMail() => Results.Json(
        new { error = "This Modbot Cloud cannot send mail." },
        statusCode: StatusCodes.Status503ServiceUnavailable);
}
