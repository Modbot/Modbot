using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Features.Users;
using Modbot.Core.Data.Entities;

namespace Modbot.Api.Features.Settings;

/// <summary>
/// What one settings save changed, ready to be written as one "settings changed" entry.
/// </summary>
/// <remarks>
/// <para>
/// Every settings save records who changed what (spec 5.9.3). Each screen used to write its own
/// entry by hand and about half never did, so an operator could shorten how long history is kept
/// and nothing in the audit log said so. One place builds the entry now, so a screen adds its
/// fields and cannot get the shape wrong.
/// </para>
/// <para>
/// A field is recorded as <c>{ old, new }</c> under <c>changed</c>, the diff every other producer
/// writes and the audit log already reads. A field that did not change is left out, and a save
/// that changed nothing writes nothing.
/// </para>
/// <para>
/// A secret (a key, a client secret) is recorded as <c>{ secret: true }</c>: the entry says it was
/// changed and never carries the value, before or after. The setting name is the screen's; the
/// field names are what the screen calls each control.
/// </para>
/// </remarks>
public sealed class SettingsChange
{
    private readonly string _setting;
    private readonly JsonObject _changed = new();

    /// <param name="setting">The screen, in camel case: <c>retention</c>, <c>aiLimits</c>.</param>
    public SettingsChange(string setting)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(setting);
        _setting = setting;
    }

    /// <summary>Nothing changed. Nothing is written.</summary>
    public bool IsEmpty => _changed.Count == 0;

    /// <summary>A single value: recorded when <paramref name="before"/> and <paramref name="after"/> differ.</summary>
    public SettingsChange Field<T>(string field, T before, T after)
    {
        if (EqualityComparer<T>.Default.Equals(before, after))
            return this;

        _changed[field] = new JsonObject { ["old"] = Node(before), ["new"] = Node(after) };
        return this;
    }

    /// <summary>A list of values: recorded when the two lists are not the same in the same order.</summary>
    public SettingsChange Items<T>(string field, IEnumerable<T> before, IEnumerable<T> after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var was = before.ToList();
        var now = after.ToList();

        if (was.SequenceEqual(now))
            return this;

        _changed[field] = new JsonObject { ["old"] = ArrayOf(was), ["new"] = ArrayOf(now) };
        return this;
    }

    /// <summary>
    /// A setting stored as one JSON document, said one value at a time: every value in it that is
    /// not the same before and after, named by its path (<c>auditLogMinIntervalSeconds</c>).
    /// </summary>
    public SettingsChange Document(string? beforeJson, string? afterJson)
    {
        Walk(string.Empty, Parse(beforeJson), Parse(afterJson));
        return this;
    }

    private void Walk(string prefix, JsonObject? was, JsonObject? now)
    {
        var keys = (was?.Select(p => p.Key) ?? []).Concat(now?.Select(p => p.Key) ?? []).Distinct();

        foreach (var key in keys)
        {
            var a = was?[key];
            var b = now?[key];
            var name = prefix.Length == 0 ? key : $"{prefix}{char.ToUpperInvariant(key[0])}{key[1..]}";

            if ((a is null or JsonObject) && (b is null or JsonObject) && (a is not null || b is not null))
            {
                Walk(name, a as JsonObject, b as JsonObject);
                continue;
            }

            if (!JsonNode.DeepEquals(a, b))
                _changed[name] = new JsonObject { ["old"] = a?.DeepClone(), ["new"] = b?.DeepClone() };
        }
    }

    private static JsonObject? Parse(string? json)
        => string.IsNullOrWhiteSpace(json) ? null : JsonNode.Parse(json) as JsonObject;

    /// <summary>
    /// A secret that changed. Only its name is recorded, so the entry cannot leak it: not the new
    /// value, not the old one, not a hash of either.
    /// </summary>
    public SettingsChange Secret(string field, bool changed)
    {
        if (changed)
            _changed[field] = new JsonObject { ["secret"] = true };

        return this;
    }

    /// <summary>
    /// Writes the entry, in the caller's transaction so the save and its record commit together or
    /// not at all. Writes nothing when nothing changed.
    /// </summary>
    public Task RecordAsync(HttpContext http, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);

        if (IsEmpty)
            return Task.CompletedTask;

        return http.RequestServices.GetRequiredService<AccountFacts>().RecordAsync(
            FactType.SettingsChanged,
            "settings",
            Actor.Of(http),
            new JsonObject
            {
                ["setting"] = _setting,
                ["changed"] = _changed.DeepClone(),
            },
            ct);
    }

    private static JsonNode? Node<T>(T value) => JsonSerializer.SerializeToNode(value);

    private static JsonArray ArrayOf<T>(IEnumerable<T> values)
        => new([.. values.Select(v => Node(v))]);
}
