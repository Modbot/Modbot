using Modbot.Core.Data.Entities;
using Modbot.VRChat.Calendar;

namespace Modbot.VRChat.Tests.Calendar;

/// <summary>
/// Calendar design §17.2: the VRChat image id before it is sent. Only what cannot be an id at all is
/// refused, never an id's shape (foundation §3.1.1); taking the id out of a paste is the save's
/// (§15.1), and the two agree.
/// </summary>
public class CalendarVRChatPictureIdTests
{
    /// <summary>
    /// What the save keeps is what sending lets through (changed 2026-10-02): every id the save takes
    /// out of a paste passes the publisher's own check, so a save never accepts what is then refused.
    /// </summary>
    [Theory]
    [InlineData("https://api.vrchat.cloud/api/1/file/file_0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d/1/file")]
    [InlineData("https://api.vrchat.cloud/api/1/image/file_0a1b2c/1/256")]
    [InlineData("file_0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d_blob")]
    [InlineData("file_0a1b2c?x=1")]
    [InlineData("  file_odd-but_real.png  ")]
    public void EveryIdTheSaveKeeps_PassesTheCheckBeforeSending(string pasted)
    {
        var kept = Core.Files.VRChatFileIds.Find(pasted);

        Assert.NotNull(kept);
        Assert.Null(CalendarVRChatChecks.PictureIdProblem(kept));
    }

    [Theory]
    [InlineData("https://example.com/picture.png")]
    [InlineData("file_0a1b2c 1")]
    [InlineData("file_0a1b2c/1/file")]
    [InlineData("file_0a1b2c?x=1")]
    public void WhatCannotBeAnId_IsRefused(string id)
    {
        Assert.Equal(CalendarVRChatChecks.NotAPictureId, CalendarVRChatChecks.PictureIdProblem(id));
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
