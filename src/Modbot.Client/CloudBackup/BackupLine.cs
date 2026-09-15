using System.Text.Json.Serialization;
using Modbot.Client.LogReading;
using Modbot.Client.Time;

namespace Modbot.Client.CloudBackup;

/// <summary>
/// One line as it is queued on disk and sent to Modbot Cloud — the complete list of what is sent
/// about it (cloud log backup spec 2).
/// </summary>
/// <remarks>
/// <para><strong>What leaves the machine.</strong> These six fields, for every line read while the
/// backup is on, to Modbot Cloud only. This type reads nothing from disk itself; it is the shape the
/// outbox writes and the backup sends.</para>
/// </remarks>
/// <param name="File">VRChat's name for the log file. Never its folder, which holds your Windows account name.</param>
/// <param name="Offset">Where the line starts in that file.</param>
/// <param name="Text">
/// The line as VRChat wrote it, with your user profile folder replaced by <c>%USERPROFILE%</c> and
/// cut to <see cref="MaxTextLength"/> characters.
/// </param>
/// <param name="LoggedAt">The line's timestamp as written, with no offset, or null when it has none.</param>
/// <param name="UtcOffsetMinutes">This PC's UTC offset at that time.</param>
/// <param name="Event">What the client recognised in the line, when anything.</param>
public sealed record BackupLine(
    [property: JsonPropertyName("file")] string File,
    [property: JsonPropertyName("offset")] long Offset,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("loggedAt")] DateTime? LoggedAt,
    [property: JsonPropertyName("utcOffsetMinutes")] int? UtcOffsetMinutes,
    [property: JsonPropertyName("event")] BackupEvent? Event)
{
    public const int MaxTextLength = 16_384;

    public const string UserProfileStandIn = "%USERPROFILE%";

    /// <summary>Builds the queued shape of one read line.</summary>
    /// <param name="userProfile">The user profile folder to hide, e.g. <c>C:\Users\rin</c>.</param>
    public static BackupLine From(ReadLogLine line, LogTimestampConverter timestamps, string? userProfile)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(timestamps);

        var text = HideUserProfile(line.Text, userProfile);
        if (text.Length > MaxTextLength)
            text = text[..MaxTextLength];

        DateTime? loggedAt = null;
        int? offset = null;
        if (line.Parsed is { } parsed)
        {
            loggedAt = DateTime.SpecifyKind(parsed.Timestamp, DateTimeKind.Unspecified);
            offset = (int)timestamps.ToInstant(parsed.Timestamp).Offset.TotalMinutes;
        }

        return new BackupLine(line.File, line.Offset, text, loggedAt, offset, BackupEvent.From(line.Event, userProfile));
    }

    /// <summary>
    /// Replaces the user profile folder, written with either slash, wherever it appears. VRChat
    /// writes file paths into its log now and then, and the folder is named after the Windows
    /// account.
    /// </summary>
    public static string HideUserProfile(string text, string? userProfile)
    {
        if (string.IsNullOrEmpty(userProfile) || userProfile.Length < 4)
            return text;

        var result = text;
        if (result.Contains(userProfile, StringComparison.OrdinalIgnoreCase))
            result = result.Replace(userProfile, UserProfileStandIn, StringComparison.OrdinalIgnoreCase);

        var forward = userProfile.Replace('\\', '/');
        if (!string.Equals(forward, userProfile, StringComparison.Ordinal) && result.Contains(forward, StringComparison.OrdinalIgnoreCase))
            result = result.Replace(forward, UserProfileStandIn, StringComparison.OrdinalIgnoreCase);

        return result;
    }
}

/// <summary>The client's own parse of one line.</summary>
/// <param name="Type">The client's name for it, e.g. <c>PlayerJoined</c>.</param>
/// <param name="Data">The values it read out of the line, all of which are in the line's text too.</param>
public sealed record BackupEvent(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("data")] IReadOnlyDictionary<string, string> Data)
{
    public static BackupEvent? From(VRChatLogEvent? logEvent, string? userProfile) => logEvent switch
    {
        null => null,
        PlayerJoinedEvent e => new("PlayerJoined", new Dictionary<string, string> { ["displayName"] = e.DisplayName, ["userId"] = e.UserId }),
        PlayerLeftEvent e => new("PlayerLeft", new Dictionary<string, string> { ["displayName"] = e.DisplayName, ["userId"] = e.UserId }),
        LocalPlayerLeftRoomEvent => new("LocalPlayerLeftRoom", Empty),
        RemotePlayerLeftRoomEvent => new("RemotePlayerLeftRoom", Empty),
        RemotePlayerEnteredRoomEvent => new("RemotePlayerEnteredRoom", Empty),
        DestinationSetEvent e => new("DestinationSet", new Dictionary<string, string> { ["location"] = e.Location }),
        JoiningInstanceEvent e => new("JoiningInstance", new Dictionary<string, string> { ["location"] = e.Location }),
        LocalPlayerIdentifiedEvent e => new("LocalPlayerIdentified", new Dictionary<string, string> { ["displayName"] = e.DisplayName }),
        AvatarSwitchedEvent e => new("AvatarSwitched", new Dictionary<string, string> { ["payload"] = BackupLine.HideUserProfile(e.Payload, userProfile) }),

        // A shape added to the parser later: sent by its class name, which Cloud keeps as
        // unrecognised until it learns the name.
        _ => new(logEvent.GetType().Name.EndsWith("Event", StringComparison.Ordinal)
            ? logEvent.GetType().Name[..^"Event".Length]
            : logEvent.GetType().Name, Empty),
    };

    private static readonly IReadOnlyDictionary<string, string> Empty = new Dictionary<string, string>();
}
