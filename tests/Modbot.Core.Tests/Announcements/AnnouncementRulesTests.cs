using Modbot.Core.Announcements;
using Modbot.Core.Data.Entities;

namespace Modbot.Core.Tests.Announcements;

public class AnnouncementRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 20, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("  Doors close\r\nin five  ", "Doors close in five")]
    [InlineData("a\tb", "a b")]
    [InlineData("a\u0000b\u0007c", "abc")]
    [InlineData("a\n\nb", "a b")]
    [InlineData(null, "")]
    public void WordsGoOutAsOnePlainLine(string? typed, string sent)
    {
        Assert.Equal(sent, AnnouncementRules.Tidy(typed));
    }

    [Fact]
    public void ATitleAndAMessageAreBothNeeded()
    {
        Assert.Equal(new[] { "Write a title.", "Write a message." }, AnnouncementRules.Problems("", ""));
        Assert.Empty(AnnouncementRules.Problems("Hi", "There"));
    }

    [Fact]
    public void TheWordsAreCappedWellUnderVRChatsOwnLimits()
    {
        var problems = AnnouncementRules.Problems(
            new string('t', VRChatAnnouncement.MaxTitleLength + 1),
            new string('m', VRChatAnnouncement.MaxMessageLength + 1));

        Assert.Equal(2, problems.Count);
        Assert.Empty(AnnouncementRules.Problems(
            new string('t', VRChatAnnouncement.MaxTitleLength),
            new string('m', VRChatAnnouncement.MaxMessageLength)));
    }

    [Fact]
    public void OneDueMoreThanAnHourAgoIsLate()
    {
        var late = new VRChatAnnouncement { State = VRChatAnnouncementStates.Scheduled, SendAt = Now.AddMinutes(-61) };
        var due = new VRChatAnnouncement { State = VRChatAnnouncementStates.Scheduled, SendAt = Now.AddMinutes(-59) };
        var later = new VRChatAnnouncement { State = VRChatAnnouncementStates.Scheduled, SendAt = Now.AddMinutes(5) };

        Assert.True(AnnouncementRules.IsLate(late, Now));
        Assert.True(AnnouncementRules.IsDue(due, Now));
        Assert.False(AnnouncementRules.IsLate(due, Now));
        Assert.False(AnnouncementRules.IsDue(later, Now));
    }

    [Fact]
    public void OnlyAScheduledOneCanBeCancelled()
    {
        Assert.Null(AnnouncementRules.CannotCancel(new VRChatAnnouncement { State = VRChatAnnouncementStates.Scheduled }));
        Assert.NotNull(AnnouncementRules.CannotCancel(new VRChatAnnouncement { State = VRChatAnnouncementStates.Sending }));
        Assert.NotNull(AnnouncementRules.CannotCancel(new VRChatAnnouncement { State = VRChatAnnouncementStates.Sent }));
    }
}
