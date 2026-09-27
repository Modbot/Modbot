using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VRChat.API.Model;

namespace Modbot.VRChat.GroupPage;

/// <summary>
/// The body of a profile edit: only the fields being changed, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this exists rather than a plain <see cref="UpdateGroupRequest"/>.</strong> The SDK marks
/// <c>bannerId</c>, <c>iconId</c> and <c>nameplateId</c> to be written even when they are null, and
/// its serializer keeps nulls, so a request that only meant to change the description goes out as
/// <c>{"bannerId":null,"description":"…","iconId":null,"nameplateId":null}</c> (checked against
/// VRChat.API 2.21.1-nightly.41 on 2026-09-27). That is an instruction to VRChat to take the
/// group's banner, icon and nameplate away. This type writes the fields that were set and no
/// others, through the SDK's own serializer, so a Save never touches a picture.
/// </para>
/// <para>
/// Pictures are not editable from Modbot yet. When they are, add them here as fields that are
/// written only when set, never as nulls.
/// </para>
/// </remarks>
[JsonConverter(typeof(GroupProfileChangeWriter))]
public sealed class GroupProfileChange : UpdateGroupRequest
{
    public GroupProfileChange(
        string? name = null,
        string? description = null,
        string? rules = null,
        List<string>? languages = null,
        List<string>? links = null,
        GroupJoinState? joinState = null)
        : base(
            description: description!,
            joinState: joinState,
            languages: languages!,
            links: links!,
            name: name!,
            rules: rules!)
    {
    }

    /// <summary>Whether anything is being changed at all.</summary>
    public bool IsEmpty =>
        Name is null && Description is null && Rules is null && Languages is null && Links is null && JoinState is null;
}

/// <summary>Writes a <see cref="GroupProfileChange"/> with only the fields that were set.</summary>
internal sealed class GroupProfileChangeWriter : JsonConverter
{
    public override bool CanConvert(Type objectType) => objectType == typeof(GroupProfileChange);

    public override bool CanRead => false;

    public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
        => throw new NotSupportedException("A profile change is only ever sent, never read.");

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
    {
        ArgumentNullException.ThrowIfNull(serializer);

        if (value is not GroupProfileChange change)
        {
            writer.WriteNull();
            return;
        }

        var body = new JObject();

        if (change.Name is not null) body["name"] = change.Name;
        if (change.Description is not null) body["description"] = change.Description;
        if (change.Rules is not null) body["rules"] = change.Rules;
        if (change.Languages is not null) body["languages"] = new JArray(change.Languages);
        if (change.Links is not null) body["links"] = new JArray(change.Links);

        // Through the serializer, so the enum is written the way the SDK writes it ("request").
        if (change.JoinState is { } joinState) body["joinState"] = JToken.FromObject(joinState, serializer);

        body.WriteTo(writer);
    }
}
