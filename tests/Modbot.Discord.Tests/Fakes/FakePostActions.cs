using System.Collections.Concurrent;
using Modbot.Core.Discord;
using Modbot.Core.Posts;

namespace Modbot.Discord.Tests.Fakes;

/// <summary>
/// Stands in for the API's post code: records what the bot asked for and answers as told. The
/// preview is built with <see cref="PostTexts.Discord(string?, string, string?)"/>, the function the
/// real check and the Discord sender both use. A key saves one post, as the real one does.
/// </summary>
public sealed class FakePostActions : IPostActions
{
    private readonly ConcurrentDictionary<string, Guid> _saved = new(StringComparer.Ordinal);

    /// <summary>Set to make every preview and every save refuse with these sentences.</summary>
    public List<string> Problems { get; } = [];

    /// <summary>The channel's name, as Modbot has it.</summary>
    public string? ChannelName { get; set; } = "announcements";

    /// <summary>Set to make a save throw, as a database that went away does.</summary>
    public Exception? SaveThrows { get; set; }

    /// <summary>What the list answers.</summary>
    public PostListAnswer List { get; set; } = new([], 0, [], 0);

    public List<(PostDraft Draft, StaffMember By)> Previews { get; } = [];

    /// <summary>Every save asked for, repeats included.</summary>
    public List<(string Key, PostDraft Draft, StaffMember By)> SaveCalls { get; } = [];

    /// <summary>The posts that were actually saved: one per key.</summary>
    public List<(string Key, PostDraft Draft, StaffMember By)> Saved { get; } = [];

    public List<(int Most, StaffMember By)> Lists { get; } = [];

    public Task<PostPreviewAnswer> PreviewAsync(PostDraft draft, StaffMember by, CancellationToken ct = default)
    {
        Previews.Add((draft, by));

        return Task.FromResult(Problems.Count > 0
            ? new PostPreviewAnswer([.. Problems], null, ChannelName)
            : new PostPreviewAnswer([], PostTexts.Discord(draft.Title, draft.Text, null), ChannelName));
    }

    public Task<PostNowAnswer> PostNowAsync(string key, PostDraft draft, StaffMember by, CancellationToken ct = default)
    {
        SaveCalls.Add((key, draft, by));

        if (SaveThrows is { } thrown)
            throw thrown;

        if (Problems.Count > 0)
            return Task.FromResult(new PostNowAnswer(false, false, [.. Problems]));

        var id = Guid.NewGuid();

        if (!_saved.TryAdd(key, id))
            return Task.FromResult(new PostNowAnswer(false, true, [], _saved[key]));

        Saved.Add((key, draft, by));
        return Task.FromResult(new PostNowAnswer(true, false, [], id));
    }

    public Task<PostListAnswer> ListAsync(int most, StaffMember by, CancellationToken ct = default)
    {
        Lists.Add((most, by));
        return Task.FromResult(List);
    }
}
