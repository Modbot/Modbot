using System.Diagnostics;
using Modbot.AI.Usage;
using Modbot.Core.Data.Entities;
using OpenAI.Chat;

namespace Modbot.AI.Calls;

/// <summary>What one provider call came back with.</summary>
/// <param name="Value">Whatever the feature made of the answer.</param>
/// <param name="ModelAnswered">The model the provider named in its reply. Null when it named none.</param>
/// <param name="Answer">The answer as text, for the call log.</param>
public sealed record AiCallAnswer<T>(T Value, string? ModelAnswered, ChatTokenUsage? Usage, string? Answer);

/// <summary>Everything one call needs, apart from the call itself.</summary>
/// <param name="Model">The model to ask for. A feature with its own model passes that; otherwise the base one.</param>
/// <param name="Prompt">Everything the model is sent, as one piece of text, for the call log.</param>
/// <param name="KeepText">
/// Whether the prompt and answer are stored straight away. True for a call somebody started from
/// a button; moderation leaves it false and keeps the text later, only for calls that flagged.
/// </param>
/// <param name="ChatTimeLimitSeconds">Chat's own timeout. Ignored by every other feature.</param>
public sealed record AiCallPlan(
    string Feature,
    AiChat Chat,
    string Model,
    string? Prompt = null,
    Guid? UserId = null,
    string? Username = null,
    bool KeepText = false,
    int? ChatTimeLimitSeconds = null);

/// <summary>How one call ended, and where it is in the call log.</summary>
/// <param name="CallId">The row that answered, or the last attempt when nothing did.</param>
/// <param name="Error">What to show when <paramref name="Outcome"/> is not <c>answered</c>.</param>
public sealed record AiCallResult<T>(
    T? Value,
    string Outcome,
    string? Error,
    Guid CallId,
    string Model,
    bool Fallback)
{
    public bool Answered => Outcome == AiCallOutcomes.Answered;
}

/// <summary>
/// The one place a provider call is made: with the feature's timeout around it, the fallback model
/// behind it, and a row in the call log and the spend ledger whatever happens.
/// </summary>
/// <remarks>
/// <para>
/// A feature hands over the call it wants made and gets back what came of it. It never sees the
/// timeout, never decides whether to try again, and never has to remember to record anything --
/// which is the point: the three things easiest to forget are the three things a moderator later
/// needs in order to know why nothing happened.
/// </para>
/// <para>
/// A timeout is an error on that call, recorded as one. A feature is never handed an empty answer
/// that looks like "the model found nothing".
/// </para>
/// </remarks>
public sealed class AiCallRunner
{
    private readonly IAiCallLog _log;
    private readonly IAiUsage _usage;

    public AiCallRunner(IAiCallLog log, IAiUsage usage)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(usage);

        _log = log;
        _usage = usage;
    }

    /// <summary>
    /// Records a call that was never made because a Modbot limit stopped it. Nothing was sent, so
    /// there is nothing to count.
    /// </summary>
    public Task<Guid> RecordLimitedAsync(string feature, string model, string? provider, string reason, Guid? userId, CancellationToken ct)
        => _log.RecordAsync(
            new AiCallEntry(feature, model, null, provider, AiCallOutcomes.Limited, 0, Error: reason, UserId: userId), ct);

    /// <summary>
    /// Makes the call, once on the model asked for and once more on the fallback when the first
    /// attempt failed in a way a different model could survive.
    /// </summary>
    public async Task<AiCallResult<T>> RunAsync<T>(
        AiCallPlan plan,
        Func<ChatClient, CancellationToken, Task<AiCallAnswer<T>>> call,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(call);

        var timeout = AiTimeouts.For(plan.Feature, plan.ChatTimeLimitSeconds);

        var first = await AttemptAsync(plan, plan.Model, fallback: false, timeout, call, ct).ConfigureAwait(false);
        if (first.Answered || first.Error is null)
            return first.Result;

        var fallbackModel = plan.Chat.FallbackModel;

        if (string.IsNullOrWhiteSpace(fallbackModel)
            || string.Equals(fallbackModel, plan.Model, StringComparison.OrdinalIgnoreCase)
            || !AiFallbackRules.ShouldFallBack(first.Error, first.Result.Outcome == AiCallOutcomes.TimedOut))
        {
            return first.Result;
        }

        var second = await AttemptAsync(plan, fallbackModel, fallback: true, timeout, call, ct).ConfigureAwait(false);
        return second.Result;
    }

    private async Task<(AiCallResult<T> Result, bool Answered, Exception? Error)> AttemptAsync<T>(
        AiCallPlan plan,
        string model,
        bool fallback,
        TimeSpan timeout,
        Func<ChatClient, CancellationToken, Task<AiCallAnswer<T>>> call,
        CancellationToken ct)
    {
        var client = string.Equals(model, plan.Chat.Model, StringComparison.Ordinal)
            ? plan.Chat.Chat
            : plan.Chat.Client.GetChatClient(model);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);

        var started = Stopwatch.GetTimestamp();

        try
        {
            var answer = await call(client, deadline.Token).ConfigureAwait(false);

            var answered = string.IsNullOrWhiteSpace(answer.ModelAnswered) ? model : answer.ModelAnswered;

            var id = await RecordAsync(
                plan, model, answered, fallback, AiCallOutcomes.Answered, error: null,
                answer.Usage, Elapsed(started), answer.Answer, ct).ConfigureAwait(false);

            return (new AiCallResult<T>(answer.Value, AiCallOutcomes.Answered, null, id, answered, fallback), true, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller went away. Nothing is recorded and nothing is tried again: this is not a
            // failure of the model, and the row would say nothing anybody needs.
            throw;
        }
        catch (Exception e)
        {
            var timedOut = deadline.IsCancellationRequested;
            var outcome = AiFallbackRules.OutcomeOf(e, timedOut);
            var message = timedOut
                ? $"{plan.Chat.Endpoint?.Host ?? "The AI endpoint"} did not answer in time."
                : Describe(e, plan.Chat.Endpoint);

            var id = await RecordAsync(
                plan, model, answered: null, fallback, outcome, message,
                usage: null, Elapsed(started), answer: null, ct).ConfigureAwait(false);

            return (new AiCallResult<T>(default, outcome, message, id, model, fallback), false, e);
        }
    }

    private async Task<Guid> RecordAsync(
        AiCallPlan plan,
        string asked,
        string? answered,
        bool fallback,
        string outcome,
        string? error,
        ChatTokenUsage? usage,
        int durationMs,
        string? answer,
        CancellationToken ct)
    {
        // Recorded even when the caller is going away: the provider charged for it either way.
        // CancellationToken.None rather than ct, for the same reason.
        var write = ct.IsCancellationRequested ? CancellationToken.None : ct;

        var id = await _log.RecordAsync(
            new AiCallEntry(
                plan.Feature, asked, answered, plan.Chat.Provider, outcome, durationMs, fallback, error, usage,
                plan.UserId, plan.Username, plan.Prompt, answer, plan.KeepText),
            write).ConfigureAwait(false);

        // The spend ledger keeps its own row, under the model that answered: that is the model the
        // provider billed, and the one a price is looked up under.
        await _usage.RecordAsync(plan.Feature, plan.UserId, answered ?? asked, plan.Chat.Provider, usage, write)
            .ConfigureAwait(false);

        return id;
    }

    private static string Describe(Exception e, Uri? endpoint) =>
        endpoint is null ? "The AI provider could not answer." : AiClients.Describe(e, endpoint);

    private static int Elapsed(long started) =>
        (int)Math.Min(int.MaxValue, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
}
