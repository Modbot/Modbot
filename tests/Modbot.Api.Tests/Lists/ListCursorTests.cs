using Modbot.Api.Lists;

namespace Modbot.Api.Tests.Lists;

/// <summary>
/// The shape of a cursor: what survives the trip to the browser and back, and what is thrown away
/// rather than acted on.
/// </summary>
/// <remarks>
/// No database. A cursor is text and a rule about text, and the rule is the part that decides
/// whether a stale bookmark shows a list or an error page.
/// </remarks>
public class ListCursorTests
{
    [Fact]
    public void ACursorReadsBackAsTheSameThing()
    {
        var written = new ListCursor(ListDirection.Next, "joined", "2026-03-10T12:00:00.0000000+00:00", "usr_a")
            .ToString();

        var read = ListCursor.Read(written, "joined");

        Assert.NotNull(read);
        Assert.Equal(ListDirection.Next, read.Direction);
        Assert.Equal("joined", read.Sort);
        Assert.Equal("2026-03-10T12:00:00.0000000+00:00", read.Value);
        Assert.Equal("usr_a", read.Id);
    }

    [Fact]
    public void AnIdWithAnythingInItStillReadsBack()
    {
        // Legacy VRChat ids follow no structure (spec 3.1.1), and a display name is whatever
        // somebody typed. Neither is ever parsed -- but both have to survive being carried.
        const string awkward = "8JoV!9XEd~po.%_-+ /?&=#";

        var written = new ListCursor(ListDirection.Back, "name", "Zoë !~%?", awkward).ToString();
        var read = ListCursor.Read(written, "name");

        Assert.NotNull(read);
        Assert.Equal(ListDirection.Back, read.Direction);
        Assert.Equal("Zoë !~%?", read.Value);
        Assert.Equal(awkward, read.Id);
    }

    [Fact]
    public void ARowWithNoValueToOrderOnIsNotTheSameAsOneWithAnEmptyValue()
    {
        var missing = ListCursor.Read(new ListCursor(ListDirection.Next, "name", null, "usr_a").ToString(), "name");
        var empty = ListCursor.Read(new ListCursor(ListDirection.Next, "name", "", "usr_a").ToString(), "name");

        Assert.NotNull(missing);
        Assert.NotNull(empty);
        Assert.Null(missing.Value);
        Assert.Equal("", empty.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nonsense")]
    [InlineData("next!joined!=2026-03-10T12:00:00Z")]
    [InlineData("sideways!joined!=2026-03-10T12:00:00Z!usr_a")]
    [InlineData("next!joined!2026-03-10T12:00:00Z!usr_a")]
    [InlineData("next!joined!=2026-03-10T12:00:00Z!")]
    public void ACursorThatWillNotReadIsNoCursor(string? text)
    {
        // Null, not an exception: the endpoint serves the first page, because a bookmark from six
        // months ago should show the list rather than an error.
        Assert.Null(ListCursor.Read(text, "joined"));
    }

    [Fact]
    public void ACursorWrittenUnderAnotherOrderingIsIgnored()
    {
        var byName = new ListCursor(ListDirection.Next, "name", "Alice Wonder", "usr_a").ToString();

        // Read while the list is ordered by join date, "Alice Wonder" is not a date and the rows
        // it would pick out are arbitrary. A first page is the honest answer.
        Assert.Null(ListCursor.Read(byName, "joined"));
        Assert.NotNull(ListCursor.Read(byName, "name"));
    }

    [Fact]
    public void AMomentGoesOutAndComesBackAsTheSameMoment()
    {
        var at = new DateTimeOffset(2026, 3, 10, 12, 0, 0, 123, TimeSpan.FromHours(5));

        Assert.Equal(at, ListCursor.Time(ListCursor.Text(at)));
        Assert.Null(ListCursor.Time(null));
        Assert.Null(ListCursor.Time("Alice Wonder"));
    }
}
