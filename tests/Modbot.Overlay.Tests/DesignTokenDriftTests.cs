using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Avalonia.Media;

namespace Modbot.Overlay.Tests;

/// <summary>
/// The desktop window and the overlay reuse the web UI's design tokens by value rather than by
/// reference, because neither can read a stylesheet. That makes drift possible, so drift is a test.
/// </summary>
/// <remarks>
/// M3 6.0.2 chose to share tokens rather than components: sharing components would have coupled a
/// native renderer to a DOM, and sharing nothing would have let the surfaces grow apart. This is
/// what makes the middle option hold. If somebody restyles the web UI, this fails and names the
/// token, rather than the client quietly becoming a different-looking product.
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

    private static string Css() => File.ReadAllText(
        Path.Combine(FindRepoRoot(), "src", "Modbot.Web", "src", "index.css"));

    /// <summary>
    /// Applies a run of selectors in the order the CSS itself cascades them, so a token defined in
    /// one block and overridden in a later one resolves the way a browser would.
    /// </summary>
    private static Dictionary<string, string> Tokens(params string[] selectors)
    {
        var css = Css();
        var tokens = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var selector in selectors)
        {
            foreach (var (name, value) in Declarations(css, selector))
                tokens[name] = value;
        }

        return tokens;
    }

    private static IEnumerable<(string Name, string Value)> Declarations(string css, string selector)
    {
        // The escape stops "[data-density=\"vr\"]" also matching "[data-density=\"vr\"].dark",
        // which would silently apply the wrong overrides and make this test agree with anything.
        var block = Regex.Match(css, $@"(?m)^{Regex.Escape(selector)}\s*\{{(?<body>[^}}]*)\}}");
        if (!block.Success)
            throw new InvalidOperationException($"index.css no longer has a '{selector}' block.");

        foreach (Match declaration in Regex.Matches(
            block.Groups["body"].Value, @"--(?<name>[\w-]+)\s*:\s*(?<value>[^;]+);"))
        {
            yield return (declaration.Groups["name"].Value, declaration.Groups["value"].Value.Trim());
        }
    }

    private static double Pixels(string raw)
    {
        var trimmed = raw.Trim();

        return trimmed.EndsWith("rem", StringComparison.Ordinal)
            ? double.Parse(trimmed[..^3], CultureInfo.InvariantCulture) * Density.Rem
            : double.Parse(trimmed.Replace("px", "", StringComparison.Ordinal), CultureInfo.InvariantCulture);
    }

    public static TheoryData<string, string> DarkColours() =>
        new()
        {
            { "background", nameof(ModbotPalette.Background) },
            { "card", nameof(ModbotPalette.Surface) },
            { "muted", nameof(ModbotPalette.Surface2) },
            { "foreground", nameof(ModbotPalette.Text) },
            { "muted-foreground", nameof(ModbotPalette.TextDim) },
            { "border", nameof(ModbotPalette.Border) },
            { "input", nameof(ModbotPalette.Border2) },
            { "primary", nameof(ModbotPalette.Accent) },
            { "primary-foreground", nameof(ModbotPalette.AccentForeground) },
            { "accent", nameof(ModbotPalette.AccentDim) },
            { "destructive", nameof(ModbotPalette.Danger) },
            { "warn", nameof(ModbotPalette.Warn) },
            { "ok", nameof(ModbotPalette.Ok) },
            { "info", nameof(ModbotPalette.Info) },
        };

    [Theory]
    [MemberData(nameof(DarkColours))]
    public void TheDesktopPaletteMatchesTheWebUisDarkValue(string cssName, string property)
    {
        var expected = Color.Parse(Tokens(":root", ".dark")[cssName]);
        var actual = (Color)typeof(ModbotPalette).GetProperty(property)!.GetValue(ModbotPalette.Dark)!;

        Assert.True(
            expected == actual,
            $"--{cssName} is {expected} in index.css but ModbotPalette.Dark.{property} is {actual}.");
    }

    [Theory]
    [MemberData(nameof(DarkColours))]
    public void TheHeadsetPaletteMatchesTheWebUisVrDarkValue(string cssName, string property)
    {
        var expected = Color.Parse(Tokens(":root", ".dark", "[data-density=\"vr\"]", "[data-density=\"vr\"].dark")[cssName]);
        var actual = (Color)typeof(ModbotPalette).GetProperty(property)!.GetValue(ModbotPalette.VrDark)!;

        Assert.True(
            expected == actual,
            $"--{cssName} is {expected} for VR in index.css but ModbotPalette.VrDark.{property} is {actual}.");
    }

    [Fact]
    public void TheHeadsetPaletteIsNotJustTheDesktopOne()
    {
        // Guards the reason the VR block exists at all: headset panels bloom at the extremes, so
        // pure black and low-contrast greys are pulled back. Delete the VR overrides and the
        // overlay would still compile and would simply look wrong.
        Assert.NotEqual(ModbotPalette.Dark.Background, ModbotPalette.VrDark.Background);
        Assert.NotEqual(ModbotPalette.Dark.TextDim, ModbotPalette.VrDark.TextDim);
        Assert.NotEqual(ModbotPalette.Dark.Border, ModbotPalette.VrDark.Border);

        // And that it is the same palette underneath, rather than a second design.
        Assert.Equal(ModbotPalette.Dark.Accent, ModbotPalette.VrDark.Accent);
        Assert.Equal(ModbotPalette.Dark.Danger, ModbotPalette.VrDark.Danger);
    }

    public static TheoryData<string, string> DensityValues() =>
        new()
        {
            { "row-h", nameof(Density.RowHeight) },
            { "control-h", nameof(Density.ControlHeight) },
            { "text-base", nameof(Density.TextBase) },
            { "text-small", nameof(Density.TextSmall) },
            { "radius", nameof(Density.Radius) },
            { "hairline", nameof(Density.Hairline) },
        };

    [Theory]
    [MemberData(nameof(DensityValues))]
    public void TheDenseScaleMatchesTheWebUisDefault(string cssName, string property)
    {
        // Dense is the client window's density: it is a tool for scanning lists, and the whole
        // point of the default is that more of the list fits on screen at once.
        Assert.Equal(
            Pixels(Tokens(":root")[cssName]),
            (double)typeof(Density).GetProperty(property)!.GetValue(Density.Dense)!,
            3);
    }

    [Theory]
    [MemberData(nameof(DensityValues))]
    public void TheComfortableScaleMatchesTheWebUis(string cssName, string property)
    {
        Assert.Equal(
            Pixels(Tokens(":root", "[data-density=\"comfortable\"]")[cssName]),
            (double)typeof(Density).GetProperty(property)!.GetValue(Density.Comfortable)!,
            3);
    }

    [Theory]
    [MemberData(nameof(DensityValues))]
    public void TheHeadsetScaleMatchesTheWebUis(string cssName, string property)
    {
        Assert.Equal(
            Pixels(Tokens(":root", "[data-density=\"vr\"]")[cssName]),
            (double)typeof(Density).GetProperty(property)!.GetValue(Density.Vr)!,
            3);
    }

    [Fact]
    public void TheTwoTokenSetsAreTheOnesEachSurfaceActuallyUses()
    {
        // A desktop window drawn at headset density would be enormous and a headset panel drawn at
        // desktop density would be unreadable, and both mistakes are one wrong constant away.
        Assert.Equal(Density.Dense, DesignTokens.Desktop.Density);
        Assert.Equal(ModbotPalette.Dark, DesignTokens.Desktop.Palette);
        Assert.Equal(Density.Vr, DesignTokens.Vr.Density);
        Assert.Equal(ModbotPalette.VrDark, DesignTokens.Vr.Palette);
    }

    [Fact]
    public void TheExtraSurfaceStepsComeFromTheReferencePrototype()
    {
        // The stylesheet has no third surface or faint text; the prototype does, and a table needs
        // them to separate a header from a row from a hover. Checked against the prototype so the
        // two do not drift either.
        var prototype = File.ReadAllText(
            Path.Combine(FindRepoRoot(), "explore", "design", "index.html"));

        foreach (var (token, expected) in ((string, Color)[])
            [
                ("surface-2", ModbotPalette.Dark.Surface2),
                ("surface-3", ModbotPalette.Dark.Surface3),
                ("border-2", ModbotPalette.Dark.Border2),
                ("text-faint", ModbotPalette.Dark.TextFaint),
                ("danger-dim", ModbotPalette.Dark.DangerDim),
                ("warn-dim", ModbotPalette.Dark.WarnDim),
                ("ok-dim", ModbotPalette.Dark.OkDim),
                ("info-dim", ModbotPalette.Dark.InfoDim),
                ("accent-dim", ModbotPalette.Dark.AccentDim),
            ])
        {
            var match = Regex.Match(prototype, $@"--{Regex.Escape(token)}:\s*(?<value>#[0-9a-fA-F]{{3,8}})");
            Assert.True(match.Success, $"The prototype no longer defines --{token}.");
            Assert.Equal(expected, Color.Parse(match.Groups["value"].Value));
        }
    }
}
