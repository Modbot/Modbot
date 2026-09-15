namespace Modbot.AI;

/// <summary>What was wrong with the AI settings, or the cleaned values when nothing was.</summary>
public sealed record AiSettingsCheck(string? Error, AiProvider? Provider, Uri? Endpoint, string? Model)
{
    public bool Ok => Error is null;
}

/// <summary>
/// The checks every AI setting passes before it is saved or used, in one place so the save and
/// the Test button cannot disagree about what is acceptable.
/// </summary>
public static class AiSettingsRules
{
    public const int MaxEndpointLength = 2048;
    public const int MaxModelLength = 200;
    public const int MaxApiKeyLength = 4096;

    /// <param name="enabled">
    /// With AI off, an unfinished form still saves: somebody filling in the endpoint today and the
    /// key next week should not have to type the endpoint twice. With it on, everything a call
    /// needs has to be there.
    /// </param>
    /// <param name="requireModel">False for the model list, which is what somebody picks a model from.</param>
    public static AiSettingsCheck Check(
        bool enabled, string? providerId, string? endpoint, string? model, string? apiKey, bool requireModel = true)
    {
        var provider = AiProviders.Find(providerId);
        if (provider is null)
            return Fail("Choose a provider.");

        if (apiKey is { Length: > MaxApiKeyLength })
            return Fail("The API key is too long.");

        var cleanModel = string.IsNullOrWhiteSpace(model) ? null : model.Trim();
        if (cleanModel is { Length: > MaxModelLength })
            return Fail("The model name is too long.");

        var cleanEndpoint = string.IsNullOrWhiteSpace(endpoint) ? null : endpoint.Trim();

        if (cleanEndpoint is null)
        {
            return enabled
                ? Fail("Enter the endpoint address.")
                : new AiSettingsCheck(null, provider, null, cleanModel);
        }

        if (EndpointProblem(provider, cleanEndpoint, out var uri) is { } problem)
            return Fail(problem);

        if (enabled && requireModel && cleanModel is null)
            return Fail("Enter a model.");

        return new AiSettingsCheck(null, provider, uri, cleanModel);
    }

    /// <summary>Null when the address is usable; otherwise what is wrong with it.</summary>
    public static string? EndpointProblem(AiProvider provider, string endpoint, out Uri? uri)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(endpoint);

        uri = null;

        if (endpoint.Length > MaxEndpointLength)
            return "The endpoint address is too long.";

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp)
            || string.IsNullOrEmpty(parsed.Host))
        {
            return "The endpoint must be a full address starting with https://.";
        }

        if (parsed.Scheme == Uri.UriSchemeHttp && !provider.AllowsHttp)
            return "Use an https:// address, or choose Custom for a local server.";

        // The SDK appends /chat/completions to the address; a query or fragment would end up in
        // front of that path, and a user:password part would be sent along with the key.
        if (parsed.Query.Length > 0 || parsed.Fragment.Length > 0)
            return "The endpoint address cannot contain ? or #.";

        if (parsed.UserInfo.Length > 0)
            return "Put the API key in the API key field, not in the address.";

        uri = parsed;
        return null;
    }

    /// <summary>
    /// Whether two endpoint addresses name the same place, ignoring a trailing slash and the case
    /// of the scheme and host.
    /// </summary>
    /// <remarks>
    /// Decides whether a stored key may be sent. A key typed in for OpenRouter must not follow the
    /// endpoint to another address somebody changed it to: the key is write-only precisely so that
    /// nobody can take it somewhere else, and pointing the endpoint at their own server would
    /// otherwise do exactly that.
    /// </remarks>
    public static bool SameEndpoint(string? a, string? b)
    {
        if (a is null || b is null)
            return false;

        if (!Uri.TryCreate(a.Trim(), UriKind.Absolute, out var left)
            || !Uri.TryCreate(b.Trim(), UriKind.Absolute, out var right))
        {
            return false;
        }

        return Uri.Compare(
                   left, right,
                   UriComponents.SchemeAndServer | UriComponents.UserInfo,
                   UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) == 0
               && string.Equals(
                   left.AbsolutePath.TrimEnd('/'), right.AbsolutePath.TrimEnd('/'), StringComparison.Ordinal);
    }

    private static AiSettingsCheck Fail(string error) => new(error, null, null, null);
}
