using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Modbot.Core.Data.Entities;

namespace Modbot.Api.Features.Imports;

/// <summary>One item of an upload: a record, or the reason the line could not be read as one.</summary>
/// <param name="Line">The line in a newline-delimited file; the 1-based position in a JSON array.</param>
public readonly record struct ImportItem(int Line, JsonElement? Record, string? Problem);

/// <summary>A record read and checked, ready to become a fact (import design §2).</summary>
/// <param name="Source">
/// Which of Modbot's sources the fact is filed under: the record's own <c>seenBy</c>, or the
/// upload's when it said nothing (import design §5).
/// </param>
public sealed record ParsedRecord(
    int Line,
    string Kind,
    string Type,
    string? TypeRaw,
    DateTimeOffset At,
    FactPlatform SubjectPlatform,
    string SubjectId,
    FactPlatform? ActorPlatform,
    string? ActorId,
    string? ActorName,
    string? ExternalId,
    FactSource Source,
    JsonObject Data,
    string Key);

/// <summary>
/// Reads an upload as a JSON array or as newline-delimited JSON, and checks each record.
/// </summary>
/// <remarks>
/// <para>
/// The two shapes are told apart by the first non-blank character: <c>[</c> is an array; anything
/// else is one record per line. An array that will not parse fails the whole upload, because
/// there is no telling where one record ends and the next begins; a bad line in a
/// newline-delimited file is one rejection, and the file goes on.
/// </para>
/// <para>
/// Ids are never validated for shape (foundation §3.1.1). What is checked is that the fields are
/// there and are the right JSON type.
/// </para>
/// </remarks>
public static class ImportFile
{
    public const int MaxKindLength = 128;
    public const int MaxExternalIdLength = 200;
    public const int MaxNameLength = 256;

    private static readonly JsonDocumentOptions Lenient = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// Every item of the upload, in order.
    /// </summary>
    /// <exception cref="JsonException">The upload is a JSON array that cannot be read.</exception>
    /// <exception cref="InvalidOperationException">The upload is a JSON document that is not an array.</exception>
    public static IEnumerable<ImportItem> Read(byte[] body)
    {
        ArgumentNullException.ThrowIfNull(body);

        var start = 0;
        if (body.Length >= 3 && body[0] == 0xEF && body[1] == 0xBB && body[2] == 0xBF)
            start = 3;

        while (start < body.Length && IsBlank(body[start]))
            start++;

        if (start < body.Length && body[start] == (byte)'[')
            return ReadArray(body.AsMemory(start));

        return ReadLines(body, start);
    }

    private static IEnumerable<ImportItem> ReadArray(ReadOnlyMemory<byte> body)
    {
        using var document = JsonDocument.Parse(body, Lenient);

        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("The file is a JSON document but not an array of records.");

        var position = 0;
        foreach (var element in document.RootElement.EnumerateArray())
        {
            position++;
            yield return new ImportItem(position, element.Clone(), null);
        }
    }

    private static IEnumerable<ImportItem> ReadLines(byte[] body, int start)
    {
        var line = 0;
        var at = 0;

        while (at < body.Length)
        {
            var end = Array.IndexOf(body, (byte)'\n', at);
            if (end < 0)
                end = body.Length;

            line++;
            var from = Math.Max(at, start);
            var length = end - from;
            at = end + 1;

            if (length <= 0)
                continue;

            var slice = body.AsMemory(from, length);
            if (slice.Span[^1] == (byte)'\r')
                slice = slice[..^1];

            if (IsBlank(slice.Span))
                continue;

            yield return ParseLine(line, slice);
        }
    }

    private static ImportItem ParseLine(int line, ReadOnlyMemory<byte> slice)
    {
        try
        {
            using var document = JsonDocument.Parse(slice, Lenient);
            return new ImportItem(line, document.RootElement.Clone(), null);
        }
        catch (JsonException e)
        {
            return new ImportItem(line, null, $"Not valid JSON: {e.Message}");
        }
    }

