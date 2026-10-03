using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.Core.Posts;

/// <summary>
/// Claims one destination of a post for sending, before the site is asked (posts design §3.4).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Written first, then sent.</strong> The claim writes <see cref="PostDestinationStates.Sending"/>,
/// <see cref="PostDestination.SentAt"/> and exactly what is about to go out, raises the post's
/// <see cref="Post.Version"/>, and saves. Only when that save goes through does the caller ask the
/// site. A restart in between leaves a row that says what may have been sent, so it is looked for
/// rather than sent again.
/// </para>
/// <para>
/// <strong>The version is the lock.</strong> The post was read at some version; the save only goes
/// through if nobody wrote it since. An edit saved after the claim fails its own check and says
/// "This post is being sent."; an edit saved before the claim makes the claim fail, and nothing is
/// sent until the next pass reads the post again.
/// </para>
/// </remarks>
public sealed class PostClaim(ModbotContext db, IModbotClock clock)
{
    /// <summary>
    /// Claims <paramref name="destination"/> of <paramref name="post"/>, both tracked as they were
    /// read. True when the claim is saved and the caller may send; false when the post changed in
    /// the meantime or the destination is not waiting, and nothing may be sent. After a false the
    /// context holds nothing: the caller reads again.
    /// </summary>
    public async Task<bool> ClaimAsync(
        Post post, PostDestination destination, string? title, string text, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(post);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(text);

        if (post.Status != PostStatuses.Scheduled || destination.State != PostDestinationStates.Waiting)
            return false;

        var now = clock.UtcNow;

        destination.State = PostDestinationStates.Sending;
        destination.SentAt = now;
        destination.SentTitle = title;
        destination.SentText = text;
        destination.Error = null;
        destination.ErrorAt = null;
        destination.MissingPermission = null;
        destination.CheckAt = null;
        destination.MayBeSent = false;
        destination.SendIfMissing = false;
        destination.UpdatedAt = now;
        post.Version++;

        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            // Somebody wrote the post since it was read. What this pass holds is stale.
            db.ChangeTracker.Clear();
            return false;
        }
    }
}

/// <summary>
/// What a person's actions do to a post's destinations (posts design §4.5): Try again, Post now and
/// Cancel post. Each raises the post's version, so a send already claimed is never changed under it.
/// </summary>
/// <remarks>Pure: the caller saves, and says what the clock reads.</remarks>
public static class PostChanges
{
    /// <summary>
    /// Sends a failed destination on its way again. When the site may already have the post, it is
    /// looked for first and sent only if it is not there (§3.5); otherwise it waits to be sent.
    /// </summary>
    public static void TryAgain(Post post, PostDestination destination, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(post);
        ArgumentNullException.ThrowIfNull(destination);

        if (destination.MayBeSent)
        {
            destination.State = PostDestinationStates.Checking;
            destination.CheckAt = now;
            destination.SendIfMissing = true;
        }
        else
        {
            destination.State = PostDestinationStates.Waiting;
            destination.CheckAt = null;
            destination.SendIfMissing = false;
        }

        destination.Error = null;
        destination.ErrorAt = null;
        destination.MissingPermission = null;
        destination.UpdatedAt = now;
        post.Version++;
    }

    /// <summary>
    /// Sends the post now: scheduled at <paramref name="now"/>, and every failed destination that
    /// is surely not on its site back to waiting, the late ones included. One that may be on its site
    /// stays failed: Try again looks first.
    /// </summary>
    public static void SendNow(Post post, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(post);

        post.Status = PostStatuses.Scheduled;
        post.SendAt = now;
        post.UpdatedAt = now;

        foreach (var destination in post.Destinations)
        {
            if (destination.State == PostDestinationStates.Waiting
                || (destination.State == PostDestinationStates.Failed && !destination.MayBeSent))
            {
                destination.State = PostDestinationStates.Waiting;
                destination.Error = null;
                destination.ErrorAt = null;
                destination.MissingPermission = null;
                destination.CheckAt = null;
                destination.UpdatedAt = now;
            }
        }

        post.Version++;
    }

    /// <summary>
    /// Cancels the post: every destination not sent and surely not on its site is skipped. One
    /// already posted stays posted, and is deleted on its site by hand if wanted.
    /// </summary>
    public static void Cancel(Post post, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(post);

        post.Status = PostStatuses.Cancelled;
        post.CancelledAt = now;
        post.UpdatedAt = now;

        foreach (var destination in post.Destinations)
        {
            if (destination.State == PostDestinationStates.Waiting
                || (destination.State == PostDestinationStates.Failed && !destination.MayBeSent))
            {
                destination.State = PostDestinationStates.Skipped;
                destination.UpdatedAt = now;
            }
        }

        post.Version++;
    }
}
