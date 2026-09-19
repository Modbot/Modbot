namespace Modbot.Companion.LogReading;

/// <summary>
/// Recognises the handful of <c>[Behaviour]</c> lines Modbot cares about.
/// </summary>
/// <remarks>
/// <para><strong>What is read.</strong> Only lines tagged <c>[Behaviour]</c> — about 4% of a VRChat
/// log (754 lines out of 17,123 in the sample this parser was built against). Everything else —
/// tracking data, asset downloads, the OSC and Steam subsystems, VRChat's own HTTP calls — is read
/// past without being looked at.</para>
/// <para><strong>What is recognised inside that 4%.</strong> Instance transitions, the world's
/// readable name, player joins and leaves, which display name belongs to the local user, and
/// avatar switches. Nothing else. There
/// is no line shape here for chat, friends, invites, private worlds or anything a moderator does
/// outside the instances their group runs.</para>
/// <para><strong>What leaves the machine.</strong> Nothing, from here. This function returns an
/// in-memory description of the line; the raw text is dropped on the floor. What is eventually
/// transmitted, and to whom, is decided later — see <c>InstanceSessionTracker</c> for the
/// filtering and <c>CompanionEvent</c> for the exact field list.</para>
/// </remarks>
public static class BehaviourEventParser
{
    private const string BehaviourTag = "Behaviour";

    /// <summary>The line that carries the world's readable name, rather than its id.</summary>
    private const string WorldNamePrefix = "Joining or Creating Room:";

    /// <summary>
    /// Maps one log line to an event, or <c>null</c> when the line is not one Modbot recognises —
    /// which is the overwhelmingly common case and not an error.
    /// </summary>
    public static VRChatLogEvent? Parse(in VRChatLogLine line)
    {
        if (!string.Equals(line.Tag, BehaviourTag, StringComparison.Ordinal))
            return null;

        var message = line.Message;
        var at = line.Timestamp;

        // Ordered roughly by frequency, and by specificity where two prefixes overlap:
        // "OnPlayerLeftRoom" must be tested before "OnPlayerLeft", and "Joining or Creating Room"
        // before "Joining ".
        if (TryIdentified(message, "OnPlayerJoined ", out var joinedName, out var joinedId))
            return new PlayerJoinedEvent(at, joinedName, joinedId);

        if (message is "OnPlayerLeftRoom")
            return new RemotePlayerLeftRoomEvent(at);

        if (TryIdentified(message, "OnPlayerLeft ", out var leftName, out var leftId))
            return new PlayerLeftEvent(at, leftName, leftId);

        if (message is "OnLeftRoom")
            return new LocalPlayerLeftRoomEvent(at);

        if (message is "OnPlayerEnteredRoom")
            return new RemotePlayerEnteredRoomEvent(at);

        if (TryBetween(message, "Initialized PlayerAPI \"", "\" is local", out var localName))
            return new LocalPlayerIdentifiedEvent(at, localName);

        if (TryAfter(message, "Destination set: ", out var destination))
            return new DestinationSetEvent(at, destination);

        // "Joining or Creating Room: The Black Cat" is the world's readable name, not a location,
        // and it has to be tested first because it also starts with "Joining ". It was skipped
        // outright until 2026-09-19, when saved clips began being named after the world: a
        // moderator looking through a folder an hour later can pick out "The Black Cat" and cannot
        // pick out wrld_4cf554b4-430c-4f8f-b53e-1f294eed230b.
        if (message.StartsWith(WorldNamePrefix, StringComparison.Ordinal))
        {
            var worldName = message[WorldNamePrefix.Length..].Trim();

            // A line with no name after the colon is not a world name and must not fall through to
            // the location reading below, which would take "or Creating Room:" for a location.
            return worldName.Length == 0 ? null : new WorldNameEvent(at, worldName);
        }

        if (TryAfter(message, "Joining ", out var joining))
            return new JoiningInstanceEvent(at, joining);

        // "Switching to network region us (…)" shares this prefix and is not an avatar switch;
        // requiring the separator excludes it.
        if (TryAfter(message, "Switching ", out var switching)
            && switching.Contains(" to avatar ", StringComparison.Ordinal))
        {
            return new AvatarSwitchedEvent(at, switching);
        }

        return null;
    }

    /// <summary>
    /// Pulls a display name and a VRChat user id out of a <c>&lt;prefix&gt; &lt;name&gt; (&lt;id&gt;)</c>
    /// line.
    /// </summary>
    /// <remarks>
    /// <para>Three rules, each of which exists because breaking it produces wrong data rather than
    /// an error.</para>
    /// <para><strong>Never split on whitespace.</strong> Real display names in one sample included
    /// <c>~ RedZu ~</c>, <c>ΛƧƬΛ</c>, <c>bin¹</c> and <c>Hawk Echos</c>. Spaces, tildes,
    /// superscripts and non-Latin scripts are all ordinary.</para>
    /// <para><strong>Anchor on the last bracket, not the first.</strong> A display name is
    /// attacker-controlled and may itself contain something shaped like an id in brackets. Taking
    /// the first match would let anybody attribute their presence to somebody else simply by
    /// renaming themselves.</para>
    /// <para><strong>Never validate the id's shape.</strong> VRChat changed its id format years ago
    /// and legacy ids — held by exactly the oldest, most established members of a community —
    /// follow no structure at all. A <c>usr_</c> prefix check would work perfectly in testing and
    /// then silently drop the founders. So the id is whatever sits between the final <c>(</c> and
    /// the <c>)</c> that ends the line. Foundation spec section 3.1.1.</para>
    /// </remarks>
    private static bool TryIdentified(string message, string prefix, out string displayName, out string userId)
    {
        displayName = string.Empty;
        userId = string.Empty;

        if (!message.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        var rest = message.AsSpan(prefix.Length);
        if (rest.IsEmpty || rest[^1] != ')')
            return false;

        var open = rest.LastIndexOf('(');
        if (open < 0)
            return false;

        var id = rest[(open + 1)..^1];
        if (id.IsEmpty)
            return false;

        displayName = rest[..open].Trim().ToString();
        userId = id.ToString();
        return true;
    }

    private static bool TryAfter(string message, string prefix, out string tail)
    {
        if (message.StartsWith(prefix, StringComparison.Ordinal) && message.Length > prefix.Length)
        {
            tail = message[prefix.Length..];
            return true;
        }

        tail = string.Empty;
        return false;
    }

    /// <summary>
    /// Extracts the text between a prefix and a suffix, anchoring the suffix at the end of the
    /// line so that a display name containing the suffix text cannot truncate it.
    /// </summary>
    private static bool TryBetween(string message, string prefix, string suffix, out string inner)
    {
        inner = string.Empty;

        if (!message.StartsWith(prefix, StringComparison.Ordinal)
            || !message.EndsWith(suffix, StringComparison.Ordinal)
            || message.Length < prefix.Length + suffix.Length)
        {
            return false;
        }

        inner = message[prefix.Length..^suffix.Length];
        return true;
    }
}
