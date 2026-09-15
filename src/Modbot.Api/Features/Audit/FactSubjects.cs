using Modbot.Core.Data.Entities;

namespace Modbot.Api.Features.Audit;

/// <summary>
/// What each kind of fact is <em>about</em> — a person, a room, the group, a role, an account.
/// </summary>
/// <remarks>
/// <para>
/// The subject column holds whatever the source put there. VRChat documents its own
/// <c>targetId</c> only as "typically a UserID, GroupID, GroupRoleID, or Location", and Modbot's
/// own entries put a Modbot account id there. Nothing in the value says which, and reading the
/// shape of an id to find out is exactly what spec 3.1.1 forbids — a legacy id looks like
/// nothing in particular.
/// </para>
/// <para>
/// So the answer comes from the fact's type, which is known. This is what lets a log row's
/// subject be clickable and open the right thing: a room event opens the room, a member event
/// opens the person.
/// </para>
/// <para>
/// The default is <see cref="SubjectKind.Person"/>, because the overwhelming majority of facts
/// are about a person and a type added later without an entry here will far more often be one of
/// those than not.
/// </para>
/// </remarks>
public static class FactSubjects
{
    private static readonly Dictionary<string, SubjectKind> Kinds = new(StringComparer.Ordinal)
    {
        // The subject is the location string itself: VRChat puts it in targetId for these four,
        // and the world and room columns are filled beside it rather than instead of it.
        [FactType.GroupInstanceCreated] = SubjectKind.Instance,
        [FactType.GroupInstanceClosed] = SubjectKind.Instance,
        [FactType.GroupInstanceUpdated] = SubjectKind.Instance,
        [FactType.GroupInstanceAnnouncement] = SubjectKind.Instance,

        // About the managed group as a whole, not about anybody in it.
        [FactType.GroupInfoChanged] = SubjectKind.Group,
        [FactType.MembersSnapshot] = SubjectKind.Group,
        [FactType.BansSnapshot] = SubjectKind.Group,
        [FactType.GroupPostCreated] = SubjectKind.Group,
        [FactType.GroupPostDeleted] = SubjectKind.Group,
        [FactType.CalendarEventCreated] = SubjectKind.Group,
        [FactType.CalendarEventDeleted] = SubjectKind.Group,
        [FactType.CalendarEventSeriesUpdated] = SubjectKind.Group,
        [FactType.CalendarEventSeriesDeleted] = SubjectKind.Group,

        // A VRChat group role. Its name is in the payload; the id is not a person's.
        [FactType.RoleUpdated] = SubjectKind.Role,

        // Modbot's own record of what happened inside Modbot. The subject is a staff account.
        [FactType.Login] = SubjectKind.Account,
        [FactType.LoginFailed] = SubjectKind.Account,
        [FactType.PasswordChanged] = SubjectKind.Account,
        [FactType.UsernameChanged] = SubjectKind.Account,
        [FactType.ContactChanged] = SubjectKind.Account,
        [FactType.VRChatLinked] = SubjectKind.Account,
        [FactType.UserCreated] = SubjectKind.Account,
        [FactType.UserDisabled] = SubjectKind.Account,
        [FactType.UserEnabled] = SubjectKind.Account,
        [FactType.UserRolesChanged] = SubjectKind.Account,
        [FactType.SignedOutEverywhere] = SubjectKind.Account,

        // Neither a person nor a place: an invite link, a Modbot role, a case file, a key, a
        // channel, a partition name. Clicking these opens nothing, and pretending otherwise
        // would open the wrong popup on somebody's id-shaped case file number.
        [FactType.UserInvited] = SubjectKind.Other,
        [FactType.UserInviteUsed] = SubjectKind.Other,
        [FactType.UserInviteRevoked] = SubjectKind.Other,
        [FactType.ResetLinkCreated] = SubjectKind.Other,
        [FactType.ResetLinkUsed] = SubjectKind.Other,
        [FactType.RoleCreated] = SubjectKind.Other,
        [FactType.RoleChanged] = SubjectKind.Other,
        [FactType.RoleDeleted] = SubjectKind.Other,
        [FactType.ApiKeyCreated] = SubjectKind.Other,
        [FactType.ApiKeyRevoked] = SubjectKind.Other,
        [FactType.WebhookCreated] = SubjectKind.Other,
        [FactType.WebhookChanged] = SubjectKind.Other,
        [FactType.WebhookSecretChanged] = SubjectKind.Other,
        [FactType.WebhookDeleted] = SubjectKind.Other,
        [FactType.WebhookDisabled] = SubjectKind.Other,
        [FactType.SettingsChanged] = SubjectKind.Other,
        [FactType.BanReasonsChanged] = SubjectKind.Other,
        [FactType.SyncFailed] = SubjectKind.Other,
        [FactType.RateLimitColdStop] = SubjectKind.Other,
        [FactType.WafBlocked] = SubjectKind.Other,
        [FactType.MigrationApplied] = SubjectKind.Other,
        [FactType.RetentionPruned] = SubjectKind.Other,
        [FactType.PartitionCreated] = SubjectKind.Other,
        [FactType.AiLimitReached] = SubjectKind.Other,
        [FactType.UserPurged] = SubjectKind.Other,
        [FactType.DiscordLogPosted] = SubjectKind.Other,
        [FactType.DiscordMembersSnapshot] = SubjectKind.Other,
        [FactType.DiscordMessagesBulkRemoved] = SubjectKind.Other,
        [FactType.DiscordChannelCreated] = SubjectKind.Other,
        [FactType.DiscordChannelChanged] = SubjectKind.Other,
        [FactType.DiscordChannelDeleted] = SubjectKind.Other,
        [FactType.DiscordRoleCreated] = SubjectKind.Other,
        [FactType.DiscordRoleChanged] = SubjectKind.Other,
        [FactType.DiscordRoleDeleted] = SubjectKind.Other,

        // The link role and prompt facts are about a Discord account, so they take the default:
        // a person, with the subject platform saying Discord, which opens the Discord person popup
        // rather than the VRChat one. The link facts themselves are about the VRChat person.
        [FactType.EvidenceAttached] = SubjectKind.Other,
        [FactType.EvidenceAccessed] = SubjectKind.Other,
        [FactType.EvidenceDestroyed] = SubjectKind.Other,
    };

    public static SubjectKind For(string type)
        => Kinds.TryGetValue(type, out var kind) ? kind : SubjectKind.Person;
}
