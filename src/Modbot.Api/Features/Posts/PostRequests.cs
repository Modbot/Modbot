using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Features.Calendar;
using Modbot.Core.Bluesky;
using Modbot.Core.Calendar;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Posts;
using Modbot.VRChat.Sync;
using NodaTime;
using NodaTime.Text;

namespace Modbot.Api.Features.Posts;

/// <summary>
/// Checking what the composer sends, copying it onto a post, and drawing posts for the page
/// (posts design §4.3, §4.7).
/// </summary>
/// <remarks>
/// Every problem is found at once and answered together, the way the calendar does (calendar design
/// §17.2), so the composer can show them all. The preview checks with the same code, so what it
/// draws is what Schedule would save.
/// </remarks>
internal static class PostRequests
{
    /// <summary>A time picked a moment ago is still "now" by the time it is saved.</summary>
    public static readonly TimeSpan PastGrace = TimeSpan.FromMinutes(1);

    public const string ChannelNotInServer = "That channel is not in the Discord server.";

    /// <summary>Bluesky ticked with no account found yet, while the Posting switch is off.</summary>
    public const string BlueskyOff = "Bluesky is off.";

    /// <summary>Bluesky ticked with no account found yet, while the Posting switch is on.</summary>
    public const string BlueskyNotSetUp = "Bluesky is not set up.";

    public const string WhenNow = "now";
    public const string WhenLater = "later";

    private static readonly LocalDateTimePattern LocalPattern = LocalDateTimePattern.CreateWithInvariantCulture("uuuu'-'MM'-'dd'T'HH':'mm");
    private static readonly LocalDateTimePattern LocalPatternSeconds = LocalDateTimePattern.CreateWithInvariantCulture("uuuu'-'MM'-'dd'T'HH':'mm':'ss");

    /// <summary>What the request asks for, checked and tidied.</summary>
    public sealed record Checked(
        string? Title,
        string Text,
        Guid? PictureId,
        DateTimeOffset? SendAt,
        string TimeZone,
        Guid? EventId,
        CheckedDiscord? Discord,
        CheckedVRChat? VRChat = null,
        CheckedBluesky? Bluesky = null);

    /// <summary>The Bluesky section, checked.</summary>
    /// <param name="Did">The account it goes to: the one in Settings when it was saved.</param>
    /// <param name="OwnText">Bluesky's own text, or null for the post's title and text.</param>
    /// <param name="Text">The whole text Bluesky gets.</param>
    /// <param name="CardPictureId">The small copy of the picture for the card, kept only when it is of the post's picture.</param>
    /// <param name="Card">The link card, when the text has a link.</param>
    public sealed record CheckedBluesky(
        string? Did,
        string? OwnText,
        string Text,
        Guid? CardPictureId,
        Guid? CardPictureFrom,
        BlueskyCard? Card);

    /// <summary>The VRChat section, checked.</summary>
    /// <param name="GroupId">The group it goes to: the managed group when it was saved.</param>
    /// <param name="Title">The title VRChat gets: its own or the post's. Null when there is none.</param>
    /// <param name="Text">The text VRChat gets: its own or the post's.</param>
    /// <param name="RoleNames">The roles it is for, by name, for the preview.</param>
    /// <param name="PictureUrl">Modbot's address for the picture VRChat is sent, or null for text only.</param>
    public sealed record CheckedVRChat(
        string? GroupId,
        VRChatPostOptions Options,
        string? OwnTitle,
        string? OwnText,
        string? Title,
        string Text,
        IReadOnlyList<string> RoleNames,
        string? PictureUrl);

    /// <summary>The Discord section, checked.</summary>
    public sealed record CheckedDiscord(
        string? ChannelId,
        string? ChannelName,
        string? ChannelType,
        DiscordPostOptions Options,
        string? RoleName,
        int RoleColour,
        string? OwnText,
        string Content);