    private static bool IsBlank(byte b) => b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';

    private static bool IsBlank(ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes)
        {
            if (!IsBlank(b))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Checks one record and works out its fact type and its idempotency key.
    /// </summary>
    /// <param name="now">Anything dated after this is refused: it cannot have happened yet.</param>
    /// <param name="defaultSource">
    /// What the record is filed under when it does not say (import design §5): the upload's own
    /// choice, or <see cref="ImportSources.Default"/>.
    /// </param>
    public static bool TryParse(
        ImportItem item,
        DateTimeOffset now,
        FactSource defaultSource,
        out ParsedRecord? record,
        out string? reason)
    {
        record = null;
        reason = null;

        if (item.Problem is not null)
        {
            reason = item.Problem;
            return false;
        }

        if (item.Record is not { ValueKind: JsonValueKind.Object } element)
        {
            reason = "Not an object.";
            return false;
        }

        if (!TryString(element, "kind", out var kind) || string.IsNullOrWhiteSpace(kind))
        {
            reason = "kind is missing.";
            return false;
        }

        kind = kind.Trim();
        if (kind.Length > MaxKindLength)
        {
            reason = $"kind is longer than {MaxKindLength} characters.";
            return false;
        }

        if (!TryString(element, "at", out var atText) || string.IsNullOrWhiteSpace(atText))
        {
            reason = "at is missing.";
            return false;
        }

        if (!DateTimeOffset.TryParse(
                atText,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var at))
        {
            reason = "at is not a date and time.";
            return false;
        }

        if (at > now)
        {
            reason = "at is in the future.";
            return false;
        }

        if (!TryPerson(element, "subject", out var subjectPlatform, out var subjectId, out _, out var subjectProblem))
        {
            reason = subjectProblem ?? "subject is missing.";
            return false;
        }

        if (subjectPlatform is null || subjectId is null)
        {
            reason = "subject is missing.";
            return false;
        }

        if (!TryPerson(element, "actor", out var actorPlatform, out var actorId, out var actorName, out var actorProblem))
        {
            reason = actorProblem;
            return false;
        }

        string? externalId = null;
        if (element.TryGetProperty("externalId", out var externalElement) && externalElement.ValueKind != JsonValueKind.Null)
        {
            if (externalElement.ValueKind is JsonValueKind.String)
                externalId = externalElement.GetString();
            else if (externalElement.ValueKind is JsonValueKind.Number)
                externalId = externalElement.GetRawText();
            else
            {
                reason = "externalId is not text.";
                return false;
            }

            externalId = externalId?.Trim();
            if (string.IsNullOrEmpty(externalId))
                externalId = null;
            else if (externalId.Length > MaxExternalIdLength)
            {
                reason = $"externalId is longer than {MaxExternalIdLength} characters.";
                return false;
            }
        }

        var source = defaultSource;
        if (element.TryGetProperty("seenBy", out var seenByElement) && seenByElement.ValueKind != JsonValueKind.Null)
        {
            if (seenByElement.ValueKind != JsonValueKind.String)
            {
                reason = "seenBy is not text.";
                return false;
            }

            // An empty seenBy is the record saying nothing, so the upload's choice still stands.
            var seenBy = seenByElement.GetString();
            if (!string.IsNullOrWhiteSpace(seenBy)
                && !ImportSources.TryParse(seenBy, out source, out var sourceProblem))
            {
                reason = sourceProblem;
                return false;
            }
        }

        JsonObject data;
        if (element.TryGetProperty("data", out var dataElement) && dataElement.ValueKind != JsonValueKind.Null)
        {
            if (dataElement.ValueKind != JsonValueKind.Object)
            {
                reason = "data is not an object.";
                return false;
            }

            data = JsonNode.Parse(dataElement.GetRawText()) as JsonObject ?? new JsonObject();
        }
        else
        {
            data = new JsonObject();
        }

        var (type, typeRaw) = ImportKinds.Resolve(kind, subjectPlatform.Value);

        var key = externalId is not null
            ? "id:" + externalId
            : "hash:" + Hash(kind, at, subjectPlatform.Value, subjectId, actorPlatform, actorId, data);

        record = new ParsedRecord(
            item.Line, kind, type, typeRaw, at,
            subjectPlatform.Value, subjectId, actorPlatform, actorId, actorName, externalId, source, data, key);
        return true;
    }

    private static bool TryString(JsonElement element, string name, out string? value)
    {
        value = null;
        if (!element.TryGetProperty(name, out var property))
            return false;

        if (property.ValueKind != JsonValueKind.String)
            return false;

        value = property.GetString();
        return true;
    }

    /// <summary>
    /// Reads a <c>{ platform, id, name }</c> object. Absent is fine (the outputs are null); present
    /// and wrong is a problem.
    /// </summary>
    private static bool TryPerson(
        JsonElement element,
        string name,
        out FactPlatform? platform,
        out string? id,
        out string? displayName,
        out string? problem)
    {
        platform = null;
        id = null;
        displayName = null;
        problem = null;

        if (!element.TryGetProperty(name, out var person) || person.ValueKind == JsonValueKind.Null)
            return true;

        if (person.ValueKind != JsonValueKind.Object)
        {
            problem = $"{name} is not an object.";
            return false;
        }

        if (!TryString(person, "platform", out var platformText) || string.IsNullOrWhiteSpace(platformText))
        {
            problem = $"{name}.platform is missing.";
            return false;
        }

        platform = platformText.Trim().ToLowerInvariant() switch
        {
            "vrchat" => FactPlatform.VRChat,
            "discord" => FactPlatform.Discord,
            _ => null,
        };

        if (platform is null)
        {
            problem = $"{name}.platform is not vrchat or discord.";
            return false;
        }

        if (person.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.Number)
            id = idElement.GetRawText();
        else if (!TryString(person, "id", out id) || string.IsNullOrWhiteSpace(id))
        {
            problem = $"{name}.id is missing.";
            return false;
        }

        id = id.Trim();

        if (TryString(person, "name", out var nameText) && !string.IsNullOrWhiteSpace(nameText))
        {
            displayName = nameText.Trim();
            if (displayName.Length > MaxNameLength)
                displayName = displayName[..MaxNameLength];
        }

        return true;
    }

    /// <summary>
    /// SHA-256 over the record's canonical form (import design §6): the fields that make it the
    /// event it is, with <c>data</c>'s keys sorted at every level so two spellings of the same
    /// object hash the same.
    /// </summary>
    /// <remarks>
    /// <c>seenBy</c> is deliberately not in it. It says how Modbot files the record, not what
    /// happened, so re-uploading a file with the mapping corrected must not import every record
    /// a second time under the new source.
    /// </remarks>
    private static string Hash(
        string kind,
        DateTimeOffset at,
        FactPlatform subjectPlatform,
        string subjectId,
        FactPlatform? actorPlatform,
        string? actorId,
        JsonObject data)
    {
        var canonical = new StringBuilder()
            .Append(kind.ToLowerInvariant()).Append('\n')
            .Append(at.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)).Append('\n')
            .Append(subjectPlatform.ToString()).Append('\n')
            .Append(subjectId).Append('\n')
            .Append(actorPlatform?.ToString() ?? string.Empty).Append('\n')
            .Append(actorId ?? string.Empty).Append('\n')
            .Append(Canonical(data)!.ToJsonString())
            .ToString();

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    /// <summary>The same JSON with object keys in ordinal order at every level.</summary>
    public static JsonNode? Canonical(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                var sorted = new JsonObject();
                foreach (var pair in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                    sorted[pair.Key] = Canonical(pair.Value);
                return sorted;

            case JsonArray array:
                var items = new JsonArray();
                foreach (var item in array)
                    items.Add(Canonical(item));
                return items;

            case null:
                return null;

            default:
                return node.DeepClone();
        }
    }
}
