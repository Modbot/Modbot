using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;

namespace Modbot.Core.Profiles;

/// <summary>Who is looking at a member's profile (member profiles design §3).</summary>
public enum ProfileViewer
{
    /// <summary>The member themselves: every field, answered or not.</summary>
    Owner = 0,

    /// <summary>Modbot staff: every answered field, whatever level the member chose.</summary>
    Staff = 1,

    /// <summary>Anyone else in the server: the answered fields the member opened to members.</summary>
    Member = 2,
}

/// <summary>One line of a profile: a field, the answer in words, and who may read it.</summary>
/// <param name="Answer">The option's label or the member's words; null when the field is not answered.</param>
/// <param name="Visibility">The level in force: the member's own choice, else the field's default.</param>
public sealed record ProfileLine(Guid FieldId, string Name, ProfileFieldKind Kind, string? Answer, ProfileVisibility Visibility);

/// <summary>
/// Reading and writing member profiles (member profiles design). The Discord commands and the web
/// page both go through here, so the rule for who sees what is written once.
/// </summary>
public static class MemberProfiles
{
    /// <summary>The fields in the order members see them, each with its options in order.</summary>
    public static async Task<List<ProfileField>> FieldsAsync(ModbotContext db, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        var fields = await db.ProfileFields.AsNoTracking()
            .Include(f => f.Options)
            .OrderBy(f => f.SortOrder)
            .ThenBy(f => f.CreatedAt)
            .ToListAsync(ct).ConfigureAwait(false);

        foreach (var field in fields)
            field.Options = [.. field.Options.OrderBy(o => o.SortOrder).ThenBy(o => o.Label, StringComparer.Ordinal)];

        return fields;
    }

    /// <summary>One member's stored rows, by field.</summary>
    public static async Task<Dictionary<Guid, ProfileValue>> ValuesAsync(ModbotContext db, string discordUserId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrWhiteSpace(discordUserId);

        var rows = await db.ProfileValues.AsNoTracking()
            .Where(v => v.DiscordUserId == discordUserId)
            .ToListAsync(ct).ConfigureAwait(false);

        return rows.ToDictionary(v => v.FieldId);
    }

    /// <summary>The level in force for a field: the member's own choice, else the field's default.</summary>
    public static ProfileVisibility EffectiveVisibility(ProfileField field, ProfileValue? value)
    {
        ArgumentNullException.ThrowIfNull(field);
        return value?.Visibility ?? field.DefaultVisibility;
    }

    /// <summary>The answer in words: an option's label or the member's text; null when there is none.</summary>
    public static string? AnswerOf(ProfileField field, ProfileValue? value)
    {
        ArgumentNullException.ThrowIfNull(field);

        if (value is null)
            return null;

        if (field.Kind == ProfileFieldKind.Text)
            return string.IsNullOrWhiteSpace(value.Text) ? null : value.Text;

        return value.OptionId is { } id ? field.Options.FirstOrDefault(o => o.Id == id)?.Label : null;
    }

    /// <summary>
    /// What <paramref name="viewer"/> may read of one member's profile, in the fields' order. Staff
    /// see every answer, including the ones the member kept to themselves.
    /// </summary>
    public static async Task<IReadOnlyList<ProfileLine>> LinesAsync(
        ModbotContext db, string discordUserId, ProfileViewer viewer, CancellationToken ct)
    {
        var fields = await FieldsAsync(db, ct).ConfigureAwait(false);
        var values = await ValuesAsync(db, discordUserId, ct).ConfigureAwait(false);
        var lines = new List<ProfileLine>();

        foreach (var field in fields)
        {
            values.TryGetValue(field.Id, out var value);
            var answer = AnswerOf(field, value);
            var level = EffectiveVisibility(field, value);

            var shown = viewer switch
            {
                ProfileViewer.Owner => true,
                ProfileViewer.Staff => answer is not null,
                _ => answer is not null && level == ProfileVisibility.Members,
            };

            if (shown)
                lines.Add(new ProfileLine(field.Id, field.Name, field.Kind, answer, level));
        }

        return lines;
    }

