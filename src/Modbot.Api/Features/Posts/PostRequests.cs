using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Features.Calendar;
using Modbot.Core.Calendar;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Posts;
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
        CheckedDiscord? Discord);

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

        if (body.Draft)
        {
            if (title is null && text.Length == 0 && string.IsNullOrEmpty(ownText))
                problems.Add("Write something first.");
        }
        else if (text.Length == 0 && (body.Discord is null || string.IsNullOrEmpty(ownText)))
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

        if (!body.Draft && body.Discord is null)
            problems.Add("Pick where it goes.");

        CheckedDiscord? discord = null;
        if (body.Discord is { } section)
            discord = await CheckDiscordAsync(db, section, title, text, ownText, body.Draft, problems, ct);

        return (new Checked(title, text, body.PictureId, sendAt, zone?.Id ?? "UTC", body.EventId, discord), problems);
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
            channel = await db.DiscordChannels.AsNoTracking()
                .Where(c => c.ChannelId == channelId && c.RemovedAt == null)
                .OrderByDescending(c => c.GuildId == guildId)
                .FirstOrDefaultAsync(ct);

            if (section.Publish && channel is not null && channel.Type != DiscordChannelTypes.Announcement)
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

        return problems;
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
                .Select(d => (JsonNode)new JsonObject
                {
                    ["network"] = d.Network,
                    ["target"] = d.Target,
                    ["options"] = JsonNode.Parse(string.IsNullOrWhiteSpace(d.Options) ? "{}" : d.Options),
                    ["text"] = d.TextOverride,
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
            CalendarReadiness.Discord(settings, bot));
    }

    public static PostSitesView SitesView(PostSites sites) => new(sites.Paused, sites.DiscordOn, sites.DiscordSetUp);

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

        return [.. posts.Select(p => View(p, sites, channels, roles, users, events))];
    }

    private static PostView View(
        Post post,
        PostSites sites,
        IReadOnlyDictionary<string, string> channels,
        IReadOnlyDictionary<string, string> roles,
        IReadOnlyDictionary<Guid, string> users,
        IReadOnlyDictionary<Guid, string> events)
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
                    var options = PostTexts.DiscordOptionsOf(d);
                    var notPublished = d.State == PostDestinationStates.Posted && options.Publish && d.PublishedAt is null;

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
                        d.PublishedAt);
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