    /// <summary>Checks a request. Every problem, one sentence each, in the composer's order.</summary>
    /// <param name="keptPicture">The picture already on the post, which may be gone from nowhere.</param>
    public static async Task<(Checked Request, List<string> Problems)> CheckAsync(
        ModbotContext db, PostRequest body, DateTimeOffset now, Guid? keptPicture, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);

        var problems = new List<string>();
        var title = PostTexts.TidyTitle(body.Title);
        var text = PostTexts.Tidy(body.Text);

        if (title is { Length: > Post.MaxTitleLength })
            problems.Add($"The title is longer than {Post.MaxTitleLength} characters.");

        if (text.Length > Post.MaxTextLength)
            problems.Add($"The text is longer than {Post.MaxTextLength} characters.");

        var ownText = body.Discord?.Text is { } own ? PostTexts.Tidy(own) : null;
        var vrchatOwnTitle = body.VRChat?.Title is { } ownTitle ? PostTexts.TidyTitle(ownTitle) : null;
        var vrchatOwnText = body.VRChat?.Text is { } ownVRChat ? PostTexts.Tidy(ownVRChat) : null;
        var blueskyOwnText = body.Bluesky?.Text is { } ownBluesky ? PostTexts.Tidy(ownBluesky) : null;

        // The text each ticked site gets: its own, or the post's.
        var siteTexts = new List<string>(3);
        if (body.Discord is not null)
            siteTexts.Add(ownText ?? text);
        if (body.VRChat is not null)
            siteTexts.Add(vrchatOwnText ?? text);
        if (body.Bluesky is not null)
            siteTexts.Add(blueskyOwnText ?? text);

        if (body.Draft)
        {
            if (title is null && vrchatOwnTitle is null && text.Length == 0 && siteTexts.All(t => t.Length == 0))
                problems.Add("Write something first.");
        }
        else if (siteTexts.Count == 0 ? text.Length == 0 : siteTexts.Any(t => t.Length == 0))
        {
            problems.Add("Write some text.");
        }

        var zone = CalendarRepeat.FindZone(string.IsNullOrWhiteSpace(body.TimeZone) ? "UTC" : body.TimeZone);
        if (zone is null)
            problems.Add("That time zone is not known.");

        DateTimeOffset? sendAt = null;
        var when = body.When?.Trim().ToLowerInvariant();

        if (when == WhenNow && !body.Draft)
        {
            sendAt = now;
        }
        else if (when == WhenLater)
        {
            var local = ParseLocal(body.SendAt);

            if (local is null)
            {
                if (!body.Draft)
                    problems.Add("Pick a date and time.");
            }
            else if (zone is not null)
            {
                sendAt = zone.AtLeniently(local.Value).ToInstant().ToDateTimeOffset();

                if (!body.Draft && sendAt < now - PastGrace)
                    problems.Add("That time has passed.");
            }
        }
        else if (!body.Draft)
        {
            problems.Add("Pick when it goes.");
        }

        if (body.PictureId is { } pictureId && pictureId != keptPicture
            && !await db.CalendarCoverPictures.AnyAsync(c => c.Id == pictureId, ct))
        {
            problems.Add("That picture is gone. Choose it again.");
        }

        string? eventTitle = null;
        if (body.EventId is { } eventId)
        {
            eventTitle = await db.CalendarEvents.AsNoTracking()
                .Where(e => e.Id == eventId && e.DeletedAt == null)
                .Select(e => e.Title)
                .FirstOrDefaultAsync(ct);

            if (eventTitle is null)
                problems.Add("That event does not exist.");
        }

        if (!body.Draft && body.Discord is null && body.VRChat is null && body.Bluesky is null)
            problems.Add("Pick where it goes.");

        CheckedDiscord? discord = null;
        if (body.Discord is { } section)
            discord = await CheckDiscordAsync(db, section, title, text, ownText, body.Draft, problems, ct);

