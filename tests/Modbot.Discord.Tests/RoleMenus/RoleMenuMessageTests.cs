using Modbot.Core.Data.Entities;
using Modbot.Discord.RoleMenus;

namespace Modbot.Discord.Tests.RoleMenus;

/// <summary>The ids a role menu's buttons and list carry, and the buttons and list a menu turns into.</summary>
public class RoleMenuMessageTests
{
    private static DiscordRoleMenu Menu(string style, int roles = 3, bool singleChoice = false)
    {
        var menu = new DiscordRoleMenu { Name = "Colours", Style = style, SingleChoice = singleChoice };

        for (var i = roles - 1; i >= 0; i--)
            menu.Roles.Add(new DiscordRoleMenuRole { MenuId = menu.Id, RoleId = (800 + i).ToString(System.Globalization.CultureInfo.InvariantCulture), Label = $"Role {i}", Position = i });

        return menu;
    }

    [Fact]
    public void AButtonId_IsReadBackAsItsMenuAndRole()
    {
        var menu = Guid.CreateVersion7();

        var id = RoleMenuMessage.ButtonId(menu, "812");

        Assert.True(RoleMenuMessage.IsPress(id));
        Assert.True(RoleMenuMessage.TryRead(id, out var readMenu, out var readRole));
        Assert.Equal(menu, readMenu);
        Assert.Equal("812", readRole);
    }

    [Fact]
    public void AListId_IsReadBackAsItsMenuWithNoRole()
    {
        var menu = Guid.CreateVersion7();

        Assert.True(RoleMenuMessage.TryRead(RoleMenuMessage.ListId(menu), out var readMenu, out var readRole));
        Assert.Equal(menu, readMenu);
        Assert.Null(readRole);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("modbot:gate:abc")]
    [InlineData("modbot:rolemenu:")]
    [InlineData("modbot:rolemenu:not-a-guid")]
    [InlineData("modbot:rolemenu:0123456789abcdef0123456789abcdef:")]
    public void AnIdThatIsNotARoleMenusOrIsMangled_IsNotRead(string? id)
        => Assert.False(RoleMenuMessage.TryRead(id, out _, out _));

    [Fact]
    public void AnotherFeaturesButton_IsNotARoleMenuPress()
        => Assert.False(RoleMenuMessage.IsPress("modbot:gate:abc"));

    [Fact]
    public void ButtonsMenu_MakesOneButtonPerRole_InOrder_AndNoList()
    {
        var menu = Menu(DiscordRoleMenuStyles.Buttons);

        var buttons = RoleMenuMessage.Buttons(menu);

        Assert.Equal(["Role 0", "Role 1", "Role 2"], buttons.Select(b => b.Label));
        Assert.Equal(["800", "801", "802"], buttons.Select(b => RoleMenuMessage.TryRead(b.Id, out _, out var role) ? role : null));
        Assert.Null(RoleMenuMessage.Select(menu));
    }

    [Fact]
    public void SelectMenu_MakesOneList_AndNoButtons()
    {
        var menu = Menu(DiscordRoleMenuStyles.Select);

        var select = RoleMenuMessage.Select(menu);

        Assert.NotNull(select);
        Assert.Equal(RoleMenuMessage.ListId(menu.Id), select.Id);
        Assert.Equal(["800", "801", "802"], select.Choices.Select(c => c.Value));
        Assert.Equal(3, select.MostPicks);
        Assert.Empty(RoleMenuMessage.Buttons(menu));
    }

    [Fact]
    public void APickOneList_AllowsOnePick()
        => Assert.Equal(1, RoleMenuMessage.Select(Menu(DiscordRoleMenuStyles.Select, singleChoice: true))!.MostPicks);

    [Fact]
    public void AMenuOfTwentyFiveRoles_FitsDiscordsLimits()
    {
        var menu = Menu(DiscordRoleMenuStyles.Buttons, DiscordRoleMenuLimits.MostRoles);

        Assert.Equal(25, RoleMenuMessage.Buttons(menu).Count);
    }
}
