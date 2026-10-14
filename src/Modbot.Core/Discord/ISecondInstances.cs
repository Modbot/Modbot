namespace Modbot.Core.Discord;

/// <summary>How pressing Open second instance went.</summary>
/// <param name="Opened">The instance is open now: made by this press, or by an earlier one on the same alert.</param>
/// <param name="Message">The sentence to show.</param>
/// <param name="JoinLink">VRChat's launch page for the instance, when there is one.</param>
/// <param name="Already">An earlier press made it, so this one opened nothing.</param>
public sealed record SecondInstanceAnswer(bool Opened, string Message, string? JoinLink = null, bool Already = false)
{
    public static SecondInstanceAnswer Refused(string message) => new(false, message);
}

/// <summary>
/// What the button under a second-instance alert does: opens one more group instance of the same
/// world, with the same access, region and settings as the full one.
/// </summary>
/// <remarks>
/// In Core so the bot can ask without depending on the VRChat project, whose gate does the opening. The
/// caller has already resolved who is pressing; the implementation checks Manage calendar again, the
/// permission that lets staff open instances from Modbot (Open now).
/// </remarks>
public interface ISecondInstances
{
    Task<SecondInstanceAnswer> OpenAsync(Guid alertId, StaffMember by, CancellationToken ct = default);
}
