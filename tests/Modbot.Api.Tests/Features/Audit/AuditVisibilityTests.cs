using Modbot.Api.Features.Audit;
using Modbot.Core.Data.Entities;

namespace Modbot.Api.Tests.Features.Audit;

/// <summary>
/// The permission split of spec 5.9.4, tested without a database.
/// </summary>
/// <remarks>
/// These are the assertions worth having as pure tests: the decision is a table and a set
/// intersection, and every way it can go wrong — a new fact type nobody classified, a request
/// that widens rather than narrows — is visible without an HTTP round trip.
/// </remarks>
public class AuditVisibilityTests
{
    [Fact]
    public void EveryFactType_IsClassified()
    {
        // A type added without a line in the table is silently treated as operational. That is
        // the safe direction, and it is still a mistake -- so it fails here rather than becoming
        // an invisible fact type nobody notices for a year.
        var unclassified = FactType.All
            .Where(t => !AuditVisibility.VisibleTypes(ModbotPermissions.ViewAuditLog
                    | ModbotPermissions.ViewOperationalLog)
                .Contains(t))
            .ToList();

        Assert.Empty(unclassified);
    }

    [Fact]
    public void ViewAuditLog_DoesNotReachSettingsChanges()
    {
        var visible = AuditVisibility.VisibleTypes(ModbotPermissions.ViewAuditLog);

        Assert.Contains(FactType.MemberBanned, visible);
        Assert.DoesNotContain(FactType.SettingsChanged, visible);
        Assert.DoesNotContain(FactType.ApiKeyCreated, visible);
        Assert.DoesNotContain(FactType.LoginFailed, visible);
    }

    [Fact]
    public void ViewOperationalLog_DoesNotReachBans()
    {
        var visible = AuditVisibility.VisibleTypes(ModbotPermissions.ViewOperationalLog);

        Assert.Contains(FactType.SettingsChanged, visible);
        Assert.DoesNotContain(FactType.MemberBanned, visible);
        Assert.DoesNotContain(FactType.MemberKicked, visible);
    }

    [Fact]
    public void Administrator_SeesBoth()
    {
        var visible = AuditVisibility.VisibleTypes(ModbotPermissions.Administrator);

        Assert.Equal(FactType.All.Count, visible.Count);
    }

    [Fact]
    public void NeitherPermission_SeesNothing()
    {
        Assert.Empty(AuditVisibility.VisibleTypes(ModbotPermissions.ViewMembers));
    }

    [Fact]
    public void AskingForATypeYouCannotSee_NarrowsRatherThanWidens()
    {
        var resolved = AuditVisibility.Resolve(
            ModbotPermissions.ViewAuditLog,
            [FactType.MemberBanned, FactType.SettingsChanged]);

        Assert.Equal([FactType.MemberBanned], resolved);
    }

    [Fact]
    public void AskingForNothing_MeansEverythingPermitted_NotEverything()
    {
        var resolved = AuditVisibility.Resolve(ModbotPermissions.ViewAuditLog, null);

        Assert.Contains(FactType.MemberBanned, resolved);
        Assert.DoesNotContain(FactType.SettingsChanged, resolved);
    }

    [Fact]
    public void AskingOnlyForTypesYouCannotSee_ResolvesToNothing()
    {
        // Deliberately not a 403. The caller holds a real permission and asked a question that
        // happens to have no answer for them; refusing would conflate "you may not read this
        // log" with "that filter is empty".
        var resolved = AuditVisibility.Resolve(
            ModbotPermissions.ViewAuditLog,
            [FactType.SettingsChanged]);

        Assert.Empty(resolved);
    }

    [Theory]
    [InlineData(FactType.MemberBanned, AuditCategory.Moderation)]
    [InlineData(FactType.InstanceJoined, AuditCategory.Moderation)]
    [InlineData(FactType.DiscordRoleGranted, AuditCategory.Moderation)]
    [InlineData(FactType.SettingsChanged, AuditCategory.Operational)]
    [InlineData(FactType.RateLimitColdStop, AuditCategory.Operational)]
    [InlineData(FactType.UserPurged, AuditCategory.Operational)]
    public void CategoryOf_IsStable(string type, AuditCategory expected)
        => Assert.Equal(expected, AuditVisibility.CategoryOf(type));
}
