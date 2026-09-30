using System.Text.Json;
using System.Text.Json.Nodes;
using Modbot.Api.Features.Events;

namespace Modbot.Api.Tests.Features.Events;

/// <summary>
/// The API reference names an event's fields the way the server sends them.
/// </summary>
/// <remarks>
/// The event stream is written in snake_case and the reference is made with camelCase settings, so
/// for a while the reference said <c>occurredAt</c> for what arrives as <c>occurred_at</c>, and a
/// client made from it read six empty fields on every event. This compares what the server writes
/// with the committed reference (docs/openapi/modbot.json), so the two cannot part again without a
/// build or a test noticing.
/// </remarks>
public class EventSchemaTests
{
    [Fact]
    public void TheReferenceNamesEveryEventFieldTheServerSends()
    {
        var envelope = EventEnvelopes.Test(
            Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UnixEpoch, "actor-id", "Actor");

        AssertSameNames("EventEnvelope", Sent(envelope));
        AssertSameNames("EventSubject", Sent(envelope.Subject));
        AssertSameNames("EventActor", Sent(envelope.Actor));
    }

    [Fact]
    public void TheReferenceNamesEveryFieldOfTheAnswerToAPoll()
    {
        var envelope = EventEnvelopes.Test(
            Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UnixEpoch, "actor-id", "Actor");
        var answer = new EventPollResponse(
            [envelope], "1", false, new NoticeMessage("notice", "history_trimmed", "Trimmed."));

        AssertSameNames("EventPollResponse", Sent(answer));
        AssertSameNames("NoticeMessage", Sent(answer.Notice));
    }

    [Fact]
    public void TwoWordFieldsAreWrittenInSnakeCase()
    {
        var envelope = EventEnvelopes.Test(
            Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UnixEpoch, "actor-id", "Actor");

        var names = Sent(envelope);

        foreach (var name in new[] { "type_raw", "occurred_at", "occurred_before", "observed_at", "world_id", "instance_id" })
            Assert.Contains(name, names);
    }

    private static List<string> Sent(object? value)
    {
        Assert.NotNull(value);

        var written = JsonSerializer.SerializeToNode(value, value.GetType(), EventEnvelopes.JsonOptions);
        return [.. written!.AsObject().Select(p => p.Key)];
    }

    private static void AssertSameNames(string schema, List<string> sent)
    {
        var properties = Reference()["components"]!["schemas"]![schema]!["properties"]!.AsObject();
        var described = properties.Select(p => p.Key).Order(StringComparer.Ordinal).ToList();

        Assert.Equal(sent.Order(StringComparer.Ordinal).ToList(), described);
    }

    private static JsonNode Reference()
        => JsonNode.Parse(File.ReadAllText(Path.Combine(FindRepoRoot(), "docs", "openapi", "modbot.json")))!;

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Modbot.slnx")))
            dir = dir.Parent;

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate Modbot.slnx.");
    }
}
