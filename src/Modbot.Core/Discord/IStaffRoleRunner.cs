namespace Modbot.Core.Discord;

/// <summary>What one staff role pass did (staff roles from Discord design §6).</summary>
/// <param name="Given">Roles given, on either side.</param>
/// <param name="Taken">Roles taken away, on either side.</param>
/// <param name="Left">Changes this pass did not get to. The next pass carries on.</param>
/// <param name="Held">The pass stopped itself: it would take roles from too many accounts at once.</param>
/// <param name="Losing">How many accounts would lose a role.</param>
/// <param name="Problem">The last thing that could not be done, as a sentence, or null.</param>
public sealed record StaffRolePass(int Given, int Taken, int Left, bool Held, int Losing, string? Problem)
{
    public static StaffRolePass Nothing { get; } = new(0, 0, 0, false, 0, null);
}

/// <summary>
/// Runs the staff role pass on demand, past the brake, for the settings screen's Apply button.
/// </summary>
/// <remarks>
/// In Core so the API can offer the button without depending on the bot, like
/// <see cref="IDiscordSyncRunner"/>. A process with no bot answers that there is none.
/// </remarks>
public interface IStaffRoleRunner
{
    /// <summary>One pass now, carried out even when it would take roles from many accounts.</summary>
    Task<StaffRolePass> ApplyAsync(CancellationToken ct = default);
}

/// <summary>A process with no Discord bot.</summary>
public sealed class NoStaffRoleRunner : IStaffRoleRunner
{
    public Task<StaffRolePass> ApplyAsync(CancellationToken ct = default)
        => Task.FromResult(StaffRolePass.Nothing with { Problem = "The Discord bot is not running." });
}
