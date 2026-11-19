using Modbot.Core.Data.Entities;

namespace Modbot.Core.Tests.Data;

/// <summary>Finding the avatar id in what an admin pasted into the problem avatar box.</summary>
public class ProblemAvatarIdTests
{
    private const string Id = "avtr_6d1b3a52-0c1e-4b57-9a2d-1f7c0e3b8a90";

    [Theory]
    [InlineData(Id)]
    [InlineData("  " + Id + "  ")]
    [InlineData("https://vrchat.com/home/avatar/" + Id)]
    [InlineData("https://vrchat.com/home/avatar/" + Id + "?x=1#top")]
    [InlineData("https://vrchat.com/home/avatar/" + Id + "/")]
    public void ALinkOrABareId_GivesTheId(string text)
        => Assert.Equal(Id, ProblemAvatar.AvatarIdFrom(text));

    [Theory]
    [InlineData("White Tiger")]
    [InlineData("avtr_ with spaces")]
    [InlineData("avtr_")]
    [InlineData("https://vrchat.com/home/user/usr_6d1b3a52")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void AnythingElse_IsNotAnId(string? text)
        => Assert.Null(ProblemAvatar.AvatarIdFrom(text));
}
