using Microsoft.AspNetCore.Http;
using Modbot.Core.Data;

namespace Modbot.Api.Features.Onboarding.SelectGroup;

/// <param name="GroupId">
/// Stored and compared exactly as VRChat gave it. Never format-checked: legacy ids follow no
/// structure at all, and a shape check would work in testing and then silently exclude the oldest
/// groups in VRChat (spec 3.1.1).
/// </param>
/// <param name="Name">
/// The group's display name at the time of selection, kept so the sidebar has something to show
/// before the first sync completes. Cosmetic; the id is the identity.
/// </param>
public sealed record SelectGroupRequest(string GroupId, string? Name = null);

public sealed record SelectGroupResponse(string GroupId, string Name);

public static class SelectGroupHandler
{
    public static async Task<IResult> HandleAsync(
        SelectGroupRequest request,
        ModbotContext db,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(db);

        if (string.IsNullOrWhiteSpace(request.GroupId))
            return Results.BadRequest(new { error = "Choose a group." });

        var settings = await db.GetSettingsAsync(ct);

        settings.ManagedGroupId = request.GroupId.Trim();
        settings.ManagedGroupName = string.IsNullOrWhiteSpace(request.Name)
            ? settings.ManagedGroupId
            : request.Name.Trim();

        await db.SaveChangesAsync(ct);

        return Results.Ok(new SelectGroupResponse(settings.ManagedGroupId, settings.ManagedGroupName!));
    }
}
