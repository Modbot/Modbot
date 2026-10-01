using System.Text.Json;
using Modbot.Core.Data.Entities;
using Modbot.VRChat.Calendar;

namespace Modbot.VRChat.Tests.Calendar;

/// <summary>
/// VRChat refused every calendar update with "Can't unpublish or change access type once the
/// calendar entry is published" (seen 2026-10-01): the SDK's update model has no access type, so
/// the body carried none. Read from the body as the SDK writes it, not from the object, because
/// what is on the wire is what VRChat refused.
/// </summary>
public class CalendarUpdateBodyTests
{
    private static CalendarEvent Weekly(string visibility) => new()
    {
        Id = Guid.CreateVersion7(),
        Title = "Movie night",
        Description = "Bring snacks",
        StartsAt = new DateTimeOffset(2026, 10, 9, 19, 0, 0, TimeSpan.Zero),
        EndsAt = new DateTimeOffset(2026, 10, 9, 21, 0, 0, TimeSpan.Zero),
        TimeZone = "Europe/London",
        Repeat = CalendarRepeats.Weekly,
        RepeatDays = ["FR"],
        Visibility = visibility,
        State = CalendarEventStates.Scheduled,
    };

    [Theory]
    [InlineData("group")]
    [InlineData("public")]
    public void AnUpdateCarriesTheAccessTypeTheCreateSent_AndNothingThatUnpublishes(string visibility)
    {
        var e = Weekly(visibility);

        using var create = JsonDocument.Parse(CalendarVRChatRequests.Create(e).ToJson());
        using var update = JsonDocument.Parse(CalendarVRChatRequests.Update(e).ToJson());

        Assert.Equal(visibility, create.RootElement.GetProperty("accessType").GetString());
        Assert.Equal(
            create.RootElement.GetProperty("accessType").GetString(),
            update.RootElement.GetProperty("accessType").GetString());

        // Never a draft: absent or false.
        if (update.RootElement.TryGetProperty("isDraft", out var draft))
            Assert.Equal(JsonValueKind.False, draft.ValueKind);
    }

    [Fact]
    public void AnUpdateToOneDateCarriesTheAccessTypeToo()
    {
        var e = Weekly("public");
        var change = new CalendarDateChange
        {
            Id = Guid.CreateVersion7(),
            EventId = e.Id,
            PlannedStartsAt = e.StartsAt + TimeSpan.FromDays(7),
            StartsAt = e.StartsAt + TimeSpan.FromDays(7) + TimeSpan.FromHours(1),
            EndsAt = e.StartsAt + TimeSpan.FromDays(7) + TimeSpan.FromHours(3),
        };

        using var body = JsonDocument.Parse(CalendarVRChatRequests.UpdateDate(e, change).ToJson());

        Assert.Equal("public", body.RootElement.GetProperty("accessType").GetString());
        Assert.False(body.RootElement.TryGetProperty("recurrence", out _));
    }
}
