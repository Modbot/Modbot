using System.Collections.Concurrent;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;

namespace Modbot.Discord.Tests.Fakes;

/// <summary>
/// Stands in for the API's moderation and note services: records what the bot asked for and
/// answers as told. Acts once per key, as the real service does.
/// </summary>
public sealed class FakeStaffActions : IStaffActions
{
    private readonly ConcurrentDictionary<string, StaffActionAnswer> _byKey = new(StringComparer.Ordinal);
    private long _nextNoteId = 1;

    /// <summary>The reasons offered, for every action that takes any.</summary>
    public List<StaffReason> Reasons { get; } =
    [
        new(Guid.Parse("0199a000-0000-7000-8000-000000000001"), "Harassment", "Targeting people.", false),
        new(Guid.Parse("0199a000-0000-7000-8000-000000000002"), "Other", "Say why.", true),
    ];

    /// <summary>Whether a reason must be picked.</summary>
    public bool ReasonRequired { get; set; } = true;

    /// <summary>Set to make every check refuse with this sentence.</summary>
    public string? Refusal { get; set; }

    /// <summary>What a run answers. Done by default.</summary>
    public StaffActionAnswer Answer { get; set; } = new(true, null);

    /// <summary>Every action actually sent: once per key.</summary>
    public List<(string Action, string Key, string UserId, IReadOnlyList<Guid> ReasonIds, string Note, StaffMember By)> Sent { get; } = [];

    /// <summary>Every run asked for, including repeats of a key.</summary>
    public int Runs { get; private set; }

    /// <summary>Every check asked for.</summary>
    public List<(string Action, string UserId, IReadOnlyList<Guid> ReasonIds, string Note)> Checks { get; } = [];

    /// <summary>Every note written.</summary>
    public List<(FactPlatform Platform, string UserId, string Text, StaffMember By)> Notes { get; } = [];

    /// <summary>Every watch started.</summary>
    public List<(FactPlatform Platform, string UserId, string Reason, DateTimeOffset? EndsAt, DateTimeOffset? FollowUpAt, StaffMember By)> Watches { get; } = [];

    /// <summary>Set to make every watch refuse with this sentence, as the service does for a person already watched.</summary>
    public string? WatchRefusal { get; set; }

    public Task<StaffReasons> ReasonsAsync(string action, CancellationToken ct = default)
        => Task.FromResult(action == "approve" ? StaffReasons.None : new StaffReasons([.. Reasons], action == "ban" || ReasonRequired));

    public Task<string?> CheckAsync(
        string action, string vrchatUserId, IReadOnlyList<Guid> reasonIds, string note, StaffMember by, CancellationToken ct = default)
    {
        Checks.Add((action, vrchatUserId, reasonIds, note));
        return Task.FromResult(Refusal);
    }

    public Task<StaffActionAnswer> RunAsync(
        string action,
        string key,
        string vrchatUserId,
        IReadOnlyList<Guid> reasonIds,
        string note,
        StaffMember by,
        CancellationToken ct = default)
    {
        Runs++;

        if (_byKey.TryGetValue(key, out var first))
            return Task.FromResult(first with { Repeat = true });

        Sent.Add((action, key, vrchatUserId, reasonIds, note, by));
        _byKey[key] = Answer;
        return Task.FromResult(Answer);
    }

    public Task<StaffNoteAnswer> WriteNoteAsync(
        FactPlatform platform, string userId, string text, StaffMember by, CancellationToken ct = default)
    {
        if (!by.Held.HasFlag(ModbotPermissions.WriteNotes) && !by.Held.HasFlag(ModbotPermissions.Administrator))
            return Task.FromResult(new StaffNoteAnswer(null, "You do not have permission to write notes."));

        Notes.Add((platform, userId, text, by));
        return Task.FromResult(new StaffNoteAnswer(_nextNoteId++, null));
    }

    public Task<StaffWatchAnswer> StartWatchAsync(
        FactPlatform platform,
        string userId,
        string reason,
        DateTimeOffset? endsAt,
        DateTimeOffset? followUpAt,
        StaffMember by,
        CancellationToken ct = default)
    {
        if (!by.Held.HasFlag(ModbotPermissions.WriteNotes) && !by.Held.HasFlag(ModbotPermissions.Administrator))
            return Task.FromResult(new StaffWatchAnswer(null, "You do not have permission to watch people."));

        if (WatchRefusal is { } refusal)
            return Task.FromResult(new StaffWatchAnswer(null, refusal));

        Watches.Add((platform, userId, reason, endsAt, followUpAt, by));
        return Task.FromResult(new StaffWatchAnswer(Guid.CreateVersion7(), null));
    }
}
