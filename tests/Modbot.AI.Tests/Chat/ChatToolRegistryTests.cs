using Modbot.AI.Chat;
using Modbot.Core.Data.Entities;

namespace Modbot.AI.Tests.Chat;

/// <summary>Which tools a person is offered (AI chat design §3.1 and §3.3).</summary>
public class ChatToolRegistryTests
{
    private static readonly IReadOnlyDictionary<string, bool> NoSwitches = new Dictionary<string, bool>();

    private static ChatToolRegistry Registry() => new(
    [
        new FakeTool("find_person", ModbotPermissions.ViewProfile),
        new FakeTool("search_audit_log", ModbotPermissions.ViewAuditLog),
        new FakeTool("list_live_instances", ModbotPermissions.ViewLiveInstances),
        new FakeTool("both", ModbotPermissions.ViewProfile | ModbotPermissions.ViewAuditLog),
        new FakeTool("warn_person", ModbotPermissions.Warn, onlyReads: false),
    ]);

    [Fact]
    public void AToolThePersonLacksThePermissionFor_IsNeverOffered()
    {
        var offered = Registry().OfferedTo(ModbotPermissions.ViewProfile, NoSwitches).Select(t => t.Name);

        Assert.Equal(["find_person"], offered);
    }

    [Fact]
    public void AToolNeedingTwoPermissions_NeedsBoth()
    {
        var registry = Registry();

        Assert.DoesNotContain("both", registry.OfferedTo(ModbotPermissions.ViewAuditLog, NoSwitches).Select(t => t.Name));
        Assert.Contains("both", registry.OfferedTo(ModbotPermissions.ViewAuditLog | ModbotPermissions.ViewProfile, NoSwitches).Select(t => t.Name));
    }

    [Fact]
    public void NoPermissions_NoTools()
    {
        Assert.Empty(Registry().OfferedTo(ModbotPermissions.None, NoSwitches));
    }

    [Fact]
    public void Administrator_IsOfferedEveryReadTool_ButNotAnActingOneThatIsSwitchedOff()
    {
        var offered = Registry().OfferedTo(ModbotPermissions.Administrator, NoSwitches).Select(t => t.Name);

        Assert.Equal(["both", "find_person", "list_live_instances", "search_audit_log"], offered);
    }

    [Fact]
    public void AReadToolSwitchedOff_IsNotOfferedToAnybody()
    {
        var switches = new Dictionary<string, bool> { ["find_person"] = false };

        Assert.DoesNotContain("find_person", Registry().OfferedTo(ModbotPermissions.Administrator, switches).Select(t => t.Name));
    }

    [Fact]
    public void AnActingTool_IsOffOnlyUntilSwitchedOn_AndStillNeedsItsPermission()
    {
        var registry = Registry();
        var on = new Dictionary<string, bool> { ["warn_person"] = true };

        Assert.DoesNotContain("warn_person", registry.OfferedTo(ModbotPermissions.Warn, NoSwitches).Select(t => t.Name));
        Assert.Contains("warn_person", registry.OfferedTo(ModbotPermissions.Warn, on).Select(t => t.Name));
        Assert.DoesNotContain("warn_person", registry.OfferedTo(ModbotPermissions.ViewProfile, on).Select(t => t.Name));
    }

    [Fact]
    public void TwoToolsWithOneName_AreRefused()
    {
        Assert.Throws<InvalidOperationException>(() => new ChatToolRegistry(
            [new FakeTool("same", ModbotPermissions.None), new FakeTool("same", ModbotPermissions.None)]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    public void UnreadableSwitches_CountAsNone(string? json)
    {
        Assert.Empty(ChatToolRegistry.ParseSwitches(json));
    }
}
