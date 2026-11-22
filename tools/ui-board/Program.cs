using Avalonia;
using Modbot.Companion.App;
using Modbot.Overlay;

namespace Modbot.UiBoard;

/// <summary>
/// The same theme the companion starts with: without one a button or a text box has no template and
/// draws nothing. Not the companion's own application class, which would start the whole client.
/// </summary>
internal sealed class BoardApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;

        var accent = Ui.T.Palette.Accent;
        Resources["SystemAccentColor"] = accent;
        Resources["SystemAccentColorDark1"] = accent;
        Resources["SystemAccentColorDark2"] = accent;
        Resources["SystemAccentColorDark3"] = accent;
        Resources["SystemAccentColorLight1"] = accent;
        Resources["SystemAccentColorLight2"] = accent;
        Resources["SystemAccentColorLight3"] = accent;
    }
}

internal static class Program
{
    private const string Usage = """
        ui-board [--out <folder>]

        Draws every Modbot client surface from made-up data into <folder> (default
        tools/ui-board/out): one picture per tile, manifest.json and board.html.
        """;

    [STAThread]
    private static int Main(string[] args)
    {
        string? outArg = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out" when i + 1 < args.Length:
                    outArg = args[++i];
                    break;
                default:
                    Console.Error.WriteLine(Usage);
                    return 1;
            }
        }

        var folder = Path.GetFullPath(outArg ?? DefaultFolder());

        // The same offscreen start-up the overlay's tests use: Skia drawing for real, the embedded
        // font, no window. This thread becomes Avalonia's UI thread.
        OverlayHost.ConfigureOffscreen<BoardApp>().ConfigureFonts(Brand.RegisterFonts).SetupWithoutStarting();

        return Board.Render(folder) == 0 ? 0 : 2;
    }

    /// <summary>tools/ui-board/out in the checkout this was built from, found by looking upward for the solution file.</summary>
    private static string DefaultFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Modbot.slnx")))
                return Path.Combine(dir.FullName, "tools", "ui-board", "out");
        }

        return Path.Combine(Directory.GetCurrentDirectory(), "out");
    }
}
