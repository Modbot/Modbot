using Modbot.Core.Data.Entities;
using Modbot.VRChat.Calendar;

namespace Modbot.VRChat.Tests.Calendar;

/// <summary>
/// Calendar design §17.2: the VRChat image id as typed. A VRChat picture address gives up its id by
/// the slashes around it; only what cannot be an id at all is refused, never an id's shape
/// (foundation §3.1.1).
/// </summary>
public class CalendarVRChatPictureIdTests
{
    [Theory]
    [InlineData("https://api.vrchat.cloud/api/1/file/file_0a1b2c/1/file", "file_0a1b2c")]
    [InlineData("https://api.vrchat.cloud/api/1/image/file_0a1b2c/1/256", "file_0a1b2c")]
    [InlineData("https://api.vrchat.cloud/api/1/file/file_0a1b2c", "file_0a1b2c")]
    [InlineData("  file_0a1b2c  ", "file_0a1b2c")]
    [InlineData("an-old-id-of-no-shape", "an-old-id-of-no-shape")]
    public void APictureAddressGivesUpItsId_AnythingElseIsKeptAsTyped(string typed, string id)
    {
        Assert.Equal(id, CalendarVRChatChecks.PictureIdFrom(typed));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NothingTyped_IsNoPicture(string? typed)
    {
        Assert.Null(CalendarVRChatChecks.PictureIdFrom(typed));
    }

    [Theory]
    [InlineData("https://example.com/picture.png")]
    [InlineData("file_0a1b2c 1")]
    [InlineData("file_0a1b2c/1/file")]
    [InlineData("file_0a1b2c?x=1")]
    public void WhatCannotBeAnId_IsRefused(string typed)
    {
        Assert.Equal(CalendarVRChatChecks.NotAPictureId, CalendarVRChatChecks.PictureIdProblem(CalendarVRChatChecks.PictureIdFrom(typed)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("file_0a1b2c")]
    [InlineData("file_0a1b2c_blob")]
    [InlineData("an-old-id-of-no-shape")]
    public void AnyIdThatCouldBeOne_IsLetThrough_ForVRChatToJudge(string? id)
    {
        Assert.Null(CalendarVRChatChecks.PictureIdProblem(id));
    }

    [Fact]
    public void TheEventsOwnProblems_ComeInTheFormsOrder()
    {
        var e = new CalendarEvent { Title = " ", Description = "", VRChatImageId = "not an id" };

        Assert.Equal(
            new[] { CalendarVRChatChecks.NoTitle, CalendarVRChatChecks.NoDescription, CalendarVRChatChecks.NotAPictureId },
            CalendarVRChatChecks.FieldProblems(e));
    }

    [Fact]
    public void ThePermission_IsMissingOnlyWhenTheGroupWasReadWithoutIt()
    {
        Assert.False(CalendarVRChatChecks.LacksCalendarPermission(new Settings()));
        Assert.True(CalendarVRChatChecks.LacksCalendarPermission(new Settings { VRChatAccountPermissions = [VRChatGroupPermissions.ViewAuditLog] }));
        Assert.False(CalendarVRChatChecks.LacksCalendarPermission(new Settings { VRChatAccountPermissions = [VRChatGroupPermissions.ManageCalendar] }));
        Assert.False(CalendarVRChatChecks.LacksCalendarPermission(new Settings { VRChatAccountPermissions = [VRChatGroupPermissions.Every] }));
    }
}
