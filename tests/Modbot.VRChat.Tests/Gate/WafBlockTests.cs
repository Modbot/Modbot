namespace Modbot.VRChat.Tests.Gate;

/// <summary>
/// Telling a Cloudflare block apart from a VRChat error, from nothing but a body.
/// </summary>
/// <remarks>
/// The bodies here are the shapes actually seen in the wild, including the one the SDK produces
/// on its error path, where the response is rebuilt with no headers and the body survives only
/// inside the exception message (spec 4.1). Getting this wrong in either direction is expensive:
/// a missed block reads as "permission denied" and sends an operator to audit group roles, and a
/// false positive tells them to buy a proxy they do not need.
/// </remarks>
public class WafBlockTests
{
    [Fact]
    public void ACloudflareInterstitialIsRecognisedWithItsCode()
    {
        const string body =
            "<!DOCTYPE html><html><head><title>Attention Required! | Cloudflare</title></head>"
            + "<body><h1>Sorry, you have been blocked</h1><p>Error code: 1020</p>"
            + "<p>Cloudflare Ray ID: 8f2c1a</p></body></html>";

        Assert.True(WafBlock.TryClassify(403, null, body, out var code));
        Assert.Equal(1020, code);
    }

    [Fact]
    public void ABlockThatArrivesOnlyInsideTheSdksErrorMessageIsStillRecognised()
    {
        // The form spec 4.1 warns about: the catch path keeps the body only as
        // "Error calling {method}: {body}", with the headers thrown away.
        const string message =
            "Error calling GroupsApi->GetGroupMembers: "
            + "{\"success\":false,\"code\":1015,\"message\":\"cloudflare blocked this request\"}";

        Assert.True(WafBlock.TryClassify(403, message, rawContent: null, out var code));
        Assert.Equal(1015, code);
    }

    [Fact]
    public void AnUnnumberedCloudflarePageIsStillABlock()
    {
        const string body = "<html><body>Checking your browser... __cf_chl_jschl_tk__</body></html>";

        Assert.True(WafBlock.TryClassify(503, null, body, out var code));

        // Zero rather than null: recognisably Cloudflare, no code to report. The UI needs the
        // distinction between "blocked, code unknown" and "not blocked".
        Assert.Equal(0, code);
    }

    [Fact]
    public void AVRChatPermissionsErrorIsNotABlock()
    {
        const string body = """{"error":{"message":"Missing permission: group-members-manage","status_code":403}}""";

        Assert.False(WafBlock.TryClassify(403, "Forbidden", body, out var code));
        Assert.Null(code);
    }

    [Fact]
    public void ARateLimitIsNeverClassifiedAsAWafBlockEvenWhenCloudflareIssuedIt()
    {
        const string body = "<html><body>Cloudflare: you are being rate limited. Error code: 1015</body></html>";

        // The answer to a 429 is the cold stop (spec 4.3.1). Routing one here would tell an
        // operator to change IP in response to a rate limit, which is the behaviour the limits
        // exist to prevent and the thing spec 2.3.1 refuses to do.
        Assert.False(WafBlock.TryClassify(429, null, body, out _));
    }

    [Fact]
    public void AnEmptyBodyIsNotGuessedAt()
    {
        Assert.False(WafBlock.TryClassify(500, null, null, out _));
        Assert.False(WafBlock.TryClassify(500, "  ", "", out _));
    }

    [Fact]
    public void ThePayloadIsRecoveredFromTheSdksWrapper()
    {
        Assert.Equal(
            """{"error":"nope"}""",
            WafBlock.Payload("""Error calling GroupsApi->GetGroup: {"error":"nope"}"""));

        // An HTML body starts at character zero and must not be trimmed.
        Assert.Equal("<html>x</html>", WafBlock.Payload("<html>x</html>"));
        Assert.Null(WafBlock.Payload(null));
    }
}
