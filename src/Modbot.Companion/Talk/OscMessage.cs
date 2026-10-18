using System.Buffers.Binary;
using System.Text;

namespace Modbot.Companion.Talk;

/// <summary>
/// One OSC message: an address such as <c>/avatar/parameters/MuteSelf</c> and what it carries.
/// </summary>
/// <remarks>
/// OSC is the small packet format VRChat uses to talk to other programs on the same PC. Nothing here
/// reads a file or opens a connection; it turns bytes into a message and a message into bytes.
/// </remarks>
/// <param name="Address">The path the message is for.</param>
/// <param name="Arguments">
/// What it carries: <see cref="int"/>, <see cref="long"/>, <see cref="float"/>, <see cref="double"/>,
/// <see cref="string"/>, <see cref="bool"/> or a <see cref="T:byte[]"/>.
/// </param>
public sealed record OscMessage(string Address, IReadOnlyList<object> Arguments)
{
    /// <summary>
    /// The first argument as a true or false, or null when there is none or it is not a yes/no.
    /// VRChat sends a bool avatar parameter as OSC's own true and false, and some programs send it as
    /// 0 and 1, so both are read.
    /// </summary>
    public bool? FirstAsBool => Arguments.Count == 0
        ? null
        : Arguments[0] switch
        {
            bool b => b,
            int i => i != 0,
            float f => f != 0f,
            _ => null,
        };
}

/// <summary>
/// Reads and writes OSC packets. Never throws for bad input: what cannot be read is left out.
/// </summary>
/// <remarks>
/// Only what the Talk feature needs is written (<c>int</c>, <c>float</c>, <c>string</c>, <c>bool</c>);
/// reading also takes the other plain types and bundles, because VRChat may wrap messages in one.
/// What leaves the machine: nothing. This only builds and reads byte arrays.
/// </remarks>
public static class OscCodec
{
    private const string BundleTag = "#bundle";

    /// <summary>One message as bytes.</summary>
    /// <exception cref="ArgumentException">An argument of a kind OSC writing here does not support.</exception>
    public static byte[] Write(string address, params object[] arguments)
    {
        ArgumentException.ThrowIfNullOrEmpty(address);
        ArgumentNullException.ThrowIfNull(arguments);

        var bytes = new List<byte>();
        WriteString(bytes, address);

        var tags = new StringBuilder(",");
        foreach (var argument in arguments)
        {
            tags.Append(argument switch
            {
                int => 'i',
                float => 'f',
                string => 's',
                bool b => b ? 'T' : 'F',
                _ => throw new ArgumentException($"An OSC argument of type {argument?.GetType().Name ?? "null"} is not supported."),
            });
        }

        WriteString(bytes, tags.ToString());

        foreach (var argument in arguments)
        {
            switch (argument)
            {
                case int i:
                    WriteInt(bytes, i);
                    break;
                case float f:
                    WriteInt(bytes, BitConverter.SingleToInt32Bits(f));
                    break;
                case string s:
                    WriteString(bytes, s);
                    break;
            }
        }

        return [.. bytes];
    }

    /// <summary>
    /// Every message in a packet: the packet itself if it is one message, or what is inside it if it
    /// is a bundle. A packet that cannot be read gives an empty list.
    /// </summary>
    public static IReadOnlyList<OscMessage> Read(ReadOnlySpan<byte> packet)
    {
        var messages = new List<OscMessage>();
        ReadInto(packet, messages, depth: 0);
        return messages;
    }

    private static void ReadInto(ReadOnlySpan<byte> packet, List<OscMessage> messages, int depth)
    {
        if (packet.IsEmpty || depth > 4)
            return;

        if (packet[0] == (byte)'#')
        {
            ReadBundle(packet, messages, depth);
            return;
        }

        if (ReadMessage(packet) is { } message)
            messages.Add(message);
    }

