using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VRChat.API.Model;

namespace Modbot.VRChat.GroupPage;

/// <summary>
/// The body of a role edit: its name, description and permissions, and only the ones being set.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this exists rather than the SDK's own request.</strong>
/// The SDK's <see cref="UpdateGroupRoleRequest"/> has <c>isSelfAssignable</c> and <c>order</c> as
/// plain values that are always written, so a request that only meant to rename a role would also
/// tell VRChat the role is not self-assignable and belongs at the top. And its permissions are an
/// enum: a permission VRChat adds before the SDK knows it could not be sent back, so saving a role
/// would quietly take it away. This type writes the fields that were set and nothing else, and
/// writes permissions as VRChat's own words, whatever they are.
/// </para>
/// <para>
/// Used for both a new role and a change to one. VRChat's create and update take the same three
/// fields here; <see cref="ForCreate"/> gives the same body under the type the SDK's create takes.
/// </para>
/// </remarks>
[JsonConverter(typeof(GroupRoleChangeWriter))]
public sealed class GroupRoleChange : UpdateGroupRoleRequest, IGroupRoleBody
{
    public GroupRoleChange(string? name = null, string? description = null, IReadOnlyList<string>? permissions = null)
    {
        RoleName = name;
        RoleDescription = description;
        PermissionIds = permissions is null ? null : [.. permissions];
    }

    /// <summary>The role's name, or null to leave it.</summary>
    public string? RoleName { get; }

    /// <summary>The role's description, or null to leave it. An empty string clears it.</summary>
    public string? RoleDescription { get; }

    /// <summary>
    /// Every permission the role should have, as VRChat's ids (<c>group-bans-manage</c>), or null to
    /// leave them. The whole list, not the ones added: VRChat replaces it.
    /// </summary>
    public IReadOnlyList<string>? PermissionIds { get; }

    /// <summary>Whether anything is being changed at all.</summary>
    public bool IsEmpty => RoleName is null && RoleDescription is null && PermissionIds is null;

    /// <summary>The same body, as the type the SDK's create takes.</summary>
    public CreateGroupRoleRequest ForCreate() => new NewGroupRole(this);

    /// <summary>A new role: the same three fields, written the same way.</summary>
    [JsonConverter(typeof(GroupRoleChangeWriter))]
    private sealed class NewGroupRole(GroupRoleChange change) : CreateGroupRoleRequest, IGroupRoleBody
    {
        public string? RoleName => change.RoleName;
        public string? RoleDescription => change.RoleDescription;
        public IReadOnlyList<string>? PermissionIds => change.PermissionIds;
    }
}

/// <summary>The three fields a role body writes.</summary>
internal interface IGroupRoleBody
{
    string? RoleName { get; }
    string? RoleDescription { get; }
    IReadOnlyList<string>? PermissionIds { get; }
}

/// <summary>Writes a role body with only the fields that were set, permissions as plain text.</summary>
internal sealed class GroupRoleChangeWriter : JsonConverter
{
    public override bool CanConvert(Type objectType) => typeof(IGroupRoleBody).IsAssignableFrom(objectType);

    public override bool CanRead => false;

    public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
        => throw new NotSupportedException("A role change is only ever sent, never read.");

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
    {
        if (value is not IGroupRoleBody role)
        {
            writer.WriteNull();
            return;
        }

        var body = new JObject();

        if (role.RoleName is not null) body["name"] = role.RoleName;
        if (role.RoleDescription is not null) body["description"] = role.RoleDescription;
        if (role.PermissionIds is not null) body["permissions"] = new JArray(role.PermissionIds);

        body.WriteTo(writer);
    }
}
