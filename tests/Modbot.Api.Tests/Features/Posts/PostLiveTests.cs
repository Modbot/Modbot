using Modbot.Api.Features.Live.Stream;
using Modbot.Core.Data.Entities;

namespace Modbot.Api.Tests.Features.Posts;

/// <summary>
/// The live stream for the Marketing tab (posts design §4.2): See posts is sent the posts' facts,
/// and nothing else that the operational log would hold.
/// </summary>
public class PostLiveTests
{
    [Fact]
    public void SeePostsIsSentThePostsFacts()
    {
        var scope = LiveScope.ForPerson(ModbotPermissions.ViewPosts);

        Assert.True(scope.SeesAnything);

        foreach (var type in new[]
        {
            FactType.PostCreated, FactType.PostChanged, FactType.PostCancelled, FactType.PostSent,
            FactType.PostFailed, FactType.PostEdited, FactType.PostRemoved,
        })
        {
            Assert.True(scope.CanSee(LiveKinds.Fact, type), type);
        }
    }

    [Fact]
    public void SeePostsIsSentNothingElseFromTheOperationalLog()
    {
        var scope = LiveScope.ForPerson(ModbotPermissions.ViewPosts);

        Assert.False(scope.CanSee(LiveKinds.Fact, FactType.PlannedEventCreated));
        Assert.False(scope.CanSee(LiveKinds.Fact, FactType.SettingsChanged));
        Assert.False(scope.CanSee(LiveKinds.Fact, FactType.GiveawayCreated));
    }

    [Fact]
    public void WithoutSeePosts_ThePostsFactsAreNotSent()
    {
        var scope = LiveScope.ForPerson(ModbotPermissions.ViewCalendar | ModbotPermissions.ManageGroupPosts);

        Assert.False(scope.CanSee(LiveKinds.Fact, FactType.PostSent));
        Assert.False(LiveScope.ForDevice(Guid.NewGuid(), "12345").CanSee(LiveKinds.Fact, FactType.PostSent));
    }
}
