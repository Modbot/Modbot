using System.Text.Json;
using Modbot.Companion.CloudBackup;
using Modbot.Companion.Ingest;
using Modbot.Companion.Instances;
using Modbot.Companion.Journal;
using Modbot.Companion.Time;
using Modbot.TestSupport;

namespace Modbot.Companion.Tests.CloudBackup;

/// <summary>
/// What the backup sends when the moderator has chosen: which kinds of event, which of their
/// details, for group instances and for the rest; and what an old Cloud is sent in place of a world
/// or instance that was left out.
/// </summary>
public sealed class CloudEventBackupChoicesTests : IDisposable
{
    private static readonly TimeZoneInfo PlusTwo =
        TimeZoneInfo.CreateCustomTimeZone("Test/PlusTwo", TimeSpan.FromHours(2), "Plus two", "Plus two");

    private readonly string _directory = Directory.CreateTempSubdirectory("modbot-cloud-choices-").FullName;
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 9, 15, 8, 0, 0, TimeSpan.Zero));
    private readonly FakeCloudClient _cloud = new() { Time = FakeCloudClient.NewCloud };
    private readonly MemoryInstallStore _installs = new();
    private readonly CountingIds _ids = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        foreach (var sent in _cloud.Sent)
            sent.Body.Dispose();

        Directory.Delete(_directory, recursive: true);
    }

    private string Outbox => Path.Combine(_directory, "cloud");

    private int BatchFiles() => Directory.Exists(Outbox) ? Directory.GetFiles(Outbox, "batch-*.json.gz").Length : 0;

    private CloudEventBackup Backup(CloudChoices? choices = null, bool enabled = true, SentJournal? journal = null) => new(new CloudBackupOptions(
        Outbox,
        _clock,
        _cloud,
        _installs,
        "2026.9.0",
        Enabled: enabled,
        TimeZone: PlusTwo,
        Backoff: new BackoffPolicy(jitter: () => 1.0),
        Ids: _ids,
        Journal: journal,
        Choices: choices));

    /// <summary>Queues observations, waits out a batch and sends it.</summary>
    private async Task<IReadOnlyList<JsonElement>> SendAsync(CloudEventBackup backup, params ObservedPresence[] observations)
    {
        backup.Offer(observations);
        await backup.PumpAsync(Ct);
        _clock.Advance(CloudOutbox.MaxBatchAge);
        await backup.PumpAsync(Ct);

        return [.. _cloud.Sent.SelectMany(s => s.Body.RootElement.GetProperty("events").EnumerateArray())];
    }

    private static ObservedPresence InGroup(PresenceKind kind, string subject = "usr_1", string? avatar = null)
    {
        Assert.True(InstanceLocation.TryParse(Observations.GroupLocation, out var instance));
        return new ObservedPresence(kind, new DateTime(2026, 9, 15, 10, 0, 0), subject, "Rin", instance, avatar);
    }

    private static ObservedPresence NotInGroup(PresenceKind kind, string subject = "usr_2", string? avatar = null)
    {
        Assert.True(InstanceLocation.TryParse(Observations.PrivateLocation, out var instance));
        return new ObservedPresence(kind, new DateTime(2026, 9, 15, 10, 0, 0), subject, "Sam", instance, avatar);
    }

    private static CloudChoices Only(CloudEventKinds group, CloudEventKinds nonGroup)
        => new(new CloudSection(group), new CloudSection(nonGroup));

    [Fact]
    public async Task OnlyTheKindsThatAreOnAreSent()
    {
        var backup = Backup(Only(CloudEventKinds.Joined, CloudEventKinds.Joined | CloudEventKinds.Left));

        var sent = await SendAsync(
            backup,
            InGroup(PresenceKind.Joined, "usr_a"),
            InGroup(PresenceKind.Left, "usr_b"),
            InGroup(PresenceKind.PresenceObserved, "usr_c"),
            NotInGroup(PresenceKind.Left, "usr_d"),
            NotInGroup(PresenceKind.AvatarChanged, "usr_e", "Cat"));

        // A group's Left and Already here are off; the other section sends Joined and Left only.
        Assert.Equal(["usr_a", "usr_d"], sent.Select(e => e.GetProperty("subjectId").GetString()));
    }

    [Fact]
    public async Task AKindThatIsOffIsNeverQueuedAndNeverMakesTheClientAskTheTime()
    {
        var backup = Backup(Only(CloudEventKinds.Joined, CloudEventKinds.Joined));

        backup.Offer([InGroup(PresenceKind.Left), NotInGroup(PresenceKind.AvatarChanged, avatar: "Cat")]);

        Assert.Equal(0, backup.Status.Queued);

        await backup.PumpAsync(Ct);

        Assert.Equal(0, _cloud.Measured);
        Assert.Empty(_cloud.Registered);
        Assert.Empty(_cloud.Sent);
        Assert.False(Directory.Exists(Outbox) && Directory.GetFiles(Outbox).Length > 0);
    }

    [Fact]
    public async Task AFileWithNoEventOnAnywhereSendsNothingAndDoesNotRegisterOrAskTheTime()
    {
        var backup = Backup(new CloudChoices(new CloudSection(CloudEventKinds.None), new CloudSection(CloudEventKinds.None)));

        backup.Offer([InGroup(PresenceKind.Joined), NotInGroup(PresenceKind.Joined)]);
        _clock.Advance(TimeSpan.FromHours(1));

        Assert.False(await backup.PumpAsync(Ct));
        Assert.Equal(0, _cloud.Measured);
        Assert.Empty(_cloud.Registered);
        Assert.Empty(_cloud.Sent);
        Assert.Equal(0, backup.Status.Queued);
    }

    [Fact]
    public async Task ADetailThatIsOffIsLeftOutOfTheEventAndTheRestIsSent()
    {
        var group = new CloudSection(CloudEventKinds.All, WorldId: false, InstanceId: false, AvatarName: false, GroupId: false);
        var backup = Backup(new CloudChoices(group, CloudSection.Default));

        var sent = await SendAsync(backup, InGroup(PresenceKind.AvatarChanged, avatar: "Cat"));

        var only = Assert.Single(sent);
        Assert.False(only.TryGetProperty("worldId", out _));
        Assert.False(only.TryGetProperty("instanceId", out _));
        Assert.False(only.TryGetProperty("groupId", out _));
        Assert.False(only.GetProperty("data").TryGetProperty("avatarName", out _));

        // What is always sent is still there.
        Assert.Equal("event-1", only.GetProperty("companionEventId").GetString());
        Assert.Equal("AvatarChanged", only.GetProperty("type").GetString());
        Assert.True(only.TryGetProperty("occurredAt", out _));
        Assert.Equal("usr_1", only.GetProperty("subjectId").GetString());
        Assert.Equal("Rin", only.GetProperty("data").GetProperty("displayName").GetString());

        var batch = Assert.Single(_cloud.Sent).Body.RootElement;
        Assert.Equal("2026.9.0", batch.GetProperty("companionVersion").GetString());
        Assert.True(batch.TryGetProperty("sentAt", out _));
        Assert.True(batch.TryGetProperty("clockOffsetMs", out _));
        Assert.True(batch.TryGetProperty("clockConfidence", out _));
    }

    [Fact]
    public async Task DetailsAreChosenSeparatelyForGroupAndNonGroupInstances()
    {
        var backup = Backup(new CloudChoices(
            new CloudSection(CloudEventKinds.All, WorldId: false),
            new CloudSection(CloudEventKinds.All, InstanceId: false)));

        var sent = await SendAsync(backup, InGroup(PresenceKind.Joined, "usr_a"), NotInGroup(PresenceKind.Joined, "usr_b"));

        var group = sent.Single(e => e.GetProperty("subjectId").GetString() == "usr_a");
        var other = sent.Single(e => e.GetProperty("subjectId").GetString() == "usr_b");

        Assert.False(group.TryGetProperty("worldId", out _));
        Assert.Equal("39911", group.GetProperty("instanceId").GetString());
        Assert.Equal("grp_cats", group.GetProperty("groupId").GetString());

        Assert.Equal("wrld_2", other.GetProperty("worldId").GetString());
        Assert.False(other.TryGetProperty("instanceId", out _));

        // An instance with no group has none to send, as it never had.
        Assert.Equal(JsonValueKind.Null, other.GetProperty("groupId").ValueKind);
    }

    [Fact]
    public async Task AnOldCloudIsSentTheWordHiddenWhereAWorldOrInstanceWasLeftOut()
    {
        // A Cloud that refuses a whole batch for one event with no world would have the batch
        // deleted by the client. It must be sent something, so it is sent "hidden".
        _cloud.Time = FakeCloudClient.OldCloud;
        var group = new CloudSection(CloudEventKinds.All, WorldId: false, InstanceId: false, AvatarName: false, GroupId: false);
        var backup = Backup(new CloudChoices(group, CloudSection.Default));

        var sent = await SendAsync(backup, InGroup(PresenceKind.AvatarChanged, avatar: "Cat"));

        var only = Assert.Single(sent);
        Assert.Equal("hidden", only.GetProperty("worldId").GetString());
        Assert.Equal("hidden", only.GetProperty("instanceId").GetString());

        // A group id and an avatar name were never required, so they are simply left out.
        Assert.False(only.TryGetProperty("groupId", out _));
        Assert.False(only.GetProperty("data").TryGetProperty("avatarName", out _));
    }

    [Fact]
    public async Task ACloudWhoseTimeCheckFailedIsSentTheWordHiddenToo()
    {
        _cloud.Time = null;
        var backup = Backup(new CloudChoices(new CloudSection(CloudEventKinds.All, WorldId: false), CloudSection.Default));

        var sent = await SendAsync(backup, InGroup(PresenceKind.Joined));

        var only = Assert.Single(sent);
        Assert.Equal("hidden", only.GetProperty("worldId").GetString());
        Assert.Equal("39911", only.GetProperty("instanceId").GetString());
    }

    [Fact]
    public async Task ACloudThatSaysItTakesThemMissingIsSentThemLeftOut()
    {
        _cloud.Time = FakeCloudClient.NewCloud;
        var backup = Backup(new CloudChoices(
            new CloudSection(CloudEventKinds.All, WorldId: false, InstanceId: false), CloudSection.Default));

        var sent = await SendAsync(backup, InGroup(PresenceKind.Joined));

        var only = Assert.Single(sent);
        Assert.False(only.TryGetProperty("worldId", out _));
        Assert.False(only.TryGetProperty("instanceId", out _));
        Assert.DoesNotContain("hidden", only.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheWordHiddenIsNeverUsedForAWorldThatIsSent()
    {
        _cloud.Time = FakeCloudClient.OldCloud;
        var backup = Backup();

        var sent = await SendAsync(backup, InGroup(PresenceKind.Joined));

        var only = Assert.Single(sent);
        Assert.Equal("wrld_1", only.GetProperty("worldId").GetString());
        Assert.Equal("39911", only.GetProperty("instanceId").GetString());
        Assert.DoesNotContain("hidden", only.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhatCloudSaidIsReadAgainOnTheNextTimeCheck()
    {
        _cloud.Time = FakeCloudClient.OldCloud;
        var choices = new CloudChoices(new CloudSection(CloudEventKinds.All, WorldId: false), CloudSection.Default);
        var backup = Backup(choices);

        await SendAsync(backup, InGroup(PresenceKind.Joined, "usr_old"));

        // Cloud is updated. Two hours on the client asks again and stops using the word.
        _cloud.Time = FakeCloudClient.NewCloud;
        _clock.Advance(CloudEventBackup.ClockCheckInterval);
        await SendAsync(backup, InGroup(PresenceKind.Joined, "usr_new"));

        var events = _cloud.Sent.SelectMany(s => s.Body.RootElement.GetProperty("events").EnumerateArray()).ToList();
        Assert.Equal("hidden", events[0].GetProperty("worldId").GetString());
        Assert.False(events[1].TryGetProperty("worldId", out _));
    }

    [Fact]
    public async Task AnOmittedDetailIsNeverWrittenToTheOutboxEither()
    {
        // The server's copy of the event still has it; the backup's does not. Nothing the moderator
        // held back sits in a file on this PC waiting to be sent.
        _cloud.Registration = new CloudRegistration(IngestOutcome.NetworkFailure);
        var backup = Backup(new CloudChoices(
            new CloudSection(CloudEventKinds.All, WorldId: false, InstanceId: false), CloudSection.Default));

        backup.Offer([InGroup(PresenceKind.Joined)]);
        await backup.PumpAsync(Ct);

        var open = File.ReadAllText(Path.Combine(Outbox, "open.jsonl"));
        Assert.DoesNotContain("wrld_1", open, StringComparison.Ordinal);
        Assert.DoesNotContain("39911", open, StringComparison.Ordinal);
        Assert.Contains("grp_cats", open, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChoicesChangedWhileRunningApplyFromTheNextEvent()
    {
        var backup = Backup();
        backup.Offer([InGroup(PresenceKind.Joined, "usr_before")]);

        backup.SetChoices(Only(CloudEventKinds.Left, CloudEventKinds.Left));
        backup.Offer([InGroup(PresenceKind.Joined, "usr_after_off"), InGroup(PresenceKind.Left, "usr_after_on")]);

        await backup.PumpAsync(Ct);
        _clock.Advance(CloudOutbox.MaxBatchAge);
        await backup.PumpAsync(Ct);

        // The first was queued but moved to disk after the change, so the new choices governed it.
        Assert.Equal(
            ["usr_after_on"],
            _cloud.Sent.SelectMany(s => s.Body.RootElement.GetProperty("events").EnumerateArray())
                .Select(e => e.GetProperty("subjectId").GetString()));
    }

    [Fact]
    public async Task SwitchingItOffWhileRunningSweepsEverythingQueuedAndSwitchingItOnStartsClean()
    {
        _cloud.Registration = new CloudRegistration(IngestOutcome.NetworkFailure);
        var backup = Backup();

        backup.Offer([InGroup(PresenceKind.Joined, "usr_queued")]);
        await backup.PumpAsync(Ct);
        _clock.Advance(CloudOutbox.MaxBatchAge);
        await backup.PumpAsync(Ct);
        backup.Offer([InGroup(PresenceKind.Joined, "usr_in_memory")]);
        Assert.True(backup.Status.Queued > 0);

        backup.SetEnabled(false);

        Assert.False(backup.Enabled);
        Assert.Equal(CloudBackupState.Off, backup.Status.State);
        Assert.Equal(0, backup.Status.Queued);
        Assert.Equal(0, BatchFiles());
        Assert.Empty(backup.NextBatch());

        // Nothing seen while it is off is ever sent.
        backup.Offer([InGroup(PresenceKind.Joined, "usr_while_off")]);
        Assert.Equal(0, backup.Status.Queued);
        Assert.False(await backup.PumpAsync(Ct));

        _cloud.Registration = new CloudRegistration(IngestOutcome.Accepted, Guid.NewGuid(), "the-secret");
        backup.SetEnabled(true);

        Assert.True(backup.Enabled);
        Assert.Equal(0, backup.Status.Queued);

        var sent = await SendAsync(backup, InGroup(PresenceKind.Joined, "usr_after"));
        Assert.Equal(["usr_after"], sent.Select(e => e.GetProperty("subjectId").GetString()));
    }

    [Fact]
    public async Task ABackupStartedOffCanBeSwitchedOnWithoutARestart()
    {
        var backup = Backup(enabled: false);
        Assert.False(Directory.Exists(Outbox));

        backup.SetEnabled(true);

        var sent = await SendAsync(backup, NotInGroup(PresenceKind.Joined));
        Assert.Single(sent);
    }

    [Fact]
    public async Task TheNextBatchListsWhatWillBeSentNewestFirst()
    {
        _cloud.Registration = new CloudRegistration(IngestOutcome.NetworkFailure);
        var backup = Backup(new CloudChoices(
            new CloudSection(CloudEventKinds.All, WorldId: false, GroupId: false), CloudSection.Default));

        backup.Offer([
            InGroup(PresenceKind.Joined, "usr_1"),
            NotInGroup(PresenceKind.AvatarChanged, "usr_2", "Cat"),
            InGroup(PresenceKind.Left, "usr_3"),
        ]);
        await backup.PumpAsync(Ct);

        var next = backup.NextBatch();

        Assert.Equal(["usr_3", "usr_2", "usr_1"], next.Select(e => e.SubjectId));

        var group = next[0];
        Assert.True(group.InGroup);
        Assert.Equal(CloudEventKinds.Left, group.Kind);
        Assert.Null(group.WorldId);
        Assert.Equal("39911", group.InstanceId);
        Assert.Null(group.GroupId);
        Assert.Equal("Rin", group.DisplayName);

        var other = next[1];
        Assert.False(other.InGroup);
        Assert.Equal(CloudEventKinds.AvatarChanged, other.Kind);
        Assert.Equal("wrld_2", other.WorldId);
        Assert.Equal("Cat", other.AvatarName);
    }

    [Fact]
    public async Task TheNextBatchIsAtMostWhatWasAskedForAndReachesIntoClosedBatches()
    {
        _cloud.Registration = new CloudRegistration(IngestOutcome.NetworkFailure);
        var backup = Backup();

        backup.Offer([InGroup(PresenceKind.Joined, "usr_1"), InGroup(PresenceKind.Joined, "usr_2")]);
        await backup.PumpAsync(Ct);
        _clock.Advance(CloudOutbox.MaxBatchAge);
        await backup.PumpAsync(Ct);

        backup.Offer([InGroup(PresenceKind.Joined, "usr_3")]);
        await backup.PumpAsync(Ct);

        Assert.Equal(["usr_3", "usr_2", "usr_1"], backup.NextBatch(5).Select(e => e.SubjectId));
        Assert.Equal(["usr_3", "usr_2"], backup.NextBatch(2).Select(e => e.SubjectId));
        Assert.Empty(backup.NextBatch(0));
    }

    [Fact]
    public void TheNextBatchIsEmptyWhenNothingIsWaiting()
    {
        Assert.Empty(Backup().NextBatch());
        Assert.Empty(Backup(enabled: false).NextBatch());
    }

    [Fact]
    public async Task AnEventWithAWorldLeftOutIsStillRecordedAsTakenOnTheEventsScreen()
    {
        var journal = new SentJournal(Path.Combine(_directory, "sent.jsonl"), _clock);
        var backup = Backup(new CloudChoices(new CloudSection(CloudEventKinds.All, WorldId: false, InstanceId: false), CloudSection.Default), journal: journal);

        await SendAsync(backup, InGroup(PresenceKind.Joined));

        var row = Assert.Single(journal.Events());
        Assert.Equal(JournalEntryKind.Sent, row.CloudState);
    }

    [Fact]
    public async Task AnAvatarNameThatIsOffIsWrittenNowhereOnThisPcNotEvenToTheJournal()
    {
        // The journal's line describes the event in words, avatar name included. A detail the
        // moderator held back must not be in sent.jsonl either, whether the event is still queued
        // or has been taken.
        var path = Path.Combine(_directory, "sent.jsonl");
        var journal = new SentJournal(path, _clock);
        _cloud.Registration = new CloudRegistration(IngestOutcome.NetworkFailure);
        var backup = Backup(new CloudChoices(new CloudSection(CloudEventKinds.All, AvatarName: false), CloudSection.Default), journal: journal);

        backup.Offer([InGroup(PresenceKind.AvatarChanged, avatar: "Secret Cat")]);
        await backup.PumpAsync(Ct);

        var queued = Assert.Single(journal.Events());
        Assert.Equal(JournalEntryKind.Waiting, queued.CloudState);
        Assert.DoesNotContain("Secret Cat", queued.Summary, StringComparison.Ordinal);

        _cloud.Registration = new CloudRegistration(IngestOutcome.Accepted, Guid.NewGuid(), "the-secret");
        _clock.Advance(CloudOutbox.MaxBatchAge);
        await backup.PumpAsync(Ct);
        _clock.Advance(TimeSpan.FromMinutes(10));
        await backup.PumpAsync(Ct);

        Assert.DoesNotContain("Secret Cat", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.DoesNotContain("Secret Cat", Assert.Single(journal.Events()).Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAvatarNameThatIsOnIsKeptInTheJournalRow()
    {
        var journal = new SentJournal(Path.Combine(_directory, "sent.jsonl"), _clock);
        _cloud.Registration = new CloudRegistration(IngestOutcome.NetworkFailure);
        var backup = Backup(journal: journal);

        backup.Offer([InGroup(PresenceKind.AvatarChanged, avatar: "Tall Cat")]);
        await backup.PumpAsync(Ct);

        var row = Assert.Single(journal.Events());
        Assert.Contains("Tall Cat", row.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEventThatLeftOutItsWorldCanStillBeReadBack()
    {
        var left = new CompanionEvent
        {
            CompanionEventId = "e",
            Type = CompanionEventType.InstanceJoined,
            OccurredAt = new DateTimeOffset(2026, 9, 15, 8, 0, 0, TimeSpan.Zero),
            SubjectId = "usr_1",
            WorldId = "wrld_1",
            InstanceId = "1",
            GroupId = "grp_1",
        };

        var shaped = CloudWire.Shape(left, new CloudSection(WorldId: false, InstanceId: false, GroupId: false), inGroup: true);
        var read = CloudWire.ReadBack(shaped);

        Assert.NotNull(read);
        Assert.Equal("usr_1", read.SubjectId);
        Assert.Equal(string.Empty, read.WorldId);
        Assert.Null(read.GroupId);
        Assert.Null(CloudWire.ReadBack("not json"));
    }

    [Fact]
    public void TheWordHiddenFillsOnlyWhatIsMissingOrBlank()
    {
        var filled = JsonDocument.Parse(CloudWire.FillMissing("""{"worldId":" ","instanceId":"7","subjectId":"u"}""")).RootElement;

        Assert.Equal("hidden", filled.GetProperty("worldId").GetString());
        Assert.Equal("7", filled.GetProperty("instanceId").GetString());

        const string whole = """{"worldId":"w","instanceId":"7"}""";
        Assert.Equal(whole, CloudWire.FillMissing(whole));
        Assert.Equal("[1]", CloudWire.FillMissing("[1]"));
    }
}
