using Modbot.Discord.Gateway;
using Modbot.Discord.Tickets;
using static Modbot.Discord.Tests.Messages.MessageTestData;

namespace Modbot.Discord.Tests.Tickets;

/// <summary>The transcript of a closed ticket: plain text, oldest first, and no private images.</summary>
public class TicketTranscriptTests
{
    private static readonly DateTimeOffset Closed = Start.AddHours(1);

    private static string Build(
        IReadOnlyList<DiscordMessageSnapshot> messages, bool privateImages = false, bool textMissing = false)
        => TicketTranscript.Build("Verify", "900", "ada", Start, "901", Closed, messages, privateImages, textMissing);

    [Fact]
    public void ATranscript_NamesTheTicket_AndWhoOpenedAndClosedIt()
    {
        var text = Build([Message(1, text: "hi there")]);

        Assert.Contains("Ticket: Verify", text, StringComparison.Ordinal);
        Assert.Contains("Opened by ada (900)", text, StringComparison.Ordinal);
        Assert.Contains("Closed by 901", text, StringComparison.Ordinal);
        Assert.Contains("Person 900 (900): hi there", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAttachment_InAnOrdinaryTicket_IsNamedAndLinked()
    {
        var text = Build([Message(1, text: "look")]);

        Assert.Contains("[attachment: cat.png] https://cdn.discordapp.com/cat.png", text, StringComparison.Ordinal);
        Assert.DoesNotContain(TicketTranscript.ImageRemoved, text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAttachment_InATicketThatHoldsPrivateImages_IsRemoved_NameAndAddressToo()
    {
        var text = Build([Message(1, text: "my ID")], privateImages: true);

        Assert.Contains(TicketTranscript.ImageRemoved, text, StringComparison.Ordinal);
        Assert.DoesNotContain("cat.png", text, StringComparison.Ordinal);
        Assert.DoesNotContain("cdn.discordapp.com", text, StringComparison.Ordinal);
        Assert.Contains("my ID", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WhenOlderMessagesWereNotRead_OneLineSaysSo()
    {
        var left = TicketTranscript.Build("Verify", "900", "ada", Start, "901", Closed, [Message(1)], false, false, olderLeftOut: true);

        Assert.Contains(TicketTranscript.OlderLeftOut, left, StringComparison.Ordinal);
        Assert.DoesNotContain(TicketTranscript.OlderLeftOut, Build([Message(1)]), StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutMessageText_OneLineSaysSo()
    {
        Assert.Contains(TicketTranscript.TextMissing, Build([Message(1, text: "")], textMissing: true), StringComparison.Ordinal);
        Assert.DoesNotContain(TicketTranscript.TextMissing, Build([Message(1)]), StringComparison.Ordinal);
    }

    [Fact]
    public void AVeryLongTicket_IsCut()
    {
        var longText = new string('x', 500_000);
        var messages = Enumerable.Range(1, 8).Select(i => Message(i, text: longText)).ToList();

        var text = Build(messages);

        Assert.True(text.Length < TicketTranscript.MostLength + 100);
        Assert.EndsWith("[the transcript was cut here]", text, StringComparison.Ordinal);
    }
}
