using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Nodes;

namespace Modbot.Companion.Presentation;

/// <summary>
/// Where each pop-up chat after the first was last left, by its number, so a chat that opens with that
/// number again opens there. The first chat keeps its own fields (<c>overlayChatSpot</c> and
/// <c>desktopChatSpot</c>); the rest are saved as one object each, <c>overlayChatSpots</c> for the
/// headset and <c>desktopChatSpots</c> for the monitor, with the number as the name of each spot.
/// </summary>
/// <typeparam name="T">A headset placement, or a window's spot on a monitor.</typeparam>
/// <remarks>A number nobody has moved a chat in has no spot: the chat then opens a step off the last (<see cref="Modbot.Companion.Chat.ChatCascade"/>).</remarks>
public sealed class ChatSpots<T> where T : class
{
    private readonly ImmutableDictionary<int, T> _byNumber;

    private ChatSpots(ImmutableDictionary<int, T> byNumber) => _byNumber = byNumber;

    /// <summary>Nothing moved: every number opens at its own cascade spot.</summary>
    public static ChatSpots<T> None { get; } = new(ImmutableDictionary<int, T>.Empty);

    /// <summary>How many numbers have a spot.</summary>
    public int Count => _byNumber.Count;

    /// <summary>The spot left for this number, or null.</summary>
    public T? For(int number) => _byNumber.GetValueOrDefault(number);

    /// <summary>The same spots with this number's spot replaced.</summary>
    public ChatSpots<T> With(int number, T spot)
    {
        ArgumentNullException.ThrowIfNull(spot);

        return new ChatSpots<T>(_byNumber.SetItem(number, spot));
    }

    /// <summary>The object as it is written to the file: only the numbers that have a spot.</summary>
    public JsonObject ToJson(Func<T, JsonObject> write)
    {
        ArgumentNullException.ThrowIfNull(write);

        var json = new JsonObject();

        foreach (var (number, spot) in _byNumber.OrderBy(pair => pair.Key))
            json[number.ToString(CultureInfo.InvariantCulture)] = write(spot);

        return json;
    }

    /// <summary>
    /// Reads the object. A name that is not a number from 2 up, and a spot that cannot be read, are
    /// left out: that number then opens at its cascade spot.
    /// </summary>
    public static ChatSpots<T> FromJson(JsonNode? node, Func<JsonNode?, T?> read)
    {
        ArgumentNullException.ThrowIfNull(read);

        if (node is not JsonObject json)
            return None;

        var spots = ImmutableDictionary.CreateBuilder<int, T>();

        foreach (var (name, value) in json)
        {
            if (int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                && number >= 2
                && read(value) is { } spot)
            {
                spots[number] = spot;
            }
        }

        return new ChatSpots<T>(spots.ToImmutable());
    }
}
