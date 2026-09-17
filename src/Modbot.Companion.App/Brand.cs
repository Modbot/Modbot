using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Modbot.Companion.App;

/// <summary>
/// The brand as the companion shows it (.agent/specs/2026-09-16-brand-design.md): the
/// head-only mascot mark for the window, the tray and the sidebar, and the display face for the
/// one word next to it. Everything else in the window is the body face and the shared tokens.
/// </summary>
internal static class Brand
{
    private const string Assets = "avares://Modbot/Assets";

    /// <summary>The key the embedded font collection is registered under.</summary>
    private static readonly Uri FontsKey = new("fonts:Modbot", UriKind.Absolute);

    /// <summary>The wordmark's face, read from the assembly rather than the machine.</summary>
    private static readonly FontFamily Display = new($"{FontsKey}#Bricolage Grotesque");

    /// <summary>Registers the embedded faces with Avalonia's font manager. Called once at start.</summary>
    public static void RegisterFonts(FontManager manager)
        => manager.AddFontCollection(new EmbeddedFontCollection(FontsKey, new Uri($"{Assets}/Fonts", UriKind.Absolute)));

    /// <summary>The window and tray icon.</summary>
    public static WindowIcon Icon()
        => new(AssetLoader.Open(new Uri($"{Assets}/Modbot.ico", UriKind.Absolute)));

    /// <summary>The mark at a given size, for the sidebar's brand row.</summary>
    public static Image Mark(double size)
        => new()
        {
            Width = size,
            Height = size,
            Source = new Bitmap(AssetLoader.Open(new Uri($"{Assets}/icon-256.png", UriKind.Absolute))),
        };

    /// <summary>Sets the word "Modbot" in the display face.</summary>
    public static TextBlock Wordmark(TextBlock text)
    {
        text.FontFamily = Display;
        return text;
    }
}