        CheckedVRChat? vrchat = null;
        if (body.VRChat is { } vrchatSection)
        {
            var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);
            vrchat = CheckVRChat(vrchatSection, settings, title, text, vrchatOwnTitle, vrchatOwnText, body.PictureId, body.Draft, problems);
        }

        CheckedBluesky? bluesky = null;
        if (body.Bluesky is { } blueskySection)
            bluesky = await CheckBlueskyAsync(db, blueskySection, title, text, blueskyOwnText, body.PictureId, body.Draft, problems, ct);

        return (new Checked(title, text, body.PictureId, sendAt, zone?.Id ?? "UTC", body.EventId, discord, vrchat, bluesky), problems);
    }

    /// <summary>
    /// The Bluesky section (posts design §4.2c): its own text, the 300-character and 3000-byte limits
    /// counted the way Bluesky counts, and the card picture. The account is the one in Settings when
    /// the post is saved. The card picture is kept only when it is a small enough copy of the post's
    /// own picture; Bluesky gets it only when the text has a link for the card.
    /// </summary>
    private static async Task<CheckedBluesky> CheckBlueskyAsync(
        ModbotContext db,
        PostBlueskyRequest section,
        string? title,
        string text,
        string? ownText,
        Guid? pictureId,
        bool draft,
        List<string> problems,
        CancellationToken ct)
    {
        var account = await db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => new { s.BlueskyDid, s.BlueskyPostingOn })
            .FirstOrDefaultAsync(ct);

        var did = account?.BlueskyDid?.Trim();

        if (string.IsNullOrEmpty(did))
        {
            did = null;

            // The same state, in the same order, as the composer's chip beside Bluesky: Off while
            // the Posting switch is off, otherwise Not set up.
            if (!draft)
                problems.Add(account?.BlueskyPostingOn == true ? BlueskyNotSetUp : BlueskyOff);
        }

        if (ownText is { Length: > Post.MaxTextLength })
            problems.Add($"The Bluesky text is longer than {Post.MaxTextLength} characters.");

        var sent = ownText ?? PostTexts.Bluesky(title, text);

        if (!BlueskyText.Fits(sent))
            problems.Add(BlueskyText.TooLong);

        Guid? cardPicture = null;

        if (section.CardPictureId is { } copy && pictureId is { } picture && section.CardPictureFrom == picture)
        {
            var size = await db.CalendarCoverPictures.AsNoTracking()
                .Where(c => c.Id == copy)
                .Select(c => (int?)c.Bytes.Length)
                .FirstOrDefaultAsync(ct);

            if (size is > BlueskyText.CardPictureMaxBytes)
                problems.Add("The Bluesky picture is larger than 1 MB.");
            else if (size is not null)
                cardPicture = copy;
        }

        return new CheckedBluesky(
            did,
            ownText,
            sent,
            cardPicture,
            cardPicture is null ? null : pictureId,
            BlueskyPostRecord.CardFor(title, sent));
    }

    /// <summary>
    /// The VRChat section (posts design §3.6): who sees it, the roles, notify, its own title and
    /// text, and the picture's VRChat id. VRChat needs a title; it documents no length limit, so none
    /// is made up beyond what Modbot keeps. Role ids are opaque: blanks and repeats go, nothing else
    /// is judged (foundation §3.1.1).
    /// </summary>
    private static CheckedVRChat CheckVRChat(
        PostVRChatRequest section,
        Core.Data.Entities.Settings? settings,
        string? title,
        string text,
        string? ownTitle,
        string? ownText,
        Guid? pictureId,
        bool draft,
        List<string> problems)
    {
        var groupId = settings?.ManagedGroupId?.Trim();
        if (string.IsNullOrEmpty(groupId))
        {
            groupId = null;
            if (!draft)
                problems.Add("No VRChat group is set up yet.");
        }

        var visibility = string.IsNullOrWhiteSpace(section.Visibility)
            ? VRChatPostVisibilities.Group
            : section.Visibility.Trim().ToLowerInvariant();

        if (!VRChatPostVisibilities.IsKnown(visibility))
        {
            problems.Add("Who sees it must be Group or Everyone.");
            visibility = VRChatPostVisibilities.Group;
        }

        if (ownTitle is { Length: > Post.MaxTitleLength })
            problems.Add($"The VRChat title is longer than {Post.MaxTitleLength} characters.");

        if (ownText is { Length: > Post.MaxTextLength })
            problems.Add($"The VRChat text is longer than {Post.MaxTextLength} characters.");

        var sentTitle = ownTitle ?? title;
        if (sentTitle is null && !draft)
            problems.Add("VRChat needs a title.");

        // The picture goes to VRChat only while uploads are on (decision 14), and only as uploaded
        // from the picture the post has; otherwise VRChat gets text only.
        var imageId = string.IsNullOrWhiteSpace(section.ImageId) ? null : section.ImageId.Trim();
        if (settings is not { VRChatPictureUploads: true } || pictureId is null)
            imageId = null;

        var options = new VRChatPostOptions(
            visibility,
            [.. section.RoleIds ?? []],
            section.Notify,
            imageId,
            imageId is null ? null : pictureId);

        options = options with { RoleIds = PostTexts.VRChatRoles(options) };

        var roles = VRChatRoleNames(settings);

        return new CheckedVRChat(
            groupId,
            options,
            ownTitle,
            ownText,
            sentTitle,
            ownText ?? text,
            [.. options.RoleIds!.Select(id => roles.TryGetValue(id, out var name) ? name : id)],
            imageId is null || pictureId is not { } picture ? null : PostEndpoints.PicturePath + picture);
    }

    /// <summary>The VRChat group's roles, in VRChat's order, as the last group read found them.</summary>
    public static IReadOnlyList<PostRoleChoice> VRChatRoles(Core.Data.Entities.Settings? settings) =>
        [.. (GroupInfoSnapshot.Parse(settings?.GroupInfoSnapshot)?.Roles ?? [])
            .OrderBy(r => r.Order)
            .Select(r => new PostRoleChoice(r.Id, string.IsNullOrWhiteSpace(r.Name) ? r.Id : r.Name))];

    /// <summary>The VRChat group's role names by id, as the last group read found them.</summary>
    private static Dictionary<string, string> VRChatRoleNames(Core.Data.Entities.Settings? settings)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var role in VRChatRoles(settings))
            names.TryAdd(role.Id, role.Name);

        return names;
    }

    private static async Task<CheckedDiscord> CheckDiscordAsync(
        ModbotContext db,
        PostDiscordRequest section,
        string? title,
        string text,
        string? ownText,
        bool draft,
        List<string> problems,
        CancellationToken ct)
    {
        var guildId = (await db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => s.DiscordGuildId)
            .FirstOrDefaultAsync(ct))?.Trim();

        var channelId = section.ChannelId?.Trim();
        DiscordChannel? channel = null;

        if (string.IsNullOrEmpty(channelId))
        {
            channelId = null;
            if (!draft)
                problems.Add("Pick a channel.");
        }
        else
        {
            // Only a channel of the server in settings, as the bot last listed it: never one of another
            // server the bot happens to be in, whatever id is sent.
            channel = string.IsNullOrEmpty(guildId)
                ? null
                : await db.DiscordChannels.AsNoTracking()
                    .FirstOrDefaultAsync(c => c.ChannelId == channelId && c.GuildId == guildId && c.RemovedAt == null, ct);

            if (channel is null)
                problems.Add(ChannelNotInServer);
            else if (section.Publish && channel.Type != DiscordChannelTypes.Announcement)
                problems.Add("Only an Announcement channel can publish to followers.");
        }

        var roleId = section.RoleId?.Trim();
        string? roleName = null;
        var roleColour = 0;

        if (string.IsNullOrEmpty(roleId))
        {
            roleId = null;
        }
        else if (string.Equals(roleId, guildId, StringComparison.Ordinal))
        {
            problems.Add("The post cannot mention @everyone.");
        }
        else
        {
            var role = string.IsNullOrEmpty(guildId)
                ? null
                : await db.DiscordRoles.AsNoTracking().FirstOrDefaultAsync(r => r.RoleId == roleId && r.GuildId == guildId, ct);

            if (role is { Everyone: true })
            {
                problems.Add("The post cannot mention @everyone.");
            }
            else if (role is null || role.RemovedAt is not null)
            {
                problems.Add("That role is not in the Discord server.");
            }
            else
            {
                roleName = role.Name;
                roleColour = role.Color;

                var server = await db.DiscordServers.AsNoTracking().FirstOrDefaultAsync(s => s.GuildId == guildId, ct);
                if (!DiscordRole.BotCanMention(role, server))
                    problems.Add("The bot may not mention that role.");
            }
        }

        var options = new DiscordPostOptions(roleId, section.Publish);
        var content = PostTexts.Discord(title, ownText ?? text, roleId);

        if (!PostTexts.DiscordFits(content))
            problems.Add($"The Discord text is longer than {PostTexts.DiscordLimit} characters.");

        return new CheckedDiscord(channelId, channel?.Name, channel?.Type, options, roleName, roleColour, ownText, content);
    }

    /// <summary>
    /// Copies a checked request onto a post, and its sites onto its destinations: a site ticked gets
    /// a row waiting to go, a site unticked loses the row it had. Only for a post that may be changed
    /// whole (<see cref="PostRules.CannotEdit"/>), so no row here has been sent.
    /// </summary>
    public static void Apply(Post post, Checked request, bool draft, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(post);
        ArgumentNullException.ThrowIfNull(request);

        post.Title = request.Title;
        post.Text = request.Text;
        post.PictureId = request.PictureId;
        post.Status = draft ? PostStatuses.Draft : PostStatuses.Scheduled;
        post.SendAt = request.SendAt;
        post.TimeZone = request.TimeZone;
        post.EventId = request.EventId;
        post.CancelledAt = null;
        post.UpdatedAt = now;

        var discord = post.Destinations.FirstOrDefault(d => d.Network == PostNetworks.Discord);

        if (request.Discord is { } wanted)
        {
            if (discord is null)
            {
                discord = new PostDestination
                {
                    Id = Guid.CreateVersion7(),
                    PostId = post.Id,
                    Network = PostNetworks.Discord,
                };
                post.Destinations.Add(discord);
            }

            discord.Target = wanted.ChannelId ?? string.Empty;
            discord.Options = PostTexts.WriteDiscordOptions(wanted.Options);
            discord.TextOverride = wanted.OwnText;
            discord.TitleOverride = null;
            discord.State = PostDestinationStates.Waiting;
            discord.Error = null;
            discord.ErrorAt = null;
            discord.MissingPermission = null;
            discord.CheckAt = null;
            discord.MayBeSent = false;
            discord.SendIfMissing = false;
            discord.UpdatedAt = now;
        }
        else if (discord is not null)
        {
            post.Destinations.Remove(discord);
        }

        var vrchat = post.Destinations.FirstOrDefault(d => d.Network == PostNetworks.VRChat);

        if (request.VRChat is { } wantedVRChat)
        {
            if (vrchat is null)
            {
                vrchat = new PostDestination
                {
                    Id = Guid.CreateVersion7(),
                    PostId = post.Id,
                    Network = PostNetworks.VRChat,
                };
                post.Destinations.Add(vrchat);
            }

            vrchat.Target = wantedVRChat.GroupId ?? string.Empty;
            vrchat.Options = PostTexts.WriteVRChatOptions(wantedVRChat.Options);
            vrchat.TitleOverride = wantedVRChat.OwnTitle;
            vrchat.TextOverride = wantedVRChat.OwnText;
            vrchat.State = PostDestinationStates.Waiting;
            vrchat.Error = null;
            vrchat.ErrorAt = null;
            vrchat.MissingPermission = null;
            vrchat.CheckAt = null;
            vrchat.MayBeSent = false;
            vrchat.SendIfMissing = false;
            vrchat.UpdatedAt = now;
        }
        else if (vrchat is not null)
        {
            post.Destinations.Remove(vrchat);
        }

        var bluesky = post.Destinations.FirstOrDefault(d => d.Network == PostNetworks.Bluesky);

        if (request.Bluesky is { } wantedBluesky)
        {
            if (bluesky is null)
            {
                bluesky = new PostDestination
                {
                    Id = Guid.CreateVersion7(),
                    PostId = post.Id,
                    Network = PostNetworks.Bluesky,
                };
                post.Destinations.Add(bluesky);
            }

            // The record key (ClientKey) is kept: a row only changes whole when no try of it may be
            // on Bluesky, and sending again under the same key can never make a second post.
            bluesky.Target = wantedBluesky.Did ?? string.Empty;
            bluesky.Options = PostTexts.WriteBlueskyOptions(new BlueskyPostOptions(wantedBluesky.CardPictureFrom));
            bluesky.SitePictureId = wantedBluesky.CardPictureId;
            bluesky.TitleOverride = null;
            bluesky.TextOverride = wantedBluesky.OwnText;
            bluesky.State = PostDestinationStates.Waiting;
            bluesky.Error = null;
            bluesky.ErrorAt = null;
            bluesky.MissingPermission = null;
            bluesky.CheckAt = null;
            bluesky.MayBeSent = false;
            bluesky.SendIfMissing = false;
            bluesky.UpdatedAt = now;
        }
        else if (bluesky is not null)
        {
            post.Destinations.Remove(bluesky);
        }
    }

    /// <summary>What still stops a draft being sent now, one sentence each.</summary>
    public static List<string> NotReadyToSend(Post post)
    {
        ArgumentNullException.ThrowIfNull(post);

        var problems = new List<string>();
        var live = post.Destinations.Where(d => d.State is PostDestinationStates.Waiting or PostDestinationStates.Failed).ToList();

        if (live.Count == 0)
            problems.Add("Pick where it goes.");

        foreach (var destination in live.Where(d => d.Network == PostNetworks.Discord))
        {
            var content = PostTexts.Discord(post, destination);

            if (string.IsNullOrWhiteSpace(destination.Target))
                problems.Add("Pick a channel.");

            if (PostTexts.TextFor(post, destination).Length == 0)
                problems.Add("Write some text.");

            if (!PostTexts.DiscordFits(content))
                problems.Add($"The Discord text is longer than {PostTexts.DiscordLimit} characters.");
        }

        foreach (var destination in live.Where(d => d.Network == PostNetworks.VRChat))
        {
            if (string.IsNullOrWhiteSpace(destination.Target))
                problems.Add("No VRChat group is set up yet.");

            if (PostTexts.TitleFor(post, destination) is null)
                problems.Add("VRChat needs a title.");

            if (PostTexts.TextFor(post, destination).Length == 0)
                problems.Add("Write some text.");
        }

        foreach (var destination in live.Where(d => d.Network == PostNetworks.Bluesky))
        {
            var sent = PostTexts.Bluesky(post, destination);

            if (string.IsNullOrWhiteSpace(destination.Target))
                problems.Add("No Bluesky account is set up yet.");

            if (sent.Length == 0)
                problems.Add("Write some text.");

            if (!BlueskyText.Fits(sent))
                problems.Add(BlueskyText.TooLong);
        }

        return [.. problems.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>A post's fields as the audit log keeps them.</summary>
    public static JsonObject Describe(Post post)
    {
        ArgumentNullException.ThrowIfNull(post);

        return new JsonObject
        {
            ["title"] = post.Title,
            ["text"] = post.Text,
            ["pictureId"] = post.PictureId?.ToString(),
            ["status"] = post.Status,
            ["sendAt"] = post.SendAt?.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ["timeZone"] = post.TimeZone,
            ["eventId"] = post.EventId?.ToString(),
            ["destinations"] = new JsonArray([.. post.Destinations
                .OrderBy(d => d.Network, StringComparer.Ordinal)
                .Select(d =>
                {
                    var row = new JsonObject
                    {
                        ["network"] = d.Network,
                        ["target"] = d.Target,
                        ["options"] = JsonNode.Parse(string.IsNullOrWhiteSpace(d.Options) ? "{}" : d.Options),
                        ["text"] = d.TextOverride,
                    };

                    // VRChat has its own title too; Discord does not.
                    if (d.Network == PostNetworks.VRChat)
                        row["title"] = d.TitleOverride;

                    // Bluesky's card picture, the small copy of the post's.
                    if (d.Network == PostNetworks.Bluesky)
                        row["cardPictureId"] = d.SitePictureId?.ToString();

                    return (JsonNode)row;
                })]),
        };
    }

    /// <summary>The fields that differ between two descriptions, each as <c>{ old, new }</c>.</summary>
    public static JsonObject Changed(JsonObject before, JsonObject after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var changed = new JsonObject();

        foreach (var key in before.Select(p => p.Key).Union(after.Select(p => p.Key)))
        {
            var was = before[key];
            var now = after[key];

            if (!JsonNode.DeepEquals(was, now))
                changed[key] = new JsonObject { ["old"] = was?.DeepClone(), ["new"] = now?.DeepClone() };
        }

        return changed;
    }

    // ── Drawing posts ─────────────────────────────────────────────────────────────────────

    public static async Task<PostSites> SitesAsync(ModbotContext db, IDiscordBotStatus? bot, CancellationToken ct)
    {
        var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);

        return new PostSites(
            settings?.PostsPaused ?? false,
            settings?.DiscordPostsOn ?? true,
            CalendarReadiness.Discord(settings, bot),
            settings?.VRChatPostsOn ?? true,
            PostSites.VRChatReady(settings),
            settings?.BlueskyPostingOn ?? false,
            PostSites.BlueskyReady(settings));
    }

    public static PostSitesView SitesView(PostSites sites, bool vrchatPictures) =>
        new(sites.Paused, sites.DiscordOn, sites.DiscordSetUp, sites.VRChatOn, sites.VRChatSetUp, vrchatPictures, sites.BlueskyOn, sites.BlueskySetUp);

    /// <summary>The posts as the page draws them, with the names Modbot has for their channels, roles, events and writers.</summary>
    public static async Task<List<PostView>> ViewsAsync(
        ModbotContext db, IReadOnlyList<Post> posts, PostSites sites, CancellationToken ct)
    {
        var destinations = posts.SelectMany(p => p.Destinations).ToList();
        var channelIds = destinations.Where(d => d.Network == PostNetworks.Discord && d.Target.Length > 0)
            .Select(d => d.Target).Distinct().ToList();
        var roleIds = destinations.Where(d => d.Network == PostNetworks.Discord)
            .Select(d => PostTexts.DiscordOptionsOf(d).RoleId)
            .OfType<string>().Distinct().ToList();
        var userIds = posts.Select(p => p.CreatedByUserId).OfType<Guid>().Distinct().ToList();
        var eventIds = posts.Select(p => p.EventId).OfType<Guid>().Distinct().ToList();

        var channels = (await db.DiscordChannels.AsNoTracking()
                .Where(c => channelIds.Contains(c.ChannelId))
                .Select(c => new { c.ChannelId, c.Name })
                .ToListAsync(ct))
            .GroupBy(c => c.ChannelId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.Ordinal);

        var roles = (await db.DiscordRoles.AsNoTracking()
                .Where(r => roleIds.Contains(r.RoleId))
                .Select(r => new { r.RoleId, r.Name })
                .ToListAsync(ct))
            .GroupBy(r => r.RoleId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.Ordinal);

        var users = await db.Users.AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.Username, ct);

        var events = await db.CalendarEvents.AsNoTracking()
            .Where(e => eventIds.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, e => e.Title, ct);

        var vrchatRoles = destinations.Any(d => d.Network == PostNetworks.VRChat)
            ? VRChatRoleNames(await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct))
            : new Dictionary<string, string>(StringComparer.Ordinal);

        return [.. posts.Select(p => View(p, sites, channels, roles, users, events, vrchatRoles))];
    }

    private static PostView View(
        Post post,
        PostSites sites,
        IReadOnlyDictionary<string, string> channels,
        IReadOnlyDictionary<string, string> roles,
        IReadOnlyDictionary<Guid, string> users,
        IReadOnlyDictionary<Guid, string> events,
        IReadOnlyDictionary<string, string> vrchatRoles)
    {
        var zone = CalendarRepeat.FindZone(post.TimeZone) ?? DateTimeZone.Utc;

        return new PostView(
            post.Id,
            post.Title,
            post.Text,
            post.PictureId,
            post.Status,
            PostRules.ListsOf(post),
            post.SendAt,
            post.SendAt is { } at ? LocalPattern.Format(Instant.FromDateTimeOffset(at).InZone(zone).LocalDateTime) : null,
            post.TimeZone,
            post.EventId,
            post.EventId is { } eventId && events.TryGetValue(eventId, out var eventTitle) ? eventTitle : null,
            post.Kind,
            post.Version,
            post.CreatedByUserId is { } userId && users.TryGetValue(userId, out var username) ? username : null,
            post.CreatedAt,
            post.UpdatedAt,
            post.CancelledAt,
            [.. post.Destinations
                .OrderBy(d => PostNetworks.All.TakeWhile(n => n != d.Network).Count())
                .Select(d =>
                {
                    var discord = d.Network == PostNetworks.Discord;
                    var options = discord ? PostTexts.DiscordOptionsOf(d) : new DiscordPostOptions();
                    var notPublished = d.State == PostDestinationStates.Posted && options.Publish && d.PublishedAt is null;
                    VRChatDestinationView? vrchat = null;

                    if (d.Network == PostNetworks.VRChat)
                    {
                        var own = PostTexts.VRChatOptionsOf(d);
                        var ids = PostTexts.VRChatRoles(own);
                        vrchat = new VRChatDestinationView(
                            own.Visibility,
                            ids,
                            [.. ids.Select(id => vrchatRoles.TryGetValue(id, out var name) ? name : id)],
                            own.Notify,
                            own.ImageId,
                            own.PictureId);
                    }

                    BlueskyDestinationView? bluesky = null;

                    if (d.Network == PostNetworks.Bluesky)
                        bluesky = new BlueskyDestinationView(d.SitePictureId, d.SitePictureId is null ? null : PostTexts.BlueskyOptionsOf(d).PictureId);

                    return new PostDestinationView(
                        d.Id,
                        d.Network,
                        d.Target,
                        channels.TryGetValue(d.Target, out var channel) ? channel : null,
                        options.RoleId,
                        options.RoleId is { } role && roles.TryGetValue(role, out var roleName) ? roleName : null,
                        options.Publish,
                        d.State,
                        PostRules.Shown(d, sites),
                        d.Link,
                        d.ExternalId,
                        d.State is PostDestinationStates.Failed || notPublished ? d.Error : null,
                        d.ErrorAt,
                        notPublished,
                        d.MayBeSent,
                        d.TitleOverride,
                        d.TextOverride,
                        d.SentText,
                        d.SentAt,
                        d.PostedAt,
                        d.PublishedAt,
                        vrchat,
                        d.State is PostDestinationStates.Failed ? d.MissingPermission : null,
                        bluesky);
                })]);
    }

    private static LocalDateTime? ParseLocal(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var trimmed = text.Trim();
        var result = LocalPattern.Parse(trimmed);
        if (!result.Success)
            result = LocalPatternSeconds.Parse(trimmed);

        return result.Success ? result.Value : null;
    }
}
