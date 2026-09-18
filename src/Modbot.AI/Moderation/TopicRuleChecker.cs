using Microsoft.EntityFrameworkCore;
using Modbot.AI.Calls;
using Modbot.AI.Usage;
using Modbot.Core.Data;
using Modbot.Core.Logging;
using Modbot.Core.Time;
using Modbot.Moderation;
using OpenAI.Chat;
using Serilog;

namespace Modbot.AI.Moderation;

/// <summary>
/// The AI half of an AutoMod check (AutoMod design §4): asks the model which topics each piece of
/// text matches, with the pictures that belong to it where the model reads them.
/// </summary>
/// <remarks>
/// <para>
/// Called by the engine only when AI is on and the topic tool is switched on; handed pictures only
/// when the picture tool is. What it adds is everything that needs an AI client: the spend and
/// daily-call limits, the batching, the prompt, the call through <see cref="AiCallRunner"/> and the
/// reading of the answer.
/// </para>
/// <para>
/// A batch whose answer cannot be matched back to the texts that were sent -- a model that ignored
/// the shape it was asked for, or named texts nobody sent -- is not thrown away: the texts go again
/// one at a time, which is what Modbot did before batching existed, so the worst case is the old
/// cost rather than a lost check.
/// </para>
/// </remarks>
public sealed class TopicRuleChecker : IAiRuleChecker
{
    private readonly ModbotContext _db;
    private readonly IAiClients _ai;
    private readonly IAiUsage _usage;
    private readonly AiCallRunner _runner;
    private readonly IModbotClock _clock;
    private readonly ModerationPictures _pictures;
    private readonly ILogger _log;

    public TopicRuleChecker(
        ModbotContext db,
        IAiClients ai,
        IAiUsage usage,
        AiCallRunner runner,
        IModbotClock clock,
        ModerationPictures pictures)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(ai);
        ArgumentNullException.ThrowIfNull(usage);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(pictures);

