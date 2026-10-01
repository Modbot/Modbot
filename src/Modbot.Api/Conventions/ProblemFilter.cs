using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Modbot.Api.Conventions;

/// <summary>
/// Turns an error an endpoint returned into the problem shape (<see cref="Problems"/>), on every
/// endpoint <see cref="ApiSurface.MapModbotApi"/> maps.
/// </summary>
/// <remarks>
/// <para>
/// It looks at the result before it runs, so it sees what the endpoint meant: a status, and the
/// value it would have written. A status below 400 passes through untouched, and so does anything
/// it cannot read as a JSON object -- text, a file, an <c>error</c> that is an object from a
/// protocol with its own rules -- because guessing at those would change what a client already
/// depends on.
/// </para>
/// <para>
/// An error with no body (<c>Results.NotFound()</c>, <c>Results.StatusCode(429)</c>) gets one
/// here too. The few that are not results at all, such as a refusal from sign-in, are left to
/// <see cref="ProblemMiddleware"/>.
/// </para>
/// </remarks>
public static class ProblemFilter
{
    public static async ValueTask<object?> ShapeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var result = await next(context);

        return Problems.Covers(context.HttpContext.Request.Path)
            ? Reshape(result, context.HttpContext) ?? result
            : result;
    }

    /// <summary>The problem a result stands for, or null to keep the result as it is.</summary>
    internal static IResult? Reshape(object? result, HttpContext http)
    {
        var inner = result;
        while (inner is INestedHttpResult nested)
            inner = nested.Result;

        if (inner is null or Problems.ProblemResult)
            return null;

        if (inner is not IStatusCodeHttpResult { StatusCode: { } status } || status < 400)
            return null;

        if (inner is not IValueHttpResult valued)
        {
            // Text with a status of its own (Results.Text) is a protocol's answer; a bare status
            // (Results.NotFound(), Results.StatusCode(429)) is ours to give a body.
            return inner is IContentTypeHttpResult ? null : Problems.Of(status, null);
        }

        switch (valued.Value)
        {
            case null:
                return Problems.Of(status, null);

            case ProblemDetails details:
                // ASP.NET's own problem: its detail is the sentence, its extensions (and a
                // validation problem's errors) are kept, and its type and title become the code's.
                var options = Problems.JsonOptionsOf(http);
                var extensions = new JsonObject();
                foreach (var (name, extra) in details.Extensions)
                    extensions[name] = JsonSerializer.SerializeToNode(extra, options);
                if (details is HttpValidationProblemDetails validation)
                    extensions["errors"] = JsonSerializer.SerializeToNode(validation.Errors, options);
                return Problems.Of(details.Status ?? status, details.Detail ?? details.Title, extra: extensions);

            case string sentence:
                return Problems.Of(status, sentence);

            case var value:
                return FromObject(status, value, OptionsOf(inner) ?? Problems.JsonOptionsOf(http));
        }
    }

    private static IResult? FromObject(int status, object value, JsonSerializerOptions options)
    {
        JsonNode? node;
        try
        {
            node = JsonSerializer.SerializeToNode(value, value.GetType(), options);
        }
        catch (NotSupportedException)
        {
            return null;
        }

        if (node is not JsonObject body)
            return null;

        // An `error` that is not a sentence (an object, say) belongs to a protocol of its own.
        if (body["error"] is { } error && error.GetValueKind() is not (JsonValueKind.String or JsonValueKind.Null))
            return null;

        return Problems.Of(status, null, extra: body);
    }

    /// <summary>The serializer settings a <c>Results.Json</c> result was given, when it was given any.</summary>
    private static JsonSerializerOptions? OptionsOf(object result)
        => result.GetType().GetProperty("JsonSerializerOptions")?.GetValue(result) as JsonSerializerOptions;
}
