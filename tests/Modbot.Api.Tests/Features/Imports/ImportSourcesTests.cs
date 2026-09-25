using Modbot.Api.Features.Imports;
using Modbot.Core.Data.Entities;

namespace Modbot.Api.Tests.Features.Imports;

/// <summary>
/// Which sources a record may be filed under (import design §5), and the one that is legacy.
/// </summary>
public class ImportSourcesTests
{
    [Fact]
    public void EverySourceButImport_MayBeChosen()
    {
        var expected = Enum.GetValues<FactSource>().Where(s => s != FactSource.Import);

        Assert.Equal(expected, ImportSources.All);
        Assert.DoesNotContain(FactSource.Import, ImportSources.All);

        // A source added to the enum later is offered without anybody remembering to add it here,
        // which is the point of reflecting over the enum rather than listing the members twice.
        Assert.Contains(FactSource.AuditLog, ImportSources.All);
        Assert.Contains(FactSource.Modbot, ImportSources.All);
    }

    [Theory]
    [InlineData("AuditLog", FactSource.AuditLog)]
    [InlineData("auditlog", FactSource.AuditLog)]
    [InlineData("SYNCDIFF", FactSource.SyncDiff)]
    [InlineData(" Discord ", FactSource.Discord)]
    [InlineData("Manual", FactSource.Manual)]
    [InlineData("Modbot", FactSource.Modbot)]
    [InlineData("Client", FactSource.Companion)]
    public void ANameIsRead_WithoutRegardToCaseOrSpace(string name, FactSource expected)
    {
        Assert.True(ImportSources.TryParse(name, out var source, out var reason));
        Assert.Equal(expected, source);
        Assert.Null(reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NothingIsManual(string? name)
    {
        Assert.True(ImportSources.TryParse(name, out var source, out _));

        // A person put this in, by hand, from somewhere else.
        Assert.Equal(FactSource.Manual, source);
        Assert.Equal(ImportSources.Default, source);
    }

    [Fact]
    public void AnUnknownName_IsRefused_AndTheErrorListsTheRealOnes()
    {
        Assert.False(ImportSources.TryParse("Wherever", out _, out var reason));
        Assert.Contains("is not a source", reason, StringComparison.Ordinal);
        Assert.Contains("AuditLog", reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// Import is the source imported facts used to carry. Old rows still hold it, so the enum
    /// member stays; choosing it would put back the confusion §5.1 removed.
    /// </summary>
    [Fact]
    public void Import_IsRefused_WithAReasonThatSaysWhy()
    {
        Assert.False(ImportSources.TryParse("Import", out _, out var reason));
        Assert.Contains("no longer a source", reason, StringComparison.Ordinal);

        Assert.False(ImportSources.TryParse("import", out _, out _));
    }
}