    /// <summary>
    /// Saves a member's answers. The key is a field's id; the value is the option's id for a
    /// pick-list or the words for a text field, and null or empty clears the answer. A field that is
    /// gone, an option the field does not have and words past the limit are left out; nothing else
    /// about the member's profile changes.
    /// </summary>
    public static async Task SetAnswersAsync(
        ModbotContext db,
        string discordUserId,
        IReadOnlyDictionary<Guid, string?> answers,
        DateTimeOffset now,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrWhiteSpace(discordUserId);
        ArgumentNullException.ThrowIfNull(answers);

        var fields = (await FieldsAsync(db, ct).ConfigureAwait(false)).ToDictionary(f => f.Id);
        var rows = await db.ProfileValues.Where(v => v.DiscordUserId == discordUserId).ToDictionaryAsync(v => v.FieldId, ct).ConfigureAwait(false);

        foreach (var (fieldId, raw) in answers)
        {
            if (!fields.TryGetValue(fieldId, out var field))
                continue;

            var text = raw?.Trim();
            Guid? option = null;

            if (field.Kind == ProfileFieldKind.PickList)
            {
                if (!string.IsNullOrEmpty(text))
                {
                    if (!Guid.TryParse(text, out var parsed) || field.Options.All(o => o.Id != parsed))
                        continue;

                    option = parsed;
                }

                text = null;
            }
            else
            {
                if (text is { Length: > ProfileField.MaxTextLength })
                    continue;

                if (string.IsNullOrEmpty(text))
                    text = null;
            }

            rows.TryGetValue(fieldId, out var row);

            if (row is null)
            {
                if (option is null && text is null)
                    continue;

                db.ProfileValues.Add(new ProfileValue
                {
                    FieldId = fieldId,
                    DiscordUserId = discordUserId,
                    OptionId = option,
                    Text = text,
                    UpdatedAt = now,
                });
                continue;
            }

            if (row.OptionId == option && row.Text == text)
                continue;

            row.OptionId = option;
            row.Text = text;
            row.UpdatedAt = now;
            DropIfEmpty(db, row);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Saves the levels a member picked. A level that is the one already in force is not stored, so
    /// opening the form and sending it leaves every field following the default it followed, and a
    /// later change of the default still reaches that member.
    /// </summary>
    public static async Task SetVisibilityAsync(
        ModbotContext db,
        string discordUserId,
        IReadOnlyDictionary<Guid, ProfileVisibility> levels,
        DateTimeOffset now,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrWhiteSpace(discordUserId);
        ArgumentNullException.ThrowIfNull(levels);

        var fields = (await FieldsAsync(db, ct).ConfigureAwait(false)).ToDictionary(f => f.Id);
        var rows = await db.ProfileValues.Where(v => v.DiscordUserId == discordUserId).ToDictionaryAsync(v => v.FieldId, ct).ConfigureAwait(false);

        foreach (var (fieldId, level) in levels)
        {
            if (!fields.TryGetValue(fieldId, out var field) || !Enum.IsDefined(level))
                continue;

            rows.TryGetValue(fieldId, out var row);

            if (EffectiveVisibility(field, row) == level)
                continue;

            if (row is null)
            {
                db.ProfileValues.Add(new ProfileValue
                {
                    FieldId = fieldId,
                    DiscordUserId = discordUserId,
                    Visibility = level,
                    UpdatedAt = now,
                });
                continue;
            }

            // Back to the default is stored as "follow the default", not as the same level twice.
            row.Visibility = level == field.DefaultVisibility ? null : level;
            row.UpdatedAt = now;
            DropIfEmpty(db, row);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Whether the member let <c>/find</c> list them. Off for a member who never touched it.</summary>
    public static async Task<bool> IsFindableAsync(ModbotContext db, string discordUserId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrWhiteSpace(discordUserId);

        return await db.MemberProfiles.AsNoTracking()
            .AnyAsync(m => m.DiscordUserId == discordUserId && m.Findable, ct).ConfigureAwait(false);
    }

    /// <summary>Turns the member's Findable switch on or off. Off stores nothing: the row goes.</summary>
    public static async Task SetFindableAsync(
        ModbotContext db, string discordUserId, bool findable, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrWhiteSpace(discordUserId);

        var row = await db.MemberProfiles.FirstOrDefaultAsync(m => m.DiscordUserId == discordUserId, ct).ConfigureAwait(false);

        if (row is null && findable)
            db.MemberProfiles.Add(new MemberProfile { DiscordUserId = discordUserId, Findable = true, UpdatedAt = now });
        else if (row is not null && !findable)
            db.MemberProfiles.Remove(row);
        else if (row is not null && !row.Findable)
        {
            row.Findable = true;
            row.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Fills a pick-list field from the Discord roles its options are linked to (member profiles
    /// design §8). Every current member who holds a linked role and has no answer to the field gets
    /// the option of that role; one who holds roles of two options gets the first option in the
    /// field's order. An answer a member set is never touched, and a copied one follows the field's
    /// default level, so a level the member already chose stays. Returns how many answers it filled,
    /// or null when the field is gone or is not a pick-list.
    /// </summary>
    public static async Task<int?> CopyFromRolesAsync(ModbotContext db, Guid fieldId, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        var field = (await FieldsAsync(db, ct).ConfigureAwait(false)).FirstOrDefault(f => f.Id == fieldId);

        if (field is null || field.Kind != ProfileFieldKind.PickList)
            return null;

        // The first option in the order wins, so a role is looked up in that order too.
        var linked = field.Options.Where(o => !string.IsNullOrEmpty(o.DiscordRoleId)).ToList();

        if (linked.Count == 0)
            return 0;

        var roleIds = linked.Select(o => o.DiscordRoleId!).ToHashSet(StringComparer.Ordinal);

        var members = await db.DiscordMembers.AsNoTracking()
            .Where(m => m.LeftAt == null && !m.IsBot)
            .Select(m => new { m.UserId, m.Roles })
            .ToListAsync(ct).ConfigureAwait(false);

        var rows = await db.ProfileValues.Where(v => v.FieldId == fieldId).ToDictionaryAsync(v => v.DiscordUserId, ct).ConfigureAwait(false);
        var done = new HashSet<string>(StringComparer.Ordinal);
        var filled = 0;

        foreach (var member in members)
        {
            if (!done.Add(member.UserId))
                continue;

            rows.TryGetValue(member.UserId, out var row);

            if (row?.OptionId is not null)
                continue;

            var held = RolesOf(member.Roles);

            if (!held.Overlaps(roleIds))
                continue;

            var option = linked.First(o => held.Contains(o.DiscordRoleId!));

            if (row is null)
            {
                db.ProfileValues.Add(new ProfileValue
                {
                    FieldId = fieldId,
                    DiscordUserId = member.UserId,
                    OptionId = option.Id,
                    UpdatedAt = now,
                });
            }
            else
            {
                // The member only chose who sees it so far: keep that, add the answer.
                row.OptionId = option.Id;
                row.UpdatedAt = now;
            }

            filled++;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return filled;
    }

    private static HashSet<string> RolesOf(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json)?.ToHashSet(StringComparer.Ordinal) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static void DropIfEmpty(ModbotContext db, ProfileValue row)
    {
        if (row.OptionId is null && string.IsNullOrEmpty(row.Text) && row.Visibility is null)
            db.ProfileValues.Remove(row);
    }
}
