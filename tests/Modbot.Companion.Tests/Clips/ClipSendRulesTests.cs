using Modbot.Companion.Clips;

namespace Modbot.Companion.Tests.Clips;

/// <summary>
/// When to look for a clip, which recording and which part of it, and what to send once the cut has
/// been tried. All pure: no clock, no files.
/// </summary>
public class ClipSendRulesTests
{
    private static readonly DateTimeOffset Action = new(2026, 10, 24, 20, 0, 0, TimeSpan.Zero);

    private const long Bps = 1_628_000 + 192_000;
    private const long Max = 100L * 1024 * 1024;

    private static RecordingSpan Span(string path, TimeSpan startsBefore, TimeSpan length) =>
        new(path, Action - startsBefore, length);

    [Fact]
    public void TheWaitEndsThirtyTwoSecondsAfterTheAction()
    {
        var until = ClipSendRules.WaitUntil(Action, Action + TimeSpan.FromMinutes(10));

        Assert.Equal(Action + TimeSpan.FromSeconds(32), until);
    }

    [Fact]
    public void TheWaitIsCutShortSoThereIsTwoMinutesLeftToSend()
    {
        var until = ClipSendRules.WaitUntil(Action, Action + TimeSpan.FromSeconds(100));

        Assert.Equal(Action - TimeSpan.FromSeconds(20), until);
    }

    [Fact]
    public void ARequestThatCameLateIsAlreadyPastItsWait()
    {
        var until = ClipSendRules.WaitUntil(Action, Action + TimeSpan.FromMinutes(1));

        Assert.True(until < Action);
    }

    [Fact]
    public void NoRecordingMeansNoPlan()
    {
        Assert.Null(ClipSendRules.Plan(Action, [], Bps, Max));
    }

    [Fact]
    public void ARecordingThatEndedBeforeTheActionIsNotUsed()
    {
        var before = Span("a", TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(4));

        Assert.Null(ClipSendRules.Plan(Action, [before], Bps, Max));
    }

    [Fact]
    public void ARecordingThatBeganAfterTheActionIsNotUsed()
    {
        var after = Span("a", -TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(4));

        Assert.Null(ClipSendRules.Plan(Action, [after], Bps, Max));
    }

    [Fact]
    public void ARecordingThatBeganExactlyAtTheActionHoldsIt()
    {
        var exact = Span("a", TimeSpan.Zero, TimeSpan.FromMinutes(1));

        var plan = ClipSendRules.Plan(Action, [exact], Bps, Max);

        Assert.NotNull(plan);
        Assert.Equal(TimeSpan.Zero, plan.ActionAt);
    }

    [Fact]
    public void ARecordingThatEndedExactlyAtTheActionDoesNotHoldIt()
    {
        var ends = Span("a", TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));

        Assert.Null(ClipSendRules.Plan(Action, [ends], Bps, Max));
    }

    [Fact]
    public void ARecordingThatReachesThirtySecondsPastIsPreferredOverOneThatDoesNot()
    {
        var short1 = Span("short", TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(10));
        var long1 = Span("long", TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1));

        var plan = ClipSendRules.Plan(Action, [short1, long1], Bps, Max);

        Assert.Equal("long", plan!.Span.Path);
    }

    [Fact]
    public void OfTwoThatReachFarEnoughTheEarlierStartWins()
    {
        var early = Span("early", TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(4));
        var late = Span("late", TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2));

        var plan = ClipSendRules.Plan(Action, [late, early], Bps, Max);

        Assert.Equal("early", plan!.Span.Path);
    }

    [Fact]
    public void WhenNoneReachesFarEnoughTheOneReachingFurthestIsUsed()
    {
        var a = Span("a", TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(5));
        var b = Span("b", TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(15));

        var plan = ClipSendRules.Plan(Action, [a, b], Bps, Max);

        Assert.Equal("b", plan!.Span.Path);
    }

    [Fact]
    public void TheClipEndsThirtySecondsAfterTheActionAndStartsAtTheRecordingsStartWhenItIsShort()
    {
        var span = Span("a", TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5));

        var plan = ClipSendRules.Plan(Action, [span], Bps, Max)!;

        Assert.Equal(TimeSpan.FromMinutes(2), plan.ActionAt);
        Assert.Equal(TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(30), plan.End);
        Assert.Equal(TimeSpan.Zero, plan.Start);
    }

    [Fact]
    public void TheEndIsHeldAtTheEndOfTheRecording()
    {
        var span = Span("a", TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(12));

        var plan = ClipSendRules.Plan(Action, [span], Bps, Max)!;

        Assert.Equal(span.Length, plan.End);
    }

    [Fact]
    public void ALongRecordingStartsOnlyAsFarBackAsNinetyPercentOfTheLimitAllows()
    {
        var span = Span("a", TimeSpan.FromHours(2), TimeSpan.FromHours(3));

        var plan = ClipSendRules.Plan(Action, [span], Bps, Max)!;

        var room = TimeSpan.FromSeconds(Max * 0.9 * 8 / Bps);
        Assert.Equal(plan.End - room, plan.Start);
        Assert.True(plan.Start > TimeSpan.Zero);
    }

    [Fact]
    public void TheLaterRetryStartsHalfwayAndNeverAfterTheAction()
    {
        var span = Span("a", TimeSpan.FromHours(2), TimeSpan.FromHours(3));
        var plan = ClipSendRules.Plan(Action, [span], Bps, Max)!;

        var later = plan.Later();

        Assert.NotNull(later);
        Assert.Equal(plan.End - ((plan.End - plan.Start) / 2), later.Start);
        Assert.True(later.Start <= plan.ActionAt);
        Assert.Equal(plan.End, later.End);
    }

    [Fact]
    public void TheLaterRetryHoldsAtTheActionWhenHalfwayWouldLoseIt()
    {
        var span = Span("a", TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(5));
        var plan = ClipSendRules.Plan(Action, [span], Bps, Max)!;

        var later = plan.Later();

        Assert.NotNull(later);
        Assert.Equal(plan.ActionAt, later.Start);
    }

    [Fact]
    public void ThereIsNoLaterRetryWhenTheClipAlreadyStartsAtTheAction()
    {
        var span = Span("a", TimeSpan.Zero, TimeSpan.FromMinutes(5));
        var plan = ClipSendRules.Plan(Action, [span], Bps, Max)!;

        Assert.Null(plan.Later());
    }

    [Theory]
    [InlineData(true, 50L, 500L, 100L, ClipNext.UploadCut)]
    [InlineData(true, 50L, 5000L, 100L, ClipNext.UploadCut)]
    [InlineData(true, 100L, 50L, 100L, ClipNext.UploadWhole)]
    [InlineData(true, 0L, 50L, 100L, ClipNext.UploadWhole)]
    [InlineData(false, 0L, 99L, 100L, ClipNext.UploadWhole)]
    [InlineData(false, 0L, 100L, 100L, ClipNext.CouldNotCut)]
    [InlineData(false, 0L, 5000L, 100L, ClipNext.CouldNotCut)]
    [InlineData(false, 0L, 0L, 100L, ClipNext.CouldNotCut)]
    public void WhatToSendAfterTheCut(bool worked, long cutBytes, long wholeBytes, long max, ClipNext expected)
    {
        Assert.Equal(expected, ClipSendRules.AfterCut(worked, cutBytes, wholeBytes, max));
    }
}
