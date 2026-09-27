using System.Text.Json.Nodes;
using VRChat.API.Model;

namespace Modbot.Api.Features.GroupPage;

/// <summary>The group's profile as Modbot last stored it, to tell a change from a restatement.</summary>
public sealed record GroupProfileState(
    string? Name,
    string? Description,
    string? Rules,
    IReadOnlyList<string> Languages,
    IReadOnlyList<string> Links,
    string? JoinState);

/// <summary>
/// VRChat's limits on the group's page, and the checks made before anything is sent.
/// </summary>
/// <remarks>
/// <para>
/// Every limit here is one VRChat documents: the lengths are in the SDK's generated checks and in
/// VRChat's API description (<c>UpdateGroupRequest</c>: name 3 to 64 characters, description up to
/// 250, at most three languages of up to three letters, at most three links). The rules have no
/// documented limit, so none is made up; VRChat's own refusal is shown if it has one.
/// </para>
/// <para>
/// Checking first is not a second opinion on VRChat. It saves a request that could only be refused,
/// on a budget that is deliberately small, and says what is wrong in the words of the field
/// rather than in VRChat's.
/// </para>
/// </remarks>
public static class GroupPageRules
{
    public const int NameMin = 3;
    public const int NameMax = 64;
    public const int DescriptionMax = 250;
    public const int MaxLanguages = 3;
    public const int LanguageCodeMax = 3;
    public const int MaxLinks = 3;

    /// <summary>The most posts one page asks VRChat for.</summary>
    public const int PostPageSize = 20;

    /// <summary>Who can join, in VRChat's words, in the order its settings list them.</summary>
    public static readonly IReadOnlyList<string> JoinStates = ["open", "request", "invite", "closed"];

    /// <summary>Who can see a post, in VRChat's words.</summary>
    public static readonly IReadOnlyList<string> PostVisibilities = ["group", "public"];

    /// <summary>
    /// The edit with its text trimmed, its lists cleaned and its links written the way Modbot
    /// stores them; or the reason it cannot be sent.
    /// </summary>
    public static (GroupProfileEdit? Edit, string? Problem) Tidy(GroupProfileEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);

        var name = edit.Name?.Trim();
        if (name is not null && (name.Length < NameMin || name.Length > NameMax))
            return (null, $"The name must be {NameMin} to {NameMax} characters.");

        // Kept as typed apart from the ends: line breaks inside are part of what VRChat shows.
        var description = edit.Description?.Trim();
        if (description is not null && description.Length > DescriptionMax)
            return (null, $"The description can be at most {DescriptionMax} characters.");

        var rules = edit.Rules?.Trim();

        List<string>? languages = null;
        if (edit.Languages is not null)
        {
            languages = edit.Languages
                .Select(l => (l ?? string.Empty).Trim().ToLowerInvariant())
                .Where(l => l.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (languages.Count > MaxLanguages)
                return (null, $"A group can list at most {MaxLanguages} languages.");

            if (languages.Any(l => l.Length > LanguageCodeMax))
                return (null, $"A language is a code of at most {LanguageCodeMax} letters, such as eng.");
        }

        List<string>? links = null;
        if (edit.Links is not null)
        {
            links = [];

            foreach (var raw in edit.Links)
            {
                var text = (raw ?? string.Empty).Trim();
                if (text.Length == 0)
                    continue;

                if (WebLink(text) is not { } link)
                    return (null, $"\"{text}\" is not a web address. Links start with https://.");

                if (!links.Contains(link, StringComparer.Ordinal))
                    links.Add(link);
            }

            if (links.Count > MaxLinks)
                return (null, $"A group can have at most {MaxLinks} links.");
        }

        var joinState = edit.JoinState?.Trim().ToLowerInvariant();
        if (joinState is not null && !JoinStates.Contains(joinState))
            return (null, "Who can join must be open, request, invite or closed.");

        return (new GroupProfileEdit(name, description, rules, languages, links, joinState), null);
    }

