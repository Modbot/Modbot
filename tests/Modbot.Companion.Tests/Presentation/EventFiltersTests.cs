using System.Text.Json;
using System.Text.Json.Nodes;
using Modbot.Companion.Instances;
using Modbot.Companion.Journal;
using Modbot.Companion.Presentation;

namespace Modbot.Companion.Tests.Presentation;

/// <summary>
/// The Events page's filters: rows in, rows out; chips written and read back; counts that say
/// what picking a value would leave.
/// </summary>
public class EventFiltersTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 16, 20, 0, 0, TimeSpan.Zero);

    private static readonly IReadOnlyDictionary<string, string> Groups = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["cats"] = "Cat Café",
        ["dogs"] = "Dog Park",
    };

    private static JournalRow Row(
        string summary,
        string? serverId = "cats",
        JournalEntryKind? server = JournalEntryKind.Sent,
        JournalEntryKind? cloud = JournalEntryKind.Sent,
        bool seen = false)
        => new(At, summary, serverId, server, cloud, seen);

    private static readonly JournalRow RinJoined = Row("Rin joined your world");
    private static readonly JournalRow RinLeft = Row("Rin left your world", server: JournalEntryKind.Waiting, cloud: JournalEntryKind.Waiting);
    private static readonly JournalRow KaiAlready = Row("Kai was already in your world when you arrived", serverId: "dogs", server: JournalEntryKind.Withheld);
    private static readonly JournalRow SeenOnly = Row("Mira joined your world", serverId: null, server: null, cloud: JournalEntryKind.Sent, seen: true);
    private static readonly JournalRow Paused = Row("Paused", server: null, cloud: null);

    /// <summary>The server took it and the backup then gave up on it, which the page does not show.</summary>
    private static readonly JournalRow AvatarChanged = Row("Rin switched to the avatar “Fox”", server: JournalEntryKind.Sent, cloud: JournalEntryKind.Failed);

    private static readonly IReadOnlyList<JournalRow> Rows = [RinJoined, RinLeft, KaiAlready, SeenOnly, Paused, AvatarChanged];

    private static EventFilterChip Chip(string property, EventFilterOperator @operator, params string[] values)
        => new(property, @operator, values);

    private static IReadOnlyList<JournalRow> Apply(params EventFilterChip[] chips)
        => EventFilters.Apply(Rows, new EventFilterSet(chips), Groups);

    // ── What a row is ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(PresenceKind.Joined, "joined")]
    [InlineData(PresenceKind.PresenceObserved, "already there")]
    [InlineData(PresenceKind.Left, "left")]
    [InlineData(PresenceKind.LogStopped, "log stopped")]
    [InlineData(PresenceKind.AvatarChanged, "changed avatar")]
    public void EveryKindTheJournalWritesIsReadBack(PresenceKind kind, string expected)
    {
        // The journal writes the sentence, not the kind, so the filter reads the kind out of the
        // sentence; this holds the two in step.
        var row = Row(SentJournal.Sentence(kind, "Rin", "Fox"));

        Assert.Equal([expected], EventFilters.KindsOf(row));
    }

    [Fact]
    public void AnAvatarChangeWithoutANameIsStillAnAvatarChange()
        => Assert.Equal(["changed avatar"], EventFilters.KindsOf(Row(SentJournal.Sentence(PresenceKind.AvatarChanged, "Rin"))));

    [Fact]
    public void ARowSeenInAGroupNobodyManagesIsBothWhatHappenedAndSeen()
        => Assert.Equal(["joined", "seen"], EventFilters.KindsOf(SeenOnly));

    [Fact]
    public void ANoteIsANote()
        => Assert.Equal(["note"], EventFilters.KindsOf(Paused));

    [Fact]
    public void HowFarARowGot()
    {
        // One word per row, from the paired server, and the backup only ever able to lift a
        // waiting row to sent. There is no destination to filter on any more.
        Assert.Equal(["sent"], EventFilters.StatesOf(RinJoined));

        // Neither has settled, so the row has not got anywhere yet. The backup lifts a waiting row
        // to sent only once it has actually taken it.
        Assert.Equal(["waiting"], EventFilters.StatesOf(RinLeft));
        Assert.Equal(["withheld"], EventFilters.StatesOf(KaiAlready));
        Assert.Equal(["sent"], EventFilters.StatesOf(AvatarChanged));
        Assert.Empty(EventFilters.StatesOf(SeenOnly));
        Assert.Empty(EventFilters.StatesOf(Paused));
    }

    [Fact]
    public void TheGroupIsTheServersGroupOrItsIdUntilKnown()
    {
        Assert.Equal("Cat Café", EventFilters.GroupOf(RinJoined, Groups));
        Assert.Equal("srv_new", EventFilters.GroupOf(Row("x", serverId: "srv_new"), Groups));
        Assert.Null(EventFilters.GroupOf(SeenOnly, Groups));
    }

    // ── Rows in, rows out ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void NoChipsLetsEverythingThrough()
        => Assert.Equal(Rows, Apply());

    [Fact]
    public void AKindChipIsAnyOfItsValues()
        => Assert.Equal([RinJoined, RinLeft, SeenOnly], Apply(Chip(EventFilters.Kind, EventFilterOperator.Is, "joined", "left")));

    [Fact]
    public void IsNotIsTheSameListTurnedAround()
        => Assert.Equal([KaiAlready, Paused, AvatarChanged], Apply(Chip(EventFilters.Kind, EventFilterOperator.IsNot, "joined", "left")));

    [Fact]
    public void KindIsSeenShowsOnlyRowsThatWentToNoServer()
    {
        Assert.Equal([SeenOnly], Apply(Chip(EventFilters.Kind, EventFilterOperator.Is, "seen")));
        Assert.DoesNotContain(SeenOnly, Apply(Chip(EventFilters.Kind, EventFilterOperator.IsNot, "seen")));
    }

    [Fact]
    public void ChipsCombineWithAnd()
    {
        var shown = Apply(
            Chip(EventFilters.Kind, EventFilterOperator.Is, "joined", "left", "changed avatar"),
            Chip(EventFilters.State, EventFilterOperator.Is, "failed", "waiting"));

        // The avatar change is not in it. The backup gave up on that one, and the page does not
        // call an event failed because a copy of it did not arrive somewhere.
        Assert.Equal([RinLeft], shown);
    }

    [Fact]
    public void ThereIsNoFilterForWhereAnEventWent()
    {
        // The Destination chip is gone, and so is the only place on any screen that named the
        // backup. A remembered chip from an older version is dropped rather than drawn.
        Assert.DoesNotContain("destination", EventFilters.Properties.Select(p => p.Id), StringComparer.Ordinal);
        Assert.False(EventFilters.Knows("destination"));
        Assert.Equal(["kind:is:joined"], EventFilterSet.Parse(["destination:is:cloud", "kind:is:joined"]).Encode());
    }

    [Fact]
    public void GroupFindsTheServersGroupByName()
        => Assert.Equal([KaiAlready], Apply(Chip(EventFilters.Group, EventFilterOperator.Is, "Dog Park")));

    [Fact]
    public void TextIsAWordAnywhereInTheSentenceWhateverItsCase()
    {
        Assert.Equal([RinJoined, RinLeft, AvatarChanged], Apply(Chip(EventFilters.Text, EventFilterOperator.Contains, "rin")));
        Assert.Equal([AvatarChanged], Apply(Chip(EventFilters.Text, EventFilterOperator.Contains, "FOX")));
    }

    [Fact]
    public void AChipWithNoValuesLetsEverythingThrough()
        => Assert.Equal(Rows, Apply(Chip(EventFilters.Kind, EventFilterOperator.Is)));

    // ── Counts ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void OptionsCarryHowManyRowsEachValueWouldShow()
    {
        var options = EventFilters.Options(EventFilters.Kind, Rows, Groups);

        Assert.Equal(EventFilters.Kinds, options.Select(o => o.Value));
        Assert.Equal(2, options.Single(o => o.Value == "joined").Count);
        Assert.Equal(1, options.Single(o => o.Value == "seen").Count);
        Assert.Equal(0, options.Single(o => o.Value == "log stopped").Count);
    }

    [Fact]
    public void StateOptionsAreTheFourWordsARowCanSay()
    {
        var options = EventFilters.Options(EventFilters.State, Rows, Groups);

        Assert.Equal(EventFilters.States, options.Select(o => o.Value));
        Assert.Equal(2, options.Single(o => o.Value == "sent").Count);
        Assert.Equal(1, options.Single(o => o.Value == "waiting").Count);
        Assert.Equal(1, options.Single(o => o.Value == "withheld").Count);
        Assert.Equal(0, options.Single(o => o.Value == "failed").Count);
    }

    [Fact]
    public void GroupOptionsAreTheGroupsSeenInTheRowsInNameOrder()
    {
        var options = EventFilters.Options(EventFilters.Group, Rows, Groups);

        // The note about pausing names the server too, so it counts under the server's group.
        Assert.Equal(["Cat Café", "Dog Park"], options.Select(o => o.Value));
        Assert.Equal([4, 1], options.Select(o => o.Count));
    }

    // ── Chips written and read back ───────────────────────────────────────────────────────────

    [Fact]
    public void AChipIsOneLine()
    {
        Assert.Equal("kind:is:joined,left", Chip(EventFilters.Kind, EventFilterOperator.Is, "joined", "left").Encode());
        Assert.Equal("state:is-not:failed", Chip(EventFilters.State, EventFilterOperator.IsNot, "failed").Encode());
        Assert.Equal("text:contains:rin", Chip(EventFilters.Text, EventFilterOperator.Contains, "rin").Encode());
    }

    [Fact]
    public void ACommaInsideAValueIsNeverASeparator()
    {
        var chip = Chip(EventFilters.Group, EventFilterOperator.Is, "Cats, Dogs", "Birds");

        Assert.Equal("group:is:Cats%2C%20Dogs,Birds", chip.Encode());
        Assert.Equal(chip, EventFilterChip.Decode(chip.Encode()));
        Assert.Equal(["Cats, Dogs", "Birds"], EventFilterChip.Decode(chip.Encode())!.Values);
    }

    [Theory]
    [InlineData("kind:is:joined", "kind", EventFilterOperator.Is, "joined")]
    [InlineData("kind:is:", "kind", EventFilterOperator.Is)]
    [InlineData("text:contains:a%3Ab", "text", EventFilterOperator.Contains, "a:b")]
    public void ALineReadsBackAsAChip(string line, string property, EventFilterOperator @operator, params string[] values)
    {
        var chip = EventFilterChip.Decode(line);

        Assert.NotNull(chip);
        Assert.Equal(property, chip.Property);
        Assert.Equal(@operator, chip.Operator);
        Assert.Equal(values, chip.Values);
    }

    [Theory]
    [InlineData("")]
    [InlineData("kind")]
    [InlineData("kind:is")]
    [InlineData(":is:joined")]
    [InlineData("kind:maybe:joined")]
    public void ALineThatIsNotAChipIsNothing(string line)
        => Assert.Null(EventFilterChip.Decode(line));

    [Fact]
    public void ASetKeepsOneChipPerPropertyAndSkipsBadLines()
    {
        var set = EventFilterSet.Parse(["kind:is:joined", "not a chip", "kind:is:left", "state:is:sent", null]);

        Assert.Equal(["kind:is:joined", "state:is:sent"], set.Encode());
    }

    [Fact]
    public void ReplacingAndRemovingChips()
    {
        var set = new EventFilterSet([Chip(EventFilters.Kind, EventFilterOperator.Is, "joined")]);

        var replaced = set.Replace(Chip(EventFilters.Kind, EventFilterOperator.IsNot, "left"));
        Assert.Equal(["kind:is-not:left"], replaced.Encode());

        var added = replaced.Replace(Chip(EventFilters.State, EventFilterOperator.Is, "sent"));
        Assert.Equal(["kind:is-not:left", "state:is:sent"], added.Encode());

        Assert.Equal(["state:is:sent"], added.Without(EventFilters.Kind).Encode());
        Assert.Equal(["kind:is-not:left"], added.WithoutLast().Encode());
        Assert.True(EventFilterSet.Empty.WithoutLast().IsEmpty);
    }

    [Fact]
    public void SetsCompareByWhatTheySay()
    {
        var a = EventFilterSet.Parse(["kind:is:joined,left"]);
        var b = new EventFilterSet([Chip(EventFilters.Kind, EventFilterOperator.Is, "joined", "left")]);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, EventFilterSet.Parse(["kind:is:left,joined"]));
        Assert.Equal(EventFilterSet.Empty, new EventFilterSet([]));
    }

    [Fact]
    public void TheSettingsValueIsAnArrayOfLinesOrNothing()
    {
        Assert.Null(EventFilterSet.Empty.ToJson());

        var json = EventFilterSet.Parse(["kind:is:joined", "text:contains:rin"]).ToJson();
        Assert.Equal("""["kind:is:joined","text:contains:rin"]""", json!.ToJsonString());

        var back = EventFilterSet.FromJson(JsonNode.Parse("""["kind:is:joined","text:contains:rin"]""") as JsonArray);
        Assert.Equal(["kind:is:joined", "text:contains:rin"], back.Encode());
        Assert.Equal(EventFilterSet.Empty, EventFilterSet.FromJson(null));
    }

    // ── The opened row ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheDetailListsEveryFieldTheRowShows()
    {
        var fields = EventRowDetail.Fields(RinLeft, Groups).ToDictionary(f => f.Label, f => f.Value);

        Assert.Equal("Rin left your world", fields["Summary"]);
        Assert.Equal("left", fields["Kind"]);
        Assert.Equal("Cat Café", fields["Group"]);
        Assert.Equal("cats", fields["Server"]);
        Assert.Equal("waiting", fields["State"]);
        Assert.Equal("no", fields["Seen only"]);
        Assert.Equal("no", fields["Note"]);
        Assert.Equal("2026-09-16 20:00:00 UTC", fields["Server time"]);
    }

    [Fact]
    public void ARowNoPairedServerWasGivenHasNoStateToShow()
        => Assert.Equal("—", EventRowDetail.Fields(SeenOnly, Groups).Single(f => f.Label == "State").Value);

    [Fact]
    public void TheJsonIsTheRowAsThePageHasIt()
    {
        var json = JsonDocument.Parse(EventRowDetail.ToJson(RinLeft)).RootElement;

        Assert.Equal("Rin left your world", json.GetProperty("summary").GetString());
        Assert.Equal("cats", json.GetProperty("serverId").GetString());
        Assert.Equal("waiting", json.GetProperty("state").GetString());
        Assert.False(json.GetProperty("seen").GetBoolean());
        Assert.False(json.GetProperty("isNote").GetBoolean());
    }

    // ── Nothing on the page names the backup ──────────────────────────────────────────────────

    [Fact]
    public void NothingTheEventsPagePutsOnScreenNamesTheBackup()
    {
        // Written over what the page produces rather than over the source, so it stays true however
        // the code is rearranged and does not go off on an honest comment. Every chip name, every
        // value the picker offers, every field under an opened row and the JSON box beside them,
        // over rows the backup has been every kind of busy with.
        var words = new List<string>();

        foreach (var property in EventFilters.Properties)
        {
            words.Add(property.Id);
            words.Add(property.Label);

            foreach (var option in EventFilters.Options(property.Id, Rows, Groups))
            {
                words.Add(option.Value);
                words.Add(option.Label);
            }
        }

        foreach (var row in Rows)
        {
            foreach (var (label, value) in EventRowDetail.Fields(row, Groups))
            {
                words.Add(label);
                words.Add(value);
            }

            words.Add(EventRowDetail.ToJson(row));
            words.AddRange(EventFilters.KindsOf(row));
            words.AddRange(EventFilters.StatesOf(row));
        }

        Assert.DoesNotContain(words, w => w.Contains("cloud", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(words, w => w.Contains("backup", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheWordsTheStateFilterOffersAreTheWordsARowCanSay()
    {
        // The picker lists these four whether or not any row is in that state, so they have to be
        // the whole of what JournalRow.State can produce, lower-cased the same way.
        var possible = new[]
        {
            JournalEntryKind.Sent,
            JournalEntryKind.Waiting,
            JournalEntryKind.Withheld,
            JournalEntryKind.Failed,
        };

        Assert.Equal(EventFilters.States.Order(), possible.Select(k => k.ToString().ToLowerInvariant()).Order());
    }
}
