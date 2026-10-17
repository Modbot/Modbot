using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Posts;

namespace Modbot.Core.Calendar;

/// <summary>
/// The pictures Bluesky's posts use besides the cover and the post's own one: an event's list, a
/// template's list, and the list a Bluesky post was sent with. The picture sweep and the drop of a
/// picture an event let go of both ask here, so neither deletes a picture one of them still needs.
/// </summary>
public static class CalendarBlueskyPicturesInUse
{
    /// <summary>Every picture id a live event, a template or a Bluesky post lists, without repeats.</summary>
    public static async Task<HashSet<Guid>> ReadAsync(ModbotContext db, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        var ids = new HashSet<Guid>();

        // The lists sit on the event, read whole: a few ids at most.
        var events = await db.CalendarEvents
            .Where(e => e.DeletedAt == null && e.BlueskyPictureIds != null)
            .Select(e => e.BlueskyPictureIds!)
            .ToListAsync(ct).ConfigureAwait(false);

        foreach (var list in events)
            ids.UnionWith(list);

        // A template keeps its list inside its fields (JSON); there are few templates.
        var templates = await db.CalendarTemplates.Select(t => t.Fields).ToListAsync(ct).ConfigureAwait(false);

        foreach (var fields in templates)
            ids.UnionWith(TemplatePictures(fields));

        // A post keeps the list it was sent with in its destination's options (JSON). Only Bluesky
        // rows that have a list are read, so the hourly sweep does not read every destination.
        var options = await db.PostDestinations
            .Where(d => d.Network == PostNetworks.Bluesky && EF.Functions.JsonContains(d.Options, "{\"pictures\":[]}"))
            .Select(d => d.Options)
            .ToListAsync(ct).ConfigureAwait(false);

        foreach (var json in options)
        {
            if (PostTexts.ParseBlueskyOptions(json).Pictures is { } pictures)
                ids.UnionWith(pictures.Select(p => p.PictureId));
        }

        return ids;
    }

    /// <summary>The picture ids in a template's fields JSON, or none when it has no list or cannot be read.</summary>
    internal static IEnumerable<Guid> TemplatePictures(string fieldsJson)
    {
        try
        {
            using var document = JsonDocument.Parse(fieldsJson);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return [];

            var found = new List<Guid>();

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!string.Equals(property.Name, "blueskyPictures", StringComparison.OrdinalIgnoreCase)
                    || property.Value.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var picture in property.Value.EnumerateArray())
                {
                    if (picture.ValueKind != JsonValueKind.Object)
                        continue;

                    foreach (var inner in picture.EnumerateObject())
                    {
                        if (string.Equals(inner.Name, "pictureId", StringComparison.OrdinalIgnoreCase)
                            && inner.Value.ValueKind == JsonValueKind.String
                            && inner.Value.TryGetGuid(out var id))
                            found.Add(id);
                    }
                }
            }

            return found;
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
