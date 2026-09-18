using Modbot.Core.Data.Entities;
using Modbot.Discord.ModerationLog;

namespace Modbot.Discord.Tests.ModerationLog;

/// <summary>A fact in, a card out. Nothing here touches a database or a gateway.</summary>
public class ModerationEventEmbedTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 13, 2, 25, 36, TimeSpan.Zero);

    private static ModerationEventView Ban(string? subjectName = "jessie", string? actorName = "E-Ray") => new(
        52,
        FactType.MemberBanned,
        At,
        "usr_c9094d86-1846-43eb-b79d-7e3dc318f42a",
        subjectName,
        "usr_2a323be9-ac4e-4502-af07-357d79c48ccf",
        actorName,
        "User jessie was preemptively banned by E-Ray.");

    [Fact]
    public void ABan_IsTitledInPlainWords_WithTheSubjectAsAuthor()
    {
        var embed = ModerationEventEmbed.For(Ban(), "https://modbot.example.com");

        Assert.Equal("Banned", embed.Title);

        // The subject heads the card as the author line, not a field.
        Assert.Equal("jessie", embed.AuthorName);
        Assert.Equal(
            "https://modbot.example.com/audit?subject=usr_c9094d86-1846-43eb-b79d-7e3dc318f42a",
            embed.AuthorUrl);
        Assert.DoesNotContain(embed.Fields, f => f.Name == "Who");

        var by = Assert.Single(embed.Fields, f => f.Name == "By");
        Assert.Equal(
            "[E-Ray](https://modbot.example.com/audit?subject=usr_2a323be9-ac4e-4502-af07-357d79c48ccf)",
            by.Value);

        var when = Assert.Single(embed.Fields, f => f.Name == "When");
        // Discord's own timestamp markup: the reader's time zone, not the server's.
        Assert.Contains($"<t:{At.ToUnixTimeSeconds()}:f>", when.Value, StringComparison.Ordinal);
        Assert.Equal(At, embed.Timestamp);
    }

    [Fact]
    public void TheBy_HasNoLink_WhenThereIsNoPublicAddress()
    {
        var embed = ModerationEventEmbed.For(Ban(), null);

        var by = Assert.Single(embed.Fields, f => f.Name == "By");
        Assert.Equal("E-Ray", by.Value);
    }

    [Fact]
    public void TheLink_IsBuiltFromThePublicAddressOnly()
    {
        var embed = ModerationEventEmbed.For(Ban(), "https://modbot.example.com/");

        Assert.Equal(
            "https://modbot.example.com/audit?subject=usr_c9094d86-1846-43eb-b79d-7e3dc318f42a",
            embed.Url);
    }

    [Fact]
    public void NoPublicAddress_MeansNoLink()
    {
        var embed = ModerationEventEmbed.For(Ban(), null);

        Assert.Null(embed.Url);
        Assert.Null(embed.AuthorUrl);
    }

    [Fact]
    public void AnUnknownName_FallsBackToTheRawId_OnTheAuthorLine()
    {
        var embed = ModerationEventEmbed.For(Ban(subjectName: null), null);

        Assert.Equal("usr_c9094d86-1846-43eb-b79d-7e3dc318f42a", embed.AuthorName);
    }

    [Fact]
    public void TheAuthorName_IsNotEscaped_BecauseTheAuthorLineIsNotMarkdown()
    {
        var embed = ModerationEventEmbed.For(Ban(subjectName: "**@everyone**"), null);

        // The author line is a slot Discord prints literally, so nothing is backslash-escaped --
        // escaping it would leave the backslashes on screen.
        Assert.Equal("**@everyone**", embed.AuthorName);
    }

    [Fact]
    public void TheAuthorName_StripsControlCharacters()
    {
        var embed = ModerationEventEmbed.For(Ban(subjectName: "jessie\nthe\tbanned"), null);

        Assert.DoesNotContain('\n', embed.AuthorName!);
        Assert.DoesNotContain('\t', embed.AuthorName!);
    }

    [Fact]
    public void VRChatsOwnSentence_IsQuoted()
    {
        var embed = ModerationEventEmbed.For(Ban(), null);

        Assert.StartsWith("> User jessie was preemptively banned by E-Ray.", embed.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void ANoActorEvent_HasNoByField()
    {
        var view = Ban() with { ActorId = null, ActorName = null };

        var embed = ModerationEventEmbed.For(view, null);

        Assert.DoesNotContain(embed.Fields, f => f.Name == "By");
    }

    [Theory]
    [InlineData(FactType.MemberUnbanned, "Unbanned")]
    [InlineData(FactType.MemberKicked, "Kicked from the group")]
    [InlineData(FactType.GroupInstanceKick, "Kicked from an instance")]
    [InlineData(FactType.GroupInstanceWarn, "Warned in an instance")]
    [InlineData(FactType.JoinRequestRejected, "Join request rejected")]
    [InlineData(FactType.JoinRequestBlocked, "Join request blocked")]
    [InlineData(FactType.RoleGranted, "Role granted")]
    [InlineData(FactType.RoleRevoked, "Role revoked")]
    public void EveryPostableType_HasAPlainLabel(string type, string label)
    {
        Assert.Equal(label, ModerationEventEmbed.LabelFor(type));
    }

    [Fact]
    public void BansAndUnbans_AreColouredDifferently()
    {
        Assert.NotEqual(
            ModerationEventEmbed.ColorFor(FactType.MemberBanned),
            ModerationEventEmbed.ColorFor(FactType.MemberUnbanned));
    }

    [Fact]
    public void Fit_CutsWithAnEllipsis_AndNeverLeavesADanglingEscape()
    {
        Assert.Equal("abc", ModerationEventEmbed.Fit("abc", 3));
        Assert.Equal("ab…", ModerationEventEmbed.Fit("abcd", 3));

        // "ab\" + "*" cut at 4 would end in a lone backslash; the cut steps back over it.
        Assert.Equal("ab…", ModerationEventEmbed.Fit(@"ab\*cd", 4));
    }

    [Fact]
    public void FromAFact_ReadsTheActorNameFromThePayload_WhenNoProfileIsStored()
    {
        var fact = new ModbotEvent
        {
            Id = 7,
            Type = FactType.GroupInstanceKick,
            OccurredAt = At,
            ObservedAt = At,
            SubjectPlatform = FactPlatform.VRChat,
            SubjectId = "usr_target",
            ActorPlatform = FactPlatform.VRChat,
            ActorId = "usr_actor",
            Source = FactSource.AuditLog,
            Data = """{"actorDisplayName":"Moderator A","description":"kicked","auditEntryId":"gaud_1"}""",
        };

        var view = ModerationEventView.From(fact, new Dictionary<string, string?>(StringComparer.Ordinal));

        Assert.Equal("Moderator A", view.ActorName);
        Assert.Equal("kicked", view.Description);
        Assert.Null(view.SubjectName);
    }

    [Fact]
    public void FromAFact_PrefersTheStoredProfileName()
    {
        var fact = new ModbotEvent
        {
            Id = 8,
            Type = FactType.MemberBanned,
            OccurredAt = At,
            ObservedAt = At,
            SubjectPlatform = FactPlatform.VRChat,
            SubjectId = "usr_target",
            ActorPlatform = FactPlatform.VRChat,
            ActorId = "usr_actor",
            Source = FactSource.AuditLog,
            Data = """{"actorDisplayName":"Old Name"}""",
        };

        var names = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["usr_target"] = "Target Now",
            ["usr_actor"] = "Actor Now",
        };

        var view = ModerationEventView.From(fact, names);

        Assert.Equal("Target Now", view.SubjectName);
        Assert.Equal("Actor Now", view.ActorName);
    }
}

public class PersonLinkTests
{
    [Fact]
    public void BuildsFromThePublicAddress_AndEscapesTheId()
    {
        Assert.Equal(
            "https://m.example.com/audit?subject=usr%20odd%2Fid",
            PersonLink.For("https://m.example.com/", "usr odd/id"));
    }

    [Fact]
    public void NoPublicAddress_NoLink()
    {
        Assert.Null(PersonLink.For(null, "usr_1"));
        Assert.Null(PersonLink.For("  ", "usr_1"));
    }
}
