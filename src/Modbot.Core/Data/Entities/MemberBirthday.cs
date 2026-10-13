namespace Modbot.Core.Data.Entities;

/// <summary>
/// A birthday a member told Modbot with <c>/birthday set</c>: a month and a day, never a year. The
/// table is <c>member_birthday</c>, one row for each Discord account.
/// </summary>
/// <remarks>
/// <para>
/// <strong>No year, so no age.</strong> Modbot never asks for the year and has nowhere to keep it.
/// </para>
/// <para>
/// The birthday role is given on the day and taken away on a later one. The role and the day it was
/// given are kept on the row, so the next pass knows whom to take it from even after the setting
/// named a different role or the member took their birthday off.
/// </para>
/// <para>
/// Names a Discord account, so a purge of that account deletes its row.
/// </para>
/// </remarks>
public class MemberBirthday
{
    /// <summary>The member's Discord id. An opaque snowflake; never parsed.</summary>
    public string DiscordUserId { get; set; } = string.Empty;

    /// <summary>1 to 12.</summary>
    public int Month { get; set; }

    /// <summary>1 to 31, a day that exists in <see cref="Month"/>. 29 February is allowed.</summary>
    public int Day { get; set; }

    /// <summary>When the member set it.</summary>
    public DateTimeOffset SetAt { get; set; }

    /// <summary>The role Modbot gave for the day and has not taken away yet. Null when none is held.</summary>
    public string? RoleGivenId { get; set; }

    /// <summary>The day, in the birthday setting's time zone, that <see cref="RoleGivenId"/> was given.</summary>
    public DateOnly? RoleGivenOn { get; set; }

    /// <summary>
    /// Whether the member took their birthday off while Modbot could not yet take the role back. The
    /// month and day are gone; the row stays only so the next pass takes the role off, then deletes it.
    /// </summary>
    public bool IsForgotten => Month == 0;

    /// <summary>Drops the month and day but keeps the role to take back. See <see cref="IsForgotten"/>.</summary>
    public void Forget()
    {
        Month = 0;
        Day = 0;
    }
}

/// <summary>What counts as a birthday, and when it is celebrated.</summary>
public static class BirthdayRules
{
    /// <summary>The longest the message may be. Discord's own limit is 2,000, and the members fill-in takes room.</summary>
    public const int MaxMessageLength = 500;

    /// <summary>What the message says when nobody has written one. <c>{members}</c> is the members with a birthday today.</summary>
    public const string DefaultMessage = "Happy birthday {members}!";

    /// <summary>The words in the message that stand for the members.</summary>
    public const string MembersToken = "{members}";

    /// <summary>How many members one post names, so a post stays under Discord's limit on length.</summary>
    public const int MostPerPost = 40;

    /// <summary>Whether the month and day are a real date. 29 February counts: it is somebody's birthday.</summary>
    public static bool IsDate(int month, int day)
        => month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(2024, month);

    /// <summary>
    /// Whether <paramref name="date"/> is the day to celebrate this birthday. 29 February is
    /// celebrated on 28 February in a year with no 29th.
    /// </summary>
    public static bool FallsOn(int month, int day, DateOnly date)
    {
        if (month == 2 && day == 29 && !DateTime.IsLeapYear(date.Year))
            return date.Month == 2 && date.Day == 28;

        return date.Month == month && date.Day == day;
    }

    /// <summary>The message with the members put in. A message with no <c>{members}</c> in it gets none.</summary>
    public static string Fill(string? message, IReadOnlyList<string> mentions)
    {
        ArgumentNullException.ThrowIfNull(mentions);

        var text = string.IsNullOrWhiteSpace(message) ? DefaultMessage : message;
        return text.Replace(MembersToken, string.Join(", ", mentions), StringComparison.Ordinal);
    }
}
