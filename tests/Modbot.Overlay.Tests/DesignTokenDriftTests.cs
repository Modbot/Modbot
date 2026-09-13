using System.Reflection;
using System.Text.RegularExpressions;
using Avalonia.Media;

namespace Modbot.Overlay.Tests;

/// <summary>
/// The overlay reuses the web UI's design tokens by value rather than by reference, because a
/// native renderer cannot read a stylesheet. That makes drift possible, so drift is a test.
/// </summary>
/// <remarks>
/// M3 6.0.2 chose to share tokens rather than components: sharing components would have coupled a
/// native renderer to a DOM, and sharing nothing would have let the two surfaces grow apart. This
/// is what makes the middle option hold. If somebody restyles the web UI, this fails and names the
/// token, rather than the overlay quietly becoming a different-looking product.
/// </remarks>
public class DesignTokenDriftTests
{
    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Modbot.slnx")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Could not locate Modbot.slnx.");
    }

    /// <summary>
    /// The tokens as the headset sees them: the dark palette, then the VR overrides applied on
    /// top. That layering is the CSS's own — <c>[data-density="vr"].dark</c> comes after
    /// <c>.dark</c> — and reproducing it here rather than hard-coding the result is what makes
    /// this test able to notice a change in either block.
    /// </summary>
    private static Dictionary<string, string> VrDarkTokens()
    {
        var css = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Modbot.Web", "src", "index.css"));
        var tokens = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var selector in (string[])[":root", ".dark", "[data-density=\"vr\"]", "[data-density=\"vr\"].dark"])
        {
            foreach (var (name, value) in Declarations(css, selector))
                tokens[name] = value;
        }

        return tokens;
    }

    private static IEnumerable<(string Name, string Value)> Declarations(string css, string selector)
    {
        // Matches the block for exactly this selector: the escape stops "[data-density=\"vr\"]"
        // also matching "[data-density=\"vr\"].dark", which would apply the wrong overrides.
        var block = Regex.Match(css, $@"(?m)^{Regex.Escape(selector)}\s*\{{(?<body>[^}}]*)\}}");
        if (!block.Success)
            throw new InvalidOperationException($"index.css no longer has a '{selector}' block.");

        foreach (Match declaration in Regex.Matches(block.Groups["body"].Value, @"--(?<name>[\w-]+)\s*:\s*(?<value>[^;]+);"))
        {
            yield return (declaration.Groups["name"].Value, declaration.Groups["value"].Value.Trim());
        }
    }

    [Theory]
    [InlineData("background", nameof(DesignTokens.Background))]
    [InlineData("card", nameof(DesignTokens.Card))]
    [InlineData("foreground", nameof(DesignTokens.Foreground))]
    [InlineData("muted-foreground", nameof(DesignTokens.MutedForeground))]
    [InlineData("border", nameof(DesignTokens.Border))]
    [InlineData("primary", nameof(DesignTokens.Primary))]
    [InlineData("destructive", nameof(DesignTokens.Destructive))]
    [InlineData("muted", nameof(DesignTokens.Muted))]
    [InlineData("accent", nameof(DesignTokens.Accent))]
    [InlineData("accent-foreground", nameof(DesignTokens.AccentForeground))]
    [InlineData("warn", nameof(DesignTokens.Warn))]
    [InlineData("ok", nameof(DesignTokens.Ok))]
    [InlineData("info", nameof(DesignTokens.Info))]
    public void EveryColourMatchesTheWebUisVrDarkValue(string cssName, string fieldName)
    {
        var expected = Color.Parse(VrDarkTokens()[cssName]);
        var actual = (Color)typeof(DesignTokens).GetField(fieldName)!.GetValue(null)!;

        Assert.True(
            expected == actual,
            $"--{cssName} is {expected} in index.css but DesignTokens.{fieldName} is {actual}.");
    }

    [Theory]
    [InlineData("row-h", DesignTokens.RowHeight)]
    [InlineData("control-h", DesignTokens.ControlHeight)]
    [InlineData("text-base", DesignTokens.TextBase)]
    [InlineData("text-small", DesignTokens.TextSmall)]
    [InlineData("radius", DesignTokens.Radius)]
    public void EveryDensityValueMatchesTheWebUisVrValue(string cssName, double expectedPixels)
    {
        var raw = VrDarkTokens()[cssName];
        var rem = double.Parse(raw.Replace("rem", "", StringComparison.Ordinal).Trim(),
            System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(rem * DesignTokens.Rem, expectedPixels, 3);
    }

    [Fact]
    public void TheHairlineMatchesToo()
    {
        // Two pixels, because one-pixel borders disappear entirely in a headset. Spelled in px in
        // the CSS rather than rem, hence not sharing the conversion above.
        Assert.Equal("2px", VrDarkTokens()["hairline"]);
        Assert.Equal(2.0, DesignTokens.Hairline);
    }

    [Fact]
    public void TheVrPaletteIsNotJustTheDesktopDarkPalette()
    {
        // Guards the reason the VR block exists: headset panels bloom at the extremes, so pure
        // black and low-contrast greys are pulled back. If somebody deletes the VR overrides, the
        // overlay would still compile and would simply look wrong.
        var css = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Modbot.Web", "src", "index.css"));
        var dark = Declarations(css, ".dark").ToDictionary(d => d.Name, d => d.Value, StringComparer.Ordinal);

        Assert.NotEqual(Color.Parse(dark["background"]), DesignTokens.Background);
        Assert.NotEqual(Color.Parse(dark["muted-foreground"]), DesignTokens.MutedForeground);
    }
}
