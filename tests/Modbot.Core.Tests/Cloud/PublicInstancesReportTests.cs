using Microsoft.EntityFrameworkCore;
using Modbot.Core.Cloud;
using Modbot.Core.Configuration;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Tests.Data;
using Modbot.TestSupport;

namespace Modbot.Core.Tests.Cloud;

/// <summary>
/// What goes in the public instances report, and what must never go in it.
/// </summary>
/// <remarks>
/// The rules here are the ones the privacy policy states: only instances anyone can join, never an instance
/// limited to members or to members and their friends, nothing at all when Modbot Cloud is turned
/// off or the setting is off, and no head count anywhere.
/// </remarks>
[Collection(nameof(PostgresCollection))]
public class PublicInstancesReportTests
{
    private static readonly DateTimeOffset Evening = new(2026, 9, 16, 21, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _db;

    public PublicInstancesReportTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Location(string world, string number, string access, string group) =>
        $"{world}:{number}~group({group})~groupAccessType({access})~region(us)";

    /// <summary>A group with one instance of each access type open, plus a closed public one.</summary>
    private async Task<(string GroupId, ModbotContext Context)> SeedAsync(
        bool shared = true,
        string? groupName = "Night Owls")
    {
        var group = $"grp_{Guid.NewGuid()}";
        var context = _db.NewContext();

        var settings = await context.GetSettingsAsync(Ct);
        settings.ManagedGroupId = group;
        settings.ManagedGroupName = groupName;
        settings.ManagedGroupIconUrl = "https://pictures.test/icon.png";
        settings.ManagedGroupBannerUrl = "https://pictures.test/banner.png";
        settings.SharePublicInstances = shared;

        var world = $"wrld_{Guid.NewGuid()}";

        context.VRChatWorlds.Add(new VRChatWorld
        {
            WorldId = world,
            Name = "The Great Pug",
            ThumbnailImageUrl = "https://pictures.test/world.png",
            FirstSeenAt = Evening,
            LastSeenAt = Evening,
        });

        foreach (var access in new[] { "public", "plus", "members" })
        {
            context.VRChatInstances.Add(new VRChatInstance
            {
                Id = Guid.NewGuid(),
                Location = Location(world, access, access, group),
                WorldId = world,
                GroupId = group,
                GroupAccessType = access,
                Region = "us",
                OpenedAt = Evening,
                LastSeenAt = Evening,
                SeenInGroupList = true,
                HeadCount = 27,
            });
        }

        // A public instance that has already closed, and a public instance belonging to somebody else.
        context.VRChatInstances.Add(new VRChatInstance
        {
            Id = Guid.NewGuid(),
            Location = Location(world, "closed", "public", group),
            WorldId = world,
            GroupId = group,
            GroupAccessType = "public",
            OpenedAt = Evening.AddHours(-3),
            LastSeenAt = Evening.AddHours(-1),
            ClosedAt = Evening.AddHours(-1),
            ClosedBy = "list",
            SeenInGroupList = true,
        });

        context.VRChatInstances.Add(new VRChatInstance
        {
            Id = Guid.NewGuid(),
            Location = Location(world, "other", "public", "grp_somebody_else"),
            WorldId = world,
            GroupId = "grp_somebody_else",
            GroupAccessType = "public",
            OpenedAt = Evening,
            LastSeenAt = Evening,
        });

        await context.SaveChangesAsync(Ct);

        return (group, context);
    }

    private static PublicInstancesReportBuilder Builder(ModbotContext context, bool cloudDisabled = false) =>
        new(context, new ModbotCloudAddress(ModbotCloudAddress.DefaultEndpoint, cloudDisabled));

    [Fact]
    public async Task TheSettingIsOnForANewModbot()
    {
        await using var context = _db.NewContext();

        var settings = await context.GetSettingsAsync(Ct);

        Assert.True(settings.SharePublicInstances);
    }

    [Fact]
    public async Task OnlyInstancesAnyoneCanJoinAreReported()
    {
        var (group, context) = await SeedAsync();
        await using var _ = context;

        var report = await Builder(context).BuildAsync(Ct);

        Assert.NotNull(report);
        Assert.Equal(group, report.GroupId);
        var instance = Assert.Single(report.Instances);
        Assert.Contains("groupAccessType(public)", instance.Location, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMembersOnlyInstanceIsNeverReported()
    {
        var (_, context) = await SeedAsync();
        await using var _ = context;

        var report = await Builder(context).BuildAsync(Ct);

        Assert.NotNull(report);
        Assert.DoesNotContain(report.Instances, r => r.Location.Contains("groupAccessType(members)", StringComparison.Ordinal));
        Assert.DoesNotContain(report.Instances, r => r.Location.Contains("groupAccessType(plus)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnInstanceThatHasClosedIsNotReported()
    {
        var (_, context) = await SeedAsync();
        await using var _ = context;

        var report = await Builder(context).BuildAsync(Ct);

        Assert.NotNull(report);
        Assert.DoesNotContain(report.Instances, r => r.Location.Contains(":closed~", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnInstanceThatClosesTakesItselfOutOfTheNextReport()
    {
        var (group, context) = await SeedAsync();
        await using var _ = context;

        var before = await Builder(context).BuildAsync(Ct);
        Assert.NotNull(before);
        Assert.Single(before.Instances);

        var open = await context.VRChatInstances
            .SingleAsync(i => i.GroupId == group && i.GroupAccessType == "public" && i.ClosedAt == null, Ct);

        open.ClosedAt = Evening.AddMinutes(30);
        open.ClosedBy = "list";
        await context.SaveChangesAsync(Ct);

        var after = await Builder(context).BuildAsync(Ct);

        Assert.NotNull(after);
        Assert.Empty(after.Instances);
    }

    [Fact]
    public async Task AnotherGroupsInstanceIsNotReported()
    {
        var (_, context) = await SeedAsync();
        await using var _ = context;

        var report = await Builder(context).BuildAsync(Ct);

        Assert.NotNull(report);
        Assert.DoesNotContain(report.Instances, r => r.Location.Contains("grp_somebody_else", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NothingIsBuiltWhenTheSettingIsOff()
    {
        var (_, context) = await SeedAsync(shared: false);
        await using var _ = context;

        Assert.Null(await Builder(context).BuildAsync(Ct));
    }

    [Fact]
    public async Task NothingIsBuiltWhenCloudIsDisabled()
    {
        var (_, context) = await SeedAsync();
        await using var _ = context;

        Assert.Null(await Builder(context, cloudDisabled: true).BuildAsync(Ct));
    }

    [Fact]
    public async Task NothingIsBuiltWithoutAManagedGroup()
    {
        await using var context = _db.NewContext();

        var settings = await context.GetSettingsAsync(Ct);
        settings.ManagedGroupId = null;
        await context.SaveChangesAsync(Ct);

        Assert.Null(await Builder(context).BuildAsync(Ct));
    }

    [Fact]
    public async Task TheReportCarriesTheWorldTheLinkAndTheGroupsPictures()
    {
        var (_, context) = await SeedAsync();
        await using var _ = context;

        var report = await Builder(context).BuildAsync(Ct);

        Assert.NotNull(report);
        Assert.Equal("Night Owls", report.GroupName);
        Assert.Equal("https://pictures.test/icon.png", report.GroupIconUrl);
        Assert.Equal("https://pictures.test/banner.png", report.GroupBannerUrl);

        var instance = Assert.Single(report.Instances);
        Assert.Equal("The Great Pug", instance.WorldName);
        Assert.Equal("https://pictures.test/world.png", instance.WorldImageUrl);
        Assert.Equal("us", instance.Region);
        Assert.Equal(Evening, instance.OpenedAt);
        Assert.StartsWith("https://vrchat.com/home/launch?worldId=", instance.JoinLink, StringComparison.Ordinal);
    }

    /// <summary>
    /// The report has no field for a head count, so nothing serialised from it can carry one. The
    /// instances in the fixture each have 27 people in them, and the report is checked field by field
    /// rather than by searching the text, so a group id that happens to contain "27" proves nothing.
    /// </summary>
    [Fact]
    public async Task NobodyIsCounted()
    {
        var (_, context) = await SeedAsync();
        await using var _ = context;

        var report = await Builder(context).BuildAsync(Ct);
        using var json = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(report));

        var top = json.RootElement.EnumerateObject().Select(p => p.Name).ToList();
        Assert.Equal(["groupId", "groupName", "groupIconUrl", "groupBannerUrl", "instances"], top);

        var instance = json.RootElement.GetProperty("instances").EnumerateArray().Single();
        var fields = instance.EnumerateObject().Select(p => p.Name).ToList();
        Assert.Equal(
            ["location", "worldId", "worldName", "worldImageUrl", "joinLink", "region", "openedAt"],
            fields);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("http://pictures.test/x.png", null)]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("not a url", null)]
    [InlineData("https://pictures.test/x.png", "https://pictures.test/x.png")]
    public void OnlyHttpsPicturesLeaveTheServer(string? given, string? expected) =>
        Assert.Equal(expected, PublicInstancesReportBuilder.Picture(given));
}
