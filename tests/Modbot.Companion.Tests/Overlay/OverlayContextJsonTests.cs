using System.Text.Json;
using Modbot.Companion.Overlay;
using Modbot.Core.Users;

namespace Modbot.Companion.Tests.Overlay;

/// <summary>
/// The roster and alert shapes as the server actually sends them. The rank travels by name, and
/// a server too old to send it leaves the field out rather than breaking the read.
/// </summary>
public class OverlayContextJsonTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public void ARosterRowCarriesTheRankByName()
    {
        const string body = """
            {"instanceId":"39911","members":[
              {"subjectId":"usr_a","displayName":"Rin","standing":"Flagged","priorActions":2,"flags":["2 prior actions"],"trustRank":"KnownUser"},
              {"subjectId":"usr_b","displayName":"Mei","standing":"Member","priorActions":0,"flags":[],"trustRank":null}
            ]}
            """;

        var context = JsonSerializer.Deserialize<InstanceContext>(body, Json)!;

        Assert.Equal(TrustRank.KnownUser, context.Members[0].TrustRank);
        Assert.Null(context.Members[1].TrustRank);
    }

    [Fact]
    public void AServerThatDoesNotSendTheRankStillAnswers()
    {
        const string roster = """{"instanceId":"39911","members":[{"subjectId":"usr_a","displayName":"Rin","standing":"Ordinary","priorActions":0,"flags":[]}]}""";
        const string summary = """{"subjectId":"usr_a","displayName":"Rin","standing":"Ordinary","priorActions":0,"joinedAt":null,"flags":[],"roles":[]}""";
        const string alert = """{"alertId":"a","subjectId":"usr_a","displayName":"Rin","instanceId":"39911","reason":"why","priorActions":1,"raisedAt":"2026-09-12T20:14:07Z"}""";

        Assert.Null(JsonSerializer.Deserialize<InstanceContext>(roster, Json)!.Members[0].TrustRank);
        Assert.Null(JsonSerializer.Deserialize<UserSummary>(summary, Json)!.TrustRank);
        Assert.Null(JsonSerializer.Deserialize<FlaggedJoinAlert>(alert, Json)!.TrustRank);
    }

    [Fact]
    public void AnAlertAndASummaryCarryTheRank()
    {
        const string summary = """{"subjectId":"usr_a","displayName":"Rin","standing":"Staff","priorActions":0,"joinedAt":null,"flags":[],"roles":["Mod"],"trustRank":"VRChatTeam"}""";
        const string alert = """{"alertId":"a","subjectId":"usr_a","displayName":"Rin","instanceId":"39911","reason":"why","priorActions":1,"raisedAt":"2026-09-12T20:14:07Z","trustRank":"Nuisance"}""";

        Assert.Equal(TrustRank.VRChatTeam, JsonSerializer.Deserialize<UserSummary>(summary, Json)!.TrustRank);
        Assert.Equal(TrustRank.Nuisance, JsonSerializer.Deserialize<FlaggedJoinAlert>(alert, Json)!.TrustRank);
    }
}
