using Modbot.Api.Auth;
using Modbot.Core.Data.Entities;

namespace Modbot.Api.Tests.Auth;

public class PermissionCatalogTests
{
    /// <summary>
    /// A permission the catalogue does not describe is one no role can be given and no page can
    /// gate on: <c>NamesOf</c> never emits it and <c>Parse</c> refuses it. The enum is where new
    /// flags get added, so this is the test that notices when the catalogue was forgotten.
    /// </summary>
    [Fact]
    public void EveryPermissionFlagIsDescribed()
    {
        var described = PermissionCatalog.All.Select(p => (ModbotPermissions)p.Value).ToHashSet();

        var missing = Enum.GetValues<ModbotPermissions>()
            .Where(flag => flag != ModbotPermissions.None && !described.Contains(flag))
            .Select(flag => flag.ToString())
            .ToList();

        Assert.True(missing.Count == 0, $"Not in PermissionCatalog: {string.Join(", ", missing)}");
    }

    /// <summary>
    /// Nothing in Modbot checks these two permissions yet, so the Roles page must not promise
    /// what they do. When a feature starts using one, this test is the place to change.
    /// </summary>
    [Theory]
    [InlineData(nameof(ModbotPermissions.Warn))]
    [InlineData(nameof(ModbotPermissions.BulkAction))]
    public void APermissionNothingUsesYetSaysItDoesNothing(string name)
    {
        var permission = PermissionCatalog.All.Single(p => p.Name == name);

        Assert.Equal("Does nothing.", permission.Description);
    }

    [Fact]
    public void PairACompanionIsDescribedUnderModeration()
    {
        var pair = Assert.Single(PermissionCatalog.All, p => p.Name == nameof(ModbotPermissions.PairCompanion));

        Assert.Equal("Pair a companion", pair.Label);
        Assert.Equal("Moderation", pair.Group);
        Assert.Equal(ModbotPermissions.PairCompanion, PermissionCatalog.Parse(["PairCompanion"], out var error));
        Assert.Null(error);
    }

    [Fact]
    public void NamesRoundTripThroughParse()
    {
        var held = ModbotPermissions.Ban | ModbotPermissions.EditAgeVerification | ModbotPermissions.ViewProfile;

        var parsed = PermissionCatalog.Parse(PermissionCatalog.NamesOf(held), out var error);

        Assert.Null(error);
        Assert.Equal(held, parsed);
    }
}
