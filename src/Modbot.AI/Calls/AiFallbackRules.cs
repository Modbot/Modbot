using System.ClientModel;
using System.Text.Json;
using Modbot.Core.Data.Entities;

namespace Modbot.AI.Calls;

/// <summary>
/// When a failed call is worth trying again on the fallback model, and what to record for it.
/// </summary>
/// <remarks>
/// The question this answers is "would a different model have done any better?". A provider that
/// is down, a model that is busy, a model id this account may not use: yes. A wrong key, an
/// exhausted budget, or the person closing the page: no -- the second call would spend again and
/// fail the same way, or nobody is waiting for it any more.
/// </remarks>
public static class AiFallbackRules
{
    /// <summary>
    /// Statuses that are about the key or the account rather than the model, and never fall back.
    /// </summary>
    /// <remarks>
    /// 401 is a wrong key, 402 is an empty account, 403 is a key that is not allowed here. Another
    /// model on the same key fails identically, so trying one only wastes a round trip.
    /// </remarks>
    private static readonly int[] NeverFallBack = [401, 402, 403];

    /// <summary>What the exception means for the call log.</summary>
    public static string OutcomeOf(Exception error, bool timedOut)
    {
        ArgumentNullException.ThrowIfNull(error);

        if (timedOut || error is OperationCanceledException)
            return AiCallOutcomes.TimedOut;

        // A status of 0 means the request never got an answer at all.
        return error is ClientResultException { Status: > 0 } ? AiCallOutcomes.Refused : AiCallOutcomes.Error;
    }

    /// <summary>
    /// Whether to try the fallback model once. <paramref name="timedOut"/> is the feature's own
    /// timeout running out, which is not the same as the caller going away.
    /// </summary>
    public static bool ShouldFallBack(Exception error, bool timedOut)
    {
        ArgumentNullException.ThrowIfNull(error);

        if (timedOut)
            return true;

        return error switch
        {
            // The caller went away. Nobody is waiting for a second answer.
            OperationCanceledException => false,

            ClientResultException { Status: var status } => status == 0 || !NeverFallBack.Contains(status),

            // Could not reach the host, or it answered with something that is not an OpenAI reply.
            HttpRequestException or JsonException or FormatException or InvalidOperationException => true,

            _ => false,
        };
    }
}
