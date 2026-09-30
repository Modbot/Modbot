using Microsoft.AspNetCore.Http;
using Modbot.Api.Features.Settings;
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
        HttpContext http,
        SelectGroupRequest request,
        ModbotContext db,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(db);

        if (string.IsNullOrWhiteSpace(request.GroupId))
            return Results.BadRequest(new { error = "Choose a group." });

        var settings = await db.GetSettingsAsync(ct);

        var groupId = request.GroupId.Trim();
        var groupName = string.IsNullOrWhiteSpace(request.Name) ? groupId : request.Name.Trim();

        // Pointing a running deployment at a different group changes what every sync reads, so a
        // change is recorded like any settings save.
        var change = new SettingsChange("managedGroup")
            .Field("groupId", settings.ManagedGroupId, groupId)
            .Field("groupName", settings.ManagedGroupName, groupName);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        settings.ManagedGroupId = groupId;
        settings.ManagedGroupName = groupName;

        await db.SaveChangesAsync(ct);
        await change.RecordAfterSetupAsync(http, ct);
        await transaction.CommitAsync(ct);

        return Results.Ok(new SelectGroupResponse(settings.ManagedGroupId, settings.ManagedGroupName!));
    }
}