    /// <summary>
    /// Each field the tidied edit actually changes, with what it was and what it becomes. A field
    /// sent unchanged is not a change, and is neither sent to VRChat nor written down.
    /// </summary>
    public static IReadOnlyList<(string Field, JsonNode? Old, JsonNode? New)> Changes(
        GroupProfileState before, GroupProfileEdit tidy)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(tidy);

        var changes = new List<(string, JsonNode?, JsonNode?)>();

        void Text(string field, string? was, string? now)
        {
            if (now is not null && !string.Equals(was ?? string.Empty, now, StringComparison.Ordinal))
                changes.Add((field, was is null ? null : JsonValue.Create(was), JsonValue.Create(now)));
        }

        void List(string field, IReadOnlyList<string> was, IReadOnlyList<string>? now)
        {
            if (now is not null && !was.SequenceEqual(now, StringComparer.Ordinal))
                changes.Add((field, new JsonArray([.. was.Select(v => (JsonNode?)JsonValue.Create(v))]),
                    new JsonArray([.. now.Select(v => (JsonNode?)JsonValue.Create(v))])));
        }

        Text("name", before.Name, tidy.Name);
        Text("description", before.Description, tidy.Description);
        Text("rules", before.Rules, tidy.Rules);
        List("languages", before.Languages, tidy.Languages);
        List("links", before.Links.Select(l => WebLink(l) ?? l).ToList(), tidy.Links);
        Text("joinState", before.JoinState, tidy.JoinState);

        return changes;
    }

    /// <summary>
    /// The SDK's value for one of VRChat's join words. Only called on a word <see cref="Tidy"/>
    /// has already accepted.
    /// </summary>
    public static GroupJoinState JoinStateOf(string word) => word switch
    {
        "open" => GroupJoinState.Open,
        "request" => GroupJoinState.Request,
        "invite" => GroupJoinState.Invite,
        "closed" => GroupJoinState.Closed,
        _ => throw new ArgumentOutOfRangeException(nameof(word), word, "Not one of VRChat's join words."),
    };

    /// <summary>A post's audience in VRChat's word.</summary>
    public static string VisibilityWord(GroupPostVisibility? visibility)
        => visibility == GroupPostVisibility.Public ? "public" : "group";

    /// <summary>
    /// A post with its text trimmed and its roles cleaned; or the reason it cannot be sent. VRChat
    /// requires a title and text of at least one character each and documents no upper limit.
    /// </summary>
    public static (GroupPostBody? Post, string? Problem) TidyPost(GroupPostBody body)
    {
        ArgumentNullException.ThrowIfNull(body);

        var title = body.Title?.Trim() ?? string.Empty;
        if (title.Length == 0)
            return (null, "A post needs a title.");

        var text = body.Text?.Trim() ?? string.Empty;
        if (text.Length == 0)
            return (null, "A post needs some text.");

        var visibility = (body.Visibility ?? "group").Trim().ToLowerInvariant();
        if (!PostVisibilities.Contains(visibility))
            return (null, "A post is for the group's members or for everyone.");

        // Role ids are opaque: blanks and repeats go, nothing else is judged (spec 3.1.1).
        var roles = (body.RoleIds ?? [])
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var id = string.IsNullOrWhiteSpace(body.Id) ? null : body.Id;
        var image = string.IsNullOrWhiteSpace(body.ImageId) ? null : body.ImageId;

        return (new GroupPostBody(id, title, text, visibility, roles, body.Notify, image), null);
    }

    /// <summary>A link written the way Modbot stores one, or null when it is not a web address.</summary>
    /// <remarks>The same check the group-info sync makes before a link reaches an <c>href</c>.</remarks>
    public static string? WebLink(string? text)
        => Uri.TryCreate(text?.Trim(), UriKind.Absolute, out var parsed)
           && (parsed.Scheme == Uri.UriSchemeHttps || parsed.Scheme == Uri.UriSchemeHttp)
            ? parsed.AbsoluteUri
            : null;
}
