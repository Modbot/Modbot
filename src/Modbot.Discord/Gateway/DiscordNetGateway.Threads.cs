using System.Globalization;
using Discord;

namespace Modbot.Discord.Gateway;

/// <summary>Public threads started on a message the bot posted.</summary>
public sealed partial class DiscordNetGateway
{
    /// <summary>Discord's longest thread name.</summary>
    private const int MaxThreadNameLength = 100;

    private static readonly ChannelNeed StartThread = new(
        "start the thread",
        "start threads in that channel",
        "View Channels, Read Message History and Create Public Threads");

    public Task<DiscordPostOutcome> StartThreadOnMessageAsync(
        string channelId, string messageId, string name, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (!ulong.TryParse(messageId, NumberStyles.None, CultureInfo.InvariantCulture, out var message))
            return Task.FromResult(DiscordPostOutcome.Failed("That is not a Discord message id.", permanent: true));

        var trimmed = name.Trim();
        var threadName = trimmed.Length <= MaxThreadNameLength ? trimmed : trimmed[..MaxThreadNameLength];

        return InChannelAsync(channelId, StartThread, async channel =>
        {
            if (channel is not ITextChannel parent)
            {
                return DiscordPostOutcome.Failed(
                    "A thread can only be started from a text channel, not from a thread or a forum.", permanent: true);
            }

            if (await parent.GetMessageAsync(message, options: Request(ct)).ConfigureAwait(false) is not { } found)
                return DiscordPostOutcome.Failed("That message is gone.", permanent: true);

            // A week without a word archives it; writing in it opens it again.
            var thread = await parent.CreateThreadAsync(
                    threadName,
                    ThreadType.PublicThread,
                    ThreadArchiveDuration.OneWeek,
                    message: found,
                    options: Request(ct))
                .ConfigureAwait(false);

            return DiscordPostOutcome.Posted(Text(thread.Id));
        });
    }
}
