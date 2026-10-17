using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Posts;

namespace Modbot.Core.Calendar;

/// <summary>
/// The kept videos Bluesky's posts still need: a live event's, a template's, and the one a post that is
/// not yet on Bluesky goes with. The sweep and the drop of a video an event let go of both ask here, so
/// neither deletes a video one of them still needs. A post that is up no longer needs its file.
/// </summary>
public static class CalendarBlueskyVideosInUse
{
    /// <summary>Every kept video id a live event, a template or a Bluesky post still to go points at, without repeats.</summary>
    public static async Task<HashSet<Guid>> ReadAsync(ModbotContext db, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        var ids = new HashSet<Guid>();

        ids.UnionWith(await db.CalendarEvents
            .Where(e => e.DeletedAt == null && e.BlueskyVideoId != null)
            .Select(e => e.BlueskyVideoId!.Value)
            .ToListAsync(ct).ConfigureAwait(false));

        // A template keeps its video inside its fields (JSON); there are few templates.
        var templates = await db.CalendarTemplates.Select(t => t.Fields).ToListAsync(ct).ConfigureAwait(false);

        foreach (var fields in templates)
        {
            if (TemplateVideo(fields) is { } id)
                ids.Add(id);
        }

        // A post keeps the video it goes with in its destination's options (JSON). Only Bluesky rows that
        // have one, and are not yet posted, are read.
        var options = await db.PostDestinations
            .Where(d => d.Network == PostNetworks.Bluesky
                && d.State != PostDestinationStates.Posted
                && EF.Functions.JsonContains(d.Options, "{\"video\":{}}"))
            .Select(d => d.Options)
            .ToListAsync(ct).ConfigureAwait(false);

        foreach (var json in options)
        {
            if (PostTexts.ParseBlueskyOptions(json).Video?.VideoId is { } id)
                ids.Add(id);
        }

        return ids;
    }

    /// <summary>The kept video id in a template's fields JSON, or null when it has none or cannot be read.</summary>
    internal static Guid? TemplateVideo(string fieldsJson)
    {
        try
        {
            using var document = JsonDocument.Parse(fieldsJson);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!string.Equals(property.Name, "blueskyVideo", StringComparison.OrdinalIgnoreCase)
                    || property.Value.ValueKind != JsonValueKind.Object)
                    continue;

                foreach (var inner in property.Value.EnumerateObject())
                {
                    if (string.Equals(inner.Name, "videoId", StringComparison.OrdinalIgnoreCase)
                        && inner.Value.ValueKind == JsonValueKind.String
                        && inner.Value.TryGetGuid(out var id))
                        return id;
                }
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
