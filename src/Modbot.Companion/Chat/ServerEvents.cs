using System.Runtime.CompilerServices;

namespace Modbot.Companion.Chat;

/// <summary>One named block of a server's event stream: <c>event: text</c> then <c>data: {...}</c>.</summary>
public sealed record ServerEvent(string Name, string Data);

/// <summary>Reads the blocks of an event stream, one at a time, as they arrive.</summary>
public static class ServerEvents
{
    /// <summary>
    /// The events in <paramref name="reader"/>, each handed over as soon as its blank line has been
    /// read. A block with no name is called <c>message</c>, as the format says; a block with no data is
    /// skipped; a line starting with a colon is a comment and is skipped.
    /// </summary>
    public static async IAsyncEnumerable<ServerEvent> ReadAsync(
        TextReader reader,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string? name = null;
        var data = new List<string>();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);

            if (line is null)
            {
                // The stream ended; a last block that was never closed with a blank line still counts.
                if (data.Count > 0)
                    yield return new ServerEvent(name ?? "message", string.Join('\n', data));

                yield break;
            }

            if (line.Length == 0)
            {
                if (data.Count > 0)
                    yield return new ServerEvent(name ?? "message", string.Join('\n', data));

                name = null;
                data.Clear();
                continue;
            }

            if (line[0] == ':')
                continue;

            var colon = line.IndexOf(':');
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? string.Empty : line[(colon + 1)..];

            // One space after the colon belongs to the format, not to the value.
            if (value.StartsWith(' '))
                value = value[1..];

            if (field == "event")
                name = value;
            else if (field == "data")
                data.Add(value);
        }
    }
}
