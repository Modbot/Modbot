using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Modbot.Companion.Presentation;
using Modbot.Overlay;
using Modbot.Overlay.Views;

namespace Modbot.OverlayShot;

internal sealed class ShotApp : Application;

internal static class Program
{
    /// <summary>The colour behind every picture: a dark, flat stand-in for the world behind a panel.</summary>
    private static readonly IBrush Backdrop = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));

    /// <summary>The size of VRChat's window the desktop panel is placed for.</summary>
    private const int ClientWidth = 1920;

    private const int ClientHeight = 1080;

    private const string Usage = """
        overlay-shot [--out <folder>] [--palette-user <VRChat user id>]

        Draws Modbot's overlay panel (Instance list and Audit Log) and notification in the headset's
        look and in the desktop's VRChat look, from made-up data, into <folder> (default
        tools/overlay-shot/out), plus compare.png with each pair side by side.

        --palette-user  read the colour palette VRChat has selected for that user from this PC's
                        registry, the way the desktop window does, instead of the sample palette.
        """;

    [STAThread]
    private static int Main(string[] args)
    {
        string? outArg = null;
        string? paletteUser = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out" when i + 1 < args.Length:
                    outArg = args[++i];
                    break;
                case "--palette-user" when i + 1 < args.Length:
                    paletteUser = args[++i];
                    break;
                default:
                    Console.Error.WriteLine(Usage);
                    return 1;
            }
        }

        var folder = Path.GetFullPath(outArg ?? DefaultFolder());
        Directory.CreateDirectory(folder);

        var colours = paletteUser is null ? OverlayColours.From(SamplePalette) : OverlayColours.From(ReadRealPalette(paletteUser));

        if (colours is null)
        {
            Console.Error.WriteLine(paletteUser is null
                ? "The sample palette was refused as unreadable."
                : "No usable palette was found for that user; nothing drawn.");
            return 2;
        }

        // The same offscreen start-up the overlay's tests use: Skia drawing for real, the embedded
        // font, no window. This thread becomes Avalonia's UI thread.
        OverlayHost.ConfigureOffscreen<ShotApp>().SetupWithoutStarting();

        var headset = OverlayLook.Headset;
        var desktop = OverlayLook.FromColours(colours);

        var fit = VRChatHudLayout.For(ClientWidth, ClientHeight)?.Overlay()
            ?? throw new InvalidOperationException("The desktop panel has no room at the chosen window size.");

        var pairs = new List<(string Name, string Title, Bitmap Headset, Bitmap Desktop)>();

        foreach (var (name, title, screen) in new[]
        {
            ("instance", "Instance list", Samples.Roster()),
            ("auditlog", "Audit Log", Samples.AuditLog()),
        })
        {
            var h = Draw(HeadsetPanel(screen, headset), OverlayHost.DefaultResolution, OverlayHost.DefaultResolution);
            var d = Draw(DesktopPanel(screen, desktop, fit.Scale), fit.Width, fit.Height);
            pairs.Add((name, title, Save(h, folder, name + "-headset.png"), Save(d, folder, name + "-desktop.png")));
        }

        // The notification: the headset panel at its own texture size, and the desktop window the
        // way DesktopNotifyWindow draws it, at the desktop's tokens and its fixed width.
        var cards = Samples.Notification();
        var nh = Draw(
            PanelFrame.Notification(NotificationView.Build(cards), PanelBar.For(Modbot.Companion.Overlay.OverlayPlacement.Default, false), null, OverlayHost.DefaultNotificationResolution),
            OverlayHost.DefaultNotificationResolution,
            OverlayHost.DefaultNotificationResolution);
        var nd = DrawToContent(NotificationView.Build(cards, DesignTokens.Desktop), DesktopNotifyWidth);
        pairs.Add(("notification", "Notification", Save(nh, folder, "notification-headset.png"), Save(nd, folder, "notification-desktop.png")));

        Save(Compare(pairs), folder, "compare.png");

        Console.WriteLine($"Drawn into {folder}");
        Console.WriteLine($"  palette: {(paletteUser is null ? "sample" : "read from this PC")}; desktop panel {fit.Width}x{fit.Height} for a {ClientWidth}x{ClientHeight} window");
        return 0;
    }

    /// <summary>What the desktop notification window is wide (DesktopNotifyWindow.PanelWidth).</summary>
    private const double DesktopNotifyWidth = 340;

    /// <summary>The user's Thy Kingdom palette, as VRChat stores it.</summary>
    private static readonly VRChatPalette SamplePalette = new(
        Highlights: Hex("#C53B48"),
        Icons: Hex("#F66229"),
        Buttons: Hex("#934226"),
        Backgrounds: Hex("#67171E"),
        Text: Hex("#FFE07B"),
        Subtext: Hex("#D86049"));

    private static PaletteColour Hex(string text)
        => PaletteColour.TryParse(text, out var colour) ? colour : throw new FormatException(text);

    /// <summary>The headset panel: what OverlayHost hands the renderer, with the bar drawn but not showing.</summary>
    private static Control HeadsetPanel(OverlayScreen screen, OverlayLook look)
        => PanelFrame.Main(
            OverlayView.Build(screen, null, look),
            PanelBar.For(Modbot.Companion.Overlay.OverlayPlacement.Default, false),
            null,
            OverlayHost.DefaultResolution);

    /// <summary>
    /// The desktop window's panel: the title strip and the screen in a rounded frame, laid out at the
    /// window's design size and drawn at the scale VRChat's window asks for. The strip is a plain
    /// copy of the window's (group name in the middle) without its buttons; the screen is the real one.
    /// </summary>
    private static Control DesktopPanel(OverlayScreen screen, OverlayLook look, double scale)
    {
        var v = look.VRChat ?? throw new InvalidOperationException("Not the VRChat look.");
        var inner = VRChatLook.FrameRadius - VRChatLook.EdgeWidth;

        var head = new Border
        {
            Background = v.Bar(),
            CornerRadius = new CornerRadius(inner, inner, 0, 0),
            Padding = new Thickness(14, 8),
            Child = new TextBlock
            {
                Text = screen.GroupLabel,
                FontSize = 20,
                FontWeight = FontWeight.ExtraBold,
                FontFamily = new FontFamily(DesignTokens.FontFamily),
                Foreground = v.Heading,
                HorizontalAlignment = HorizontalAlignment.Center,
            },
        };
        DockPanel.SetDock(head, Dock.Top);

        var frame = new Border
        {
            Width = VRChatHudLayout.OverlayDesignWidth - (2 * VRChatLook.ShadowRoom),
            Height = VRChatHudLayout.OverlayDesignHeight - (2 * VRChatLook.ShadowRoom),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(VRChatLook.ShadowRoom),
            Background = v.Panel(),
            BorderBrush = v.Edge,
            BorderThickness = new Thickness(VRChatLook.EdgeWidth),
            CornerRadius = new CornerRadius(VRChatLook.FrameRadius),
            BoxShadow = v.PanelShadow,
            Child = new DockPanel { Children = { head, new Border { ClipToBounds = true, Child = OverlayView.Build(screen, null, look) } } },
        };

        return new Viewbox
        {
            Stretch = Stretch.Fill,
            Child = new Border
            {
                Width = VRChatHudLayout.OverlayDesignWidth,
                Height = VRChatHudLayout.OverlayDesignHeight,
                Child = frame,
            },
        };
    }

    /// <summary>Lays <paramref name="root"/> out at a fixed size over the backdrop and draws it.</summary>
    private static RenderTargetBitmap Draw(Control root, double width, double height)
    {
        var stage = new Border { Background = Backdrop, Child = root };
        var target = new RenderTargetBitmap(new PixelSize((int)width, (int)height), new Vector(96, 96));

        stage.Measure(new Size(width, height));
        stage.Arrange(new Rect(0, 0, width, height));
        target.Render(stage);
        return target;
    }

    /// <summary>Draws <paramref name="root"/> as wide as asked and as tall as it needs, like a window that sizes to its content.</summary>
    private static RenderTargetBitmap DrawToContent(Control root, double width)
    {
        root.Measure(new Size(width, double.PositiveInfinity));
        return Draw(root, width, Math.Ceiling(root.DesiredSize.Height));
    }

    private static Bitmap Save(RenderTargetBitmap picture, string folder, string file)
    {
        var path = Path.Combine(folder, file);
        picture.Save(path);
        Console.WriteLine($"  {file} {picture.PixelSize.Width}x{picture.PixelSize.Height}");
        return picture;
    }

    /// <summary>Each pair on a row: the headset's picture at the left, the desktop's at the right, a label above each.</summary>
    private static RenderTargetBitmap Compare(List<(string Name, string Title, Bitmap Headset, Bitmap Desktop)> pairs)
    {
        var rows = new StackPanel { Spacing = 28, Margin = new Thickness(24) };

        foreach (var (_, title, headset, desktop) in pairs)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 24 };
            row.Children.Add(Labelled($"{title} - headset ({headset.PixelSize.Width}x{headset.PixelSize.Height})", headset));
            row.Children.Add(Labelled($"{title} - desktop ({desktop.PixelSize.Width}x{desktop.PixelSize.Height})", desktop));
            rows.Children.Add(row);
        }

        var stage = new Border { Background = new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x10)), Child = rows };
        stage.Measure(Size.Infinity);
        var width = Math.Ceiling(stage.DesiredSize.Width);
        var height = Math.Ceiling(stage.DesiredSize.Height);

        var target = new RenderTargetBitmap(new PixelSize((int)width, (int)height), new Vector(96, 96));
        stage.Arrange(new Rect(0, 0, width, height));
        target.Render(stage);
        return target;
    }

    private static Control Labelled(string label, Bitmap picture)
    {
        var column = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Top };
        column.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 18,
            FontFamily = new FontFamily(DesignTokens.FontFamily),
            Foreground = Brushes.White,
        });
        column.Children.Add(new Image { Source = picture, Width = picture.PixelSize.Width, Height = picture.PixelSize.Height, Stretch = Stretch.None });
        return column;
    }

    /// <summary>The palette VRChat has selected for a user, read the way the desktop window reads it. Null when there is none.</summary>
    private static VRChatPalette? ReadRealPalette(string userId)
    {
        if (!OperatingSystem.IsWindows() || !VRChatPaletteValue.IsUsableId(userId))
            return null;

        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\VRChat\VRChat", writable: false);

        if (key is null || VRChatPaletteValue.Pick(key.GetValueNames(), userId) is not { } name)
            return null;

        return key.GetValue(name) is byte[] data ? VRChatPalette.Parse(data) : null;
    }

    /// <summary>tools/overlay-shot/out in the checkout this was built from, found by looking upward for the solution file.</summary>
    private static string DefaultFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Modbot.slnx")))
                return Path.Combine(dir.FullName, "tools", "overlay-shot", "out");
        }

        return Path.Combine(Directory.GetCurrentDirectory(), "out");
    }
}