        _db = db;
        _ai = ai;
        _usage = usage;
        _runner = runner;
        _clock = clock;
        _pictures = pictures;
        _log = Log.Logger.ForContext(LogArea.Name, LogArea.Moderation);
    }

    public async Task<AiRuleAnswer> CheckAsync(IReadOnlyList<AiRuleText> texts, AiAsker asker, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(texts);
        ArgumentNullException.ThrowIfNull(asker);

        if (texts.Count == 0)
            return new AiRuleAnswer([], null, null, new Dictionary<Guid, (string, string)>());

        var chat = await _ai.GetChatAsync(ct).ConfigureAwait(false);
        if (chat is null)
            return AiRuleAnswer.Off(NoAiRuleChecker.Reason);

        // A rule may only check pictures if the model reads them; the settings page will not offer
        // it otherwise, but the model can be changed after the rule was saved (design §17). Asked
        // only when some text carries pictures, so the ordinary check is not a query heavier.
        var wantsPictures = texts.Any(t => t.Pictures.Count > 0);
        var readsPictures = wantsPictures && await ModelReadsPicturesAsync(chat.Model, ct).ConfigureAwait(false);

        // One key per topic across the whole request, so the topics are listed once however many
        // pieces of text are checked against them.
        var keys = texts.SelectMany(t => t.Topics).DistinctBy(t => t.Id)
            .Select((t, i) => new TopicToCheck($"t{i + 1}", t.Id, t.Name, t.Instructions, t.Sensitivity))
            .ToDictionary(t => t.Id);

        var toAsk = new List<TopicText>(texts.Count);

        foreach (var text in texts)
        {
            var pictures = readsPictures
                ? await _pictures.ReadyAsync(text.Pictures, ModerationPictures.SendsLinks(chat.Provider), ct).ConfigureAwait(false)
                : [];

            toAsk.Add(new TopicText(text.Key, text.Target, text.Text, [.. text.Topics.Select(t => keys[t.Id])], text.Context, pictures));
        }

        var asked = await AskAsync(chat, toAsk, asker, ct).ConfigureAwait(false);

        var hits = asked.Hits
            .Select(h => new AiRuleHit(h.Hit.TextKey, h.Hit.Topic.Id, h.Hit.Why, h.Hit.Quote, h.CallId, h.Hit.Picture?.Label, h.Hit.Picture?.Url))
            .ToList();

        return new AiRuleAnswer(hits, asked.Error, asked.Model ?? chat.Model, asked.CallTexts);
    }

    /// <summary>Whether the model in use reads pictures, as the model list last said (design §17).</summary>
    /// <remarks>
    /// A model the catalogue has never heard of is treated as reading none: a picture sent to a
    /// model that cannot read it is a bill for nothing and an answer about the words alone.
    /// </remarks>
    private async Task<bool> ModelReadsPicturesAsync(string model, CancellationToken ct)
    {
        var modalities = await _db.AiCatalogModels.AsNoTracking()
            .Where(m => m.Model == model)
            .Select(m => m.InputModalities)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        return modalities is not null && modalities.Contains("image", StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>What one batched topic check answers, and what its calls were sent.</summary>
    private sealed record Asked(
        List<(TopicHit Hit, Guid CallId)> Hits,
        string? Error,
        string? Model,
        Dictionary<Guid, (string Prompt, string Answer)> CallTexts);

    private async Task<Asked> AskAsync(
        AiChat chat, IReadOnlyList<TopicText> texts, AiAsker asker, CancellationToken ct)
    {
        var size = await BatchSizeAsync(ct).ConfigureAwait(false);
        var hits = new List<(TopicHit, Guid)>();
        var callTexts = new Dictionary<Guid, (string, string)>();
        string? error = null;
        string? model = null;

        foreach (var batch in texts.Chunk(size))
        {
            var answer = await OneCallAsync(chat, batch, asker, callTexts, ct).ConfigureAwait(false);
            model ??= answer.Model;

            if (answer.Stop is { } stop)
                return new Asked(hits, stop, model, callTexts);

            // One unreadable answer costs the batch a retry, one text per call, rather than
            // costing every text in it its check.
            if (answer.Check is { Unreadable: true } && batch.Length > 1)
            {
                foreach (var one in batch)
                {
                    var single = await OneCallAsync(chat, [one], asker, callTexts, ct).ConfigureAwait(false);
                    model ??= single.Model;

                    if (single.Stop is { } singleStop)
                        return new Asked(hits, singleStop, model, callTexts);

                    if (single.Check is { Error: not null } bad)
                        error = bad.Error;
                    else if (single.Check is { } ok)
                        hits.AddRange(ok.Hits.Select(h => (h, single.CallId)));
                }

                continue;
            }

            if (answer.Check is { Error: not null } failed)
            {
                error = failed.Error;
                continue;
            }

            if (answer.Check is { } read)
                hits.AddRange(read.Hits.Select(h => (h, answer.CallId)));
        }

        return new Asked(hits, error, model, callTexts);
    }

    /// <param name="Stop">A limit that ends the whole check, not just this call.</param>
    private sealed record OneCall(TopicCheck? Check, Guid CallId, string? Stop, string? Model);

    private async Task<OneCall> OneCallAsync(
        AiChat chat,
        IReadOnlyList<TopicText> batch,
        AiAsker asker,
        Dictionary<Guid, (string Prompt, string Answer)> callTexts,
        CancellationToken ct)
    {
        // The spend limits are moderation's share of the AI bill and the bill as a whole; the daily
        // call limit is this screen's own brake. Any of them stops AI topics, and term lists carry on.
        if (await _usage.LimitReachedAsync(AiFeatures.Moderation, ct).ConfigureAwait(false) is { } reached)
        {
            await _runner.RecordLimitedAsync(AiFeatures.Moderation, chat.Model, chat.Provider, reached.Message, asker.UserId, ct)
                .ConfigureAwait(false);
            return new OneCall(null, Guid.Empty, reached.Message, null);
        }

        if (!await AiCallAllowance.TryUseAsync(_db, _clock.UtcNow, ct).ConfigureAwait(false))
        {
            await _runner.RecordLimitedAsync(AiFeatures.Moderation, chat.Model, chat.Provider, AiCallAllowance.Reached, asker.UserId, ct)
                .ConfigureAwait(false);
            return new OneCall(null, Guid.Empty, AiCallAllowance.Reached, null);
        }

        var prompt = TopicClassifier.Prompt(batch, TopicClassifier.NewMarker());

        var plan = new AiCallPlan(
            AiFeatures.Moderation, chat, chat.Model,
            Prompt: TopicClassifier.AsOneText(prompt),
            UserId: asker.UserId,
            Username: asker.Username,
            KeepText: asker.KeepText);

        var result = await _runner.RunAsync(plan, async (client, token) =>
        {
            // The instructions first and unchanged, so a provider that caches prefixes can; the
            // topics next; each member's text alone in a message of its own after them.
            List<ChatMessage> messages =
            [
                AiPromptCache.Instructions(TopicClassifier.SystemPrompt, chat.Provider),
                new UserChatMessage(prompt.Instructions),
                .. TopicClassifier.ContentMessages(prompt, batch),
            ];

            ChatCompletion completion = await client.CompleteChatAsync(
                messages,
                TopicClassifier.Options(chat.Provider, batch.Count, TopicClassifier.AnyPictures(batch)),
                token).ConfigureAwait(false);

            var reply = string.Concat(completion.Content
                .Where(p => p.Kind == ChatMessageContentPartKind.Text)
                .Select(p => p.Text));

            return new AiCallAnswer<string>(reply, completion.Model, completion.Usage, reply);
        }, ct).ConfigureAwait(false);

        if (!result.Answered)
        {
            _log.Warning("AI topic check failed: {Error}", result.Error);
            return new OneCall(new TopicCheck([], result.Error), result.CallId, null, result.Model);
        }

        var check = TopicClassifier.Read(result.Value!, batch, prompt.Marker);
        callTexts[result.CallId] = (plan.Prompt!, result.Value!);

        if (check.Error is not null)
            _log.Warning("AI topic check failed: {Error}", check.Error);

        return new OneCall(check, result.CallId, null, result.Model);
    }

    /// <summary>How many pieces of text may go in one call. At least one.</summary>
    private async Task<int> BatchSizeAsync(CancellationToken ct)
    {
        var size = await _db.Settings.AsNoTracking().Where(s => s.Id == 1)
            .Select(s => (int?)s.AiModerationProfileBatchSize)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false) ?? 1;

        // Four fields per profile, so a batch of five profiles is twenty pieces of text.
        return Math.Clamp(size, 1, 20) * 4;
    }
}

/// <summary>Keeps a flagging call's prompt and answer in the call log (AI moderation design §4.2).</summary>
public sealed class AiCallTextKeeper : IAiCallTexts
{
    private readonly IAiCallLog _calls;

    public AiCallTextKeeper(IAiCallLog calls)
    {
        ArgumentNullException.ThrowIfNull(calls);
        _calls = calls;
    }

    public Task KeepAsync(Guid callId, string prompt, string answer, CancellationToken ct)
        => _calls.KeepTextAsync(callId, prompt, answer, ct);
}
