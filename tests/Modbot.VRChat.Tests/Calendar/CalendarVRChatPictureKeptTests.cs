using System.Text.Json;
using Modbot.Core.Data.Entities;
using Modbot.VRChat.Calendar;
using Modbot.VRChat.Tests.Fakes;
using CalendarEvent = Modbot.Core.Data.Entities.CalendarEvent;

namespace Modbot.VRChat.Tests.Calendar;

/// <summary>
/// The picture of an event made on VRChat, 2026-10-19: VRChat refused the update of "18+ Hangout"
/// with "Couldn't find the image you wanted to use for your calendar entry" because Modbot sent back
/// the picture VRChat itself had given it. An event whose picture nobody changed is written without
/// one, as it is without Featured.
/// </summary>
public class CalendarVRChatPictureKeptTests
{
    private const string Picture = "file_5c3db95e-0000-4000-8000-000000000001";

    private static CalendarEvent Imported(string? vrchatSays, string? picture) => new()
    {
        Id = Guid.CreateVersion7(),
        Title = "18+ Hangout",
        Description = "Chat",
        StartsAt = new DateTimeOffset(2026, 10, 9, 19, 0, 0, TimeSpan.Zero),
        EndsAt = new DateTimeOffset(2026, 10, 9, 21, 0, 0, TimeSpan.Zero),
        TimeZone = "UTC",
        State = CalendarEventStates.Scheduled,
        MadeOnVRChat = true,
        VRChatPictureId = vrchatSays,
        VRChatImageId = picture,
    };

    [Fact]
    public void ThePictureIsCopiedAsVRChatHasIt()
    {
        var e = new CalendarEvent { MadeOnVRChat = true };
        var source = FakeCalendar.Made("cal_one", "Movie", new DateTimeOffset(2026, 7, 26, 1, 0, 0, TimeSpan.Zero), TimeSpan.FromHours(2), new DateTimeOffset(2026, 7, 26, 1, 0, 0, TimeSpan.Zero));
        source.ImageId = Picture;

        CalendarVRChatCopy.Onto(e, source);

        Assert.Equal(Picture, e.VRChatImageId);
        Assert.Equal(Picture, e.VRChatPictureId);
    }

    [Theory]
    [InlineData(Picture, Picture, null)]
    [InlineData(null, null, null)]
    [InlineData(Picture, "file_other", "file_other")]
    [InlineData(Picture, null, null)]
    [InlineData(null, Picture, Picture)]
    public void AnEventMadeOnVRChatSendsItsPictureOnlyWhenAModeratorChangedIt(string? vrchatSays, string? picture, string? sent)
    {
        Assert.Equal(sent, CalendarVRChatRequests.ImageIdToSend(Imported(vrchatSays, picture)));
    }

    [Fact]
    public void AnEventMadeInModbotKeepsSendingItsPicture()
    {
        var e = new CalendarEvent { Id = Guid.CreateVersion7(), Title = "Movie night", VRChatImageId = Picture };

        Assert.Equal(Picture, CalendarVRChatRequests.ImageIdToSend(e));
        Assert.Equal(Picture, CalendarVRChatRequests.Create(e).ImageId);

        using var update = JsonDocument.Parse(CalendarVRChatRequests.Update(e).ToJson());
        Assert.Equal(Picture, update.RootElement.GetProperty("imageId").GetString());
    }

    [Fact]
    public void AnImportedEventsUpdatesCarryNoPictureUntilItIsChanged()
    {
        var unchanged = Imported(Picture, Picture);
        var change = new CalendarDateChange
        {
            Id = Guid.CreateVersion7(),
            EventId = unchanged.Id,
            PlannedStartsAt = unchanged.StartsAt.AddDays(7),
            Title = "Week two",
        };

        using var update = JsonDocument.Parse(CalendarVRChatRequests.Update(unchanged).ToJson());
        Assert.False(update.RootElement.TryGetProperty("imageId", out _));

        using var date = JsonDocument.Parse(CalendarVRChatRequests.UpdateDate(unchanged, change).ToJson());
        Assert.False(date.RootElement.TryGetProperty("imageId", out _));

        // A create of such an event leaves VRChat's own picture out too (2026-10-19).
        Assert.Null(CalendarVRChatRequests.Create(unchanged).ImageId);

        // A moderator changing the picture is a change, and is sent, in a create, an update and a one-date one.
        var changed = Imported(Picture, "file_other");
        Assert.Equal("file_other", CalendarVRChatRequests.Create(changed).ImageId);
        using var sent = JsonDocument.Parse(CalendarVRChatRequests.Update(changed).ToJson());
        Assert.Equal("file_other", sent.RootElement.GetProperty("imageId").GetString());

        using var sentDate = JsonDocument.Parse(CalendarVRChatRequests.UpdateDate(changed, change).ToJson());
        Assert.Equal("file_other", sentDate.RootElement.GetProperty("imageId").GetString());
    }
}
