using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;

namespace Modbot.VRChat.Session;

/// <summary>
/// Keeps the sign-in limit's record in Postgres, which is what makes spec 4.1.2 survive a restart.
/// </summary>
/// <remarks>
/// A scope per operation, like the rate limit store: the gate is a singleton and the context is
/// scoped. Volume is a handful of rows an hour at most, by construction.
/// </remarks>
public sealed class DatabaseSignInStore(IServiceScopeFactory scopes) : IVRChatSignInStore
{
    /// <summary>
    /// Rows older than this are deleted as new ones are written. A day rather than the hour the
    /// limit counts over, so someone looking into a sign-in problem the next morning still has the
    /// evening's record.
    /// </summary>
    private static readonly TimeSpan KeepFor = TimeSpan.FromDays(1);

    public async Task<StoredSignIns> LoadAsync(DateTimeOffset since, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        var attempts = await db.VRChatSignInAttempts
            .AsNoTracking()
            .Where(a => a.At > since)
            .OrderBy(a => a.At)
            .Select(a => a.At)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var settings = await db.Settings
            .AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => new { s.VRChatSignInWaitUntil, s.VRChatSignInWaitReason, s.VRChatLastSignedInAt })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        SignInWait? wait = null;

        if (settings?.VRChatSignInWaitUntil is { } until)
        {
            // An unreadable reason is still a wait: the time is what keeps Modbot quiet, and
            // dropping it because a word did not parse would send a sign-in into the block.
            var reason = Enum.TryParse<SignInWaitReason>(settings.VRChatSignInWaitReason, out var parsed)
                ? parsed
                : SignInWaitReason.RateLimitedByVRChat;

            wait = new SignInWait(reason, until);
        }

        return new StoredSignIns(attempts, wait, settings?.VRChatLastSignedInAt);
    }

    public async Task RecordAttemptAsync(DateTimeOffset at, string operation, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        db.VRChatSignInAttempts.Add(new VRChatSignInAttempt { At = at, Operation = operation });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        var before = at - KeepFor;
        await db.VRChatSignInAttempts
            .Where(a => a.At < before)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task SaveWaitAsync(SignInWait? wait, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        var settings = await db.GetSettingsAsync(ct).ConfigureAwait(false);

        settings.VRChatSignInWaitUntil = wait?.RetryAt;
        settings.VRChatSignInWaitReason = wait?.Reason.ToString();

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task RecordSignedInAsync(DateTimeOffset at, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        var settings = await db.GetSettingsAsync(ct).ConfigureAwait(false);

        settings.VRChatLastSignedInAt = at;

        // A sign-in with the password is VRChat accepting the stored credentials, which is what
        // this column records -- including when the attempt after a wait is what finally got in.
        settings.VRChatVerifiedAt = at;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