    private static void ReadBundle(ReadOnlySpan<byte> packet, List<OscMessage> messages, int depth)
    {
        var at = 0;
        if (!TryReadString(packet, ref at, out var tag) || tag != BundleTag)
            return;

        // The 8-byte time tag says when to act; this acts at once, so it is skipped.
        at += 8;

        while (at + 4 <= packet.Length)
        {
            var size = BinaryPrimitives.ReadInt32BigEndian(packet[at..]);
            at += 4;

            if (size <= 0 || at + size > packet.Length)
                return;

            ReadInto(packet.Slice(at, size), messages, depth + 1);
            at += size;
        }
    }

    private static OscMessage? ReadMessage(ReadOnlySpan<byte> packet)
    {
        var at = 0;
        if (!TryReadString(packet, ref at, out var address) || !address.StartsWith('/'))
            return null;

        // A message with no type tags at all is allowed by old OSC and carries nothing.
        if (at >= packet.Length)
            return new OscMessage(address, []);

        if (!TryReadString(packet, ref at, out var tags) || !tags.StartsWith(','))
            return null;

        var arguments = new List<object>();
        foreach (var tag in tags.AsSpan(1))
        {
            switch (tag)
            {
                case 'i' when at + 4 <= packet.Length:
                    arguments.Add(BinaryPrimitives.ReadInt32BigEndian(packet[at..]));
                    at += 4;
                    break;
                case 'f' when at + 4 <= packet.Length:
                    arguments.Add(BinaryPrimitives.ReadSingleBigEndian(packet[at..]));
                    at += 4;
                    break;
                case 'h' when at + 8 <= packet.Length:
                    arguments.Add(BinaryPrimitives.ReadInt64BigEndian(packet[at..]));
                    at += 8;
                    break;
                case 'd' when at + 8 <= packet.Length:
                    arguments.Add(BinaryPrimitives.ReadDoubleBigEndian(packet[at..]));
                    at += 8;
                    break;
                case 's' when TryReadString(packet, ref at, out var text):
                    arguments.Add(text);
                    break;
                case 'b' when TryReadBlob(packet, ref at, out var blob):
                    arguments.Add(blob);
                    break;
                case 'T':
                    arguments.Add(true);
                    break;
                case 'F':
                    arguments.Add(false);
                    break;
                case 'N' or 'I':
                    // Nil and "infinitum" carry no data and no meaning here.
                    break;
                default:
                    // A type this does not know has an unknown size, so nothing after it can be read.
                    return null;
            }
        }

        return new OscMessage(address, arguments);
    }

    private static bool TryReadString(ReadOnlySpan<byte> packet, ref int at, out string text)
    {
        text = "";
        var end = packet[at..].IndexOf((byte)0);
        if (end < 0)
            return false;

        text = Encoding.UTF8.GetString(packet.Slice(at, end));

        // The string and its zero are padded to a multiple of four bytes.
        var next = at + ((end + 4) & ~3);
        if (next > packet.Length)
            return false;

        at = next;
        return true;
    }

    private static bool TryReadBlob(ReadOnlySpan<byte> packet, ref int at, out byte[] blob)
    {
        blob = [];
        if (at + 4 > packet.Length)
            return false;

        var size = BinaryPrimitives.ReadInt32BigEndian(packet[at..]);
        if (size < 0 || at + 4 + size > packet.Length)
            return false;

        blob = packet.Slice(at + 4, size).ToArray();
        at += 4 + ((size + 3) & ~3);
        return true;
    }

    private static void WriteInt(List<byte> bytes, int value)
    {
        Span<byte> four = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(four, value);
        bytes.AddRange(four.ToArray());
    }

    private static void WriteString(List<byte> bytes, string text)
    {
        var raw = Encoding.UTF8.GetBytes(text);
        bytes.AddRange(raw);

        // A zero to end it, then zeros up to a multiple of four.
        var padding = 4 - (raw.Length % 4);
        for (var i = 0; i < padding; i++)
            bytes.Add(0);
    }
}
