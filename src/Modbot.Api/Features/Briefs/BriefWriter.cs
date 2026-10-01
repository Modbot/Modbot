using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Modbot.AI;
using Modbot.AI.Briefs;
using Modbot.AI.Calls;
using Modbot.AI.Usage;
using Modbot.Analytics.Facts;
using Modbot.Api.Features.Audit;
using Modbot.Api.Features.Chat;
using Modbot.Api.Features.Users;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;
using OpenAI.Chat;

namespace Modbot.Api.Features.Briefs;

/// <summary>Why a brief was not written, with the status to answer with.</summary>
public sealed class BriefRefused(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>The person asking for a brief.</summary>
internal sealed record BriefAsker(Guid UserId, string Username, ModbotPermissions Held);

/// <summary>What one brief is about, and the entries the asker may read about it.</summary>
/// <param name="About">One sentence naming the instance or the person, for the model.</param>
/// <param name="Entries">Newest first, as the audit log reads them.</param>
/// <param name="Newest">True when there were more entries than these.</param>
/// <param name="People">Who reading these entries read about, for the lookup fact (AI chat design §13).</param>
/// <param name="Details">What the lookup fact says the brief was: <c>brief</c>, and the instance's id for one.</param>
/// <param name="SamePlace">The world and number every entry happened in, for an instance, so it is not repeated on each.</param>
internal sealed record BriefSubject(
    string About,
    IReadOnlyList<AuditEntry> Entries,
    bool Newest,
    IReadOnlyList<LookedUpPerson> People,
    JsonObject Details,
    (string WorldId, string Number)? SamePlace = null);

/// <summary>
/// Writes one brief: the entries go to the Chat model in one call, with no tools, and the answer
/// comes back with the ids it cites checked against the entries it was given (AI chat design §14).
/// </summary>
/// <remarks>
/// <para>
/// <strong>A Chat call in every way that matters to the bill.</strong> It is recorded and limited
/// under <c>chat</c>, so Chat's spend limit, the person and role limits and the member's monthly
/// allowance all stop it, and it runs on Chat's model with Chat's time limit. It goes through
/// <see cref="AiCallRunner"/>, so the fallback model, the call log and the spend ledger are the same
/// as every other feature's. Somebody pressed a button for it, so the call log keeps what was sent
/// and what came back (§12.4).
/// </para>
/// <para>
/// <strong>Only what the asker could already read.</strong> The entries are the ones the popup's
/// Activity tab shows them, read the same way and narrowed by <see cref="AuditVisibility"/> with
/// their own permissions, so a brief never says anything its reader could not open themselves.
/// </para>
/// <para>
/// <strong>Nothing recorded is not a call.</strong> With no entries the model is not asked and
/// nothing is spent; the answer says so.
/// </para>
/// </remarks>
internal sealed class BriefWriter(
    ModbotContext db,
    IAiClients ai,
    AiSpendLimits limits,
    AiCallRunner runner,
    IAiCallLog calls,
    IModbotClock clock,
    IFactWriter facts,
    EventPartitionMaintainer partitions)
{
    public const string Off = "AI briefs are off.";
    public const string NotSetUp = "AI is not set up.";
    public const string NoText = "The model answered with no text.";

    /// <summary>Refused before anything is read when briefs are off, so a switched-off deployment reads nothing for one.</summary>
    public async Task<Core.Data.Entities.Settings> SettingsAsync(CancellationToken ct)
    {
        var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);

        if (!ChatSwitch.BriefsOn(settings))
            throw new BriefRefused(StatusCodes.Status409Conflict, Off);

        return settings;
    }

    public async Task<BriefView> WriteAsync(
        Core.Data.Entities.Settings settings,
        BriefSubject subject,
        BriefAsker asker,
        string? timeZone,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(asker);

        if (subject.Entries.Count == 0)
            return new BriefView(null, [], 0, false, null, null, null);

        var chat = await ai.GetChatAsync(ct)
                   ?? throw new BriefRefused(StatusCodes.Status409Conflict, NotSetUp);

        var model = string.IsNullOrWhiteSpace(settings.AiChatModel) ? chat.Model : settings.AiChatModel.Trim();

        // Checked before anything is sent, like a Chat turn (AI chat design §10): the limit for
        // everyone, Chat's own, the ones on this person or their roles, and their allowance.
        if (await limits.CheckAsync(AiFeatures.Chat, asker.UserId, asker.Held, ct) is { } reached)
        {
            await calls.RecordAsync(
                new AiCallEntry(
                    AiFeatures.Chat, model, null, chat.Provider, AiCallOutcomes.Limited, 0,
                    Error: reached.Message, UserId: asker.UserId, Username: asker.Username),
                ct);

            throw new BriefRefused(StatusCodes.Status429TooManyRequests, reached.Message);
        }

        var zone = AI.Insights.InsightTimes.ZoneOrUtc(timeZone);
        var records = subject.Entries.Select(e => Record(e, subject.SamePlace)).ToList();
        var question = BriefPrompt.Records(subject.About, records, subject.Newest, zone);

        var plan = new AiCallPlan(
            AiFeatures.Chat, chat, model,
            Prompt: $"{BriefPrompt.Instructions}\n\n{question}",
            UserId: asker.UserId,
            Username: asker.Username,
            KeepText: true,
            ChatTimeLimitSeconds: settings.AiChatTimeLimitSeconds);

        AiCallResult<string> result;
        try
        {
            result = await runner.RunAsync(plan, async (client, token) =>
            {
                var options = new ChatCompletionOptions { MaxOutputTokenCount = BriefPrompt.MaxOutputTokens };
                AiReportedCost.AskFor(options, chat.Provider);

                ChatCompletion completion = await client.CompleteChatAsync(
                    [AiPromptCache.Instructions(BriefPrompt.Instructions, chat.Provider), new UserChatMessage(question)],
                    options,
                    token);

                var answer = string.Concat(completion.Content
                    .Where(p => p.Kind == ChatMessageContentPartKind.Text)
                    .Select(p => p.Text)).Trim();

                return new AiCallAnswer<string>(answer, completion.Model, completion.Usage, answer);
            }, ct);
        }
        finally
        {
            // Who read about whom, once for the brief and whatever came of the call: the entries
            // were read and sent either way (AI chat design §13).
            await ChatLookupFacts.RecordAsync(
                facts, partitions, clock.UtcNow, new Actor(asker.UserId, asker.Username),
                subject.Details, subject.People, [], CancellationToken.None);
        }

        if (!result.Answered)
            throw new BriefRefused(StatusCodes.Status502BadGateway, result.Error ?? "The AI provider could not answer.");

        var text = result.Value ?? string.Empty;
        if (text.Length == 0)
            throw new BriefRefused(StatusCodes.Status502BadGateway, NoText);

        if (text.Length > BriefPrompt.MaxTextLength)
            text = string.Concat(text.AsSpan(0, BriefPrompt.MaxTextLength - 1), "…");

        var given = subject.Entries.Select(e => e.Id).ToHashSet();
        var sources = BriefPrompt.Cited(text).Where(given.Contains).ToList();

        var times = subject.Entries.Select(e => e.OccurredAt).ToList();
        var builtFrom = BriefPrompt.BuiltFrom(subject.Entries.Count, subject.Newest, times.Min(), times.Max(), zone);

        return new BriefView(text, sources, subject.Entries.Count, subject.Newest, builtFrom, result.CallId, result.Model);
    }

    /// <summary>One entry as the model is given it: what the Activity tab shows of it, in words.</summary>
    internal static BriefRecord Record(AuditEntry e, (string WorldId, string Number)? samePlace = null)
    {
        ArgumentNullException.ThrowIfNull(e);

        var here = samePlace is { } place && e.WorldId == place.WorldId && e.InstanceId == place.Number;

        return new BriefRecord(
            e.Id,
            e.OccurredAt,
            e.OccurredBefore,
            FactLabels.For(e.Type),
            NameOf(e.SubjectName, e.SubjectId),
            e.ActorId is null ? null : NameOf(e.ActorName, e.ActorId),
            here || e.WorldId is null ? null : PlaceOf(e.WorldName ?? e.WorldId, e.InstanceId, e.InstanceName),
            Details(e));
    }

    /// <summary>The world, and the instance's own name or VRChat's number for it.</summary>
    internal static string PlaceOf(string world, string? number, string? name)
    {
        if (!string.IsNullOrWhiteSpace(name))
            return $"{world}, “{name}”";

        return number is { Length: > 0 } n ? $"{world}, instance {n}" : world;
    }

    /// <summary>
    /// The people the entries are about or by, for the lookup fact: VRChat and Discord accounts in
    /// the order first named, at most <see cref="ChatLookupFacts.MostPeople"/>, as Chat records them.
    /// </summary>
    internal static IReadOnlyList<LookedUpPerson> PeopleIn(IEnumerable<AuditEntry> entries)
    {
        var people = new List<LookedUpPerson>();
        var seen = new HashSet<(FactPlatform, string)>();

        void Add(string? platform, string? id, string? name, bool person)
        {
            if (!person || string.IsNullOrWhiteSpace(id) || people.Count >= ChatLookupFacts.MostPeople)
                return;

            FactPlatform? on = platform switch
            {
                nameof(FactPlatform.VRChat) => FactPlatform.VRChat,
                nameof(FactPlatform.Discord) => FactPlatform.Discord,
                _ => null,
            };

            if (on is { } p && seen.Add((p, id)))
                people.Add(new LookedUpPerson(p, id, name));
        }

        foreach (var e in entries)
        {
            Add(e.SubjectPlatform, e.SubjectId, e.SubjectName, e.SubjectKind == SubjectKind.Person);
            Add(e.ActorPlatform, e.ActorId, e.ActorName, true);
        }

        return people;
    }

    private static string NameOf(string? name, string id) => string.IsNullOrWhiteSpace(name) ? id : name.Trim();

    /// <summary>
    /// The entry's description, then whatever else its payload holds, then the facts recorded with
    /// it from the same decision. The model is asked for what happened, and the payload is where
    /// most entries keep it; keys the line already carries are left out.
    /// </summary>
    private static string? Details(AuditEntry e)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(e.Description))
            parts.Add(e.Description.Trim());

        if (e.Data is JsonObject data)
        {
            var rest = new JsonObject();

            foreach (var (key, value) in data)
            {
                if (value is null || key is "actorDisplayName" or "description")
                    continue;

                if (key == "text" && value is JsonValue v && v.TryGetValue<string>(out var said) && said.Trim() == e.Description?.Trim())
                    continue;

                rest[key] = value.DeepClone();
            }

            if (rest.Count > 0)
                parts.Add(rest.ToJsonString());
        }

        if (e.Linked is { Count: > 0 } linked)
            parts.Add("recorded with it: " + string.Join(", ", linked.Select(l => FactLabels.For(l.Type))));

        return parts.Count == 0 ? null : string.Join("; ", parts);
    }
}
