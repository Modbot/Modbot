using Modbot.Api.Features.Audit;
using Modbot.Core.Data.Entities;

namespace Modbot.Api.Tests.Features.Audit;

public class FactLabelsTests
{
    /// <summary>
    /// Everything the audit-log producer can write has a label, so the timeline never shows a
    /// raw type string for something VRChat has a sentence for. The fallback to the raw type is
    /// deliberate for types nobody has described; these have all been described.
    /// </summary>
    [Fact]
    public void EveryGroupAuditLogTypeHasAPlainLabel()
    {
        var groupTypes = FactType.All
            .Where(t => t.StartsWith("vrchat.group.", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(groupTypes);
        Assert.All(groupTypes, t => Assert.NotEqual(t, FactLabels.For(t)));
    }

    /// <summary>
    /// Two different actions, two different labels. Blending them is the mistake the separate
    /// fact types exist to prevent, and a shared label would undo it on the screen.
    /// </summary>
    [Fact]
    public void AnInstanceKickAndAGroupKickReadDifferently()
        => Assert.NotEqual(FactLabels.For(FactType.MemberKicked), FactLabels.For(FactType.GroupInstanceKick));
}
