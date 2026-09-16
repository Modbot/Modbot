using System.Globalization;

namespace Modbot.Demo;

/// <summary>
/// The pictures the demo uses for profiles, avatars and worlds.
/// </summary>
/// <remarks>
/// <para>
/// Every picture is a two-colour gradient written straight into the <c>src</c> as a
/// <c>data:image/svg+xml</c> URL. Nothing is fetched, from anywhere: a demo that pulled four hundred
/// thumbnails off somebody else's image host would break the moment that host rate-limited it, would
/// not work at all on a machine with no internet, and would put a third party in the middle of a
/// page that is supposed to have no outside dependencies (demo mode design §4.2).
/// </para>
/// <para>
/// They are also, at a glance, obviously not photographs of anybody — which is the right look for
/// made-up people.
/// </para>
/// </remarks>
internal static class DemoPictures
{
    /// <summary>A gradient, picked from a number so the same person always gets the same one.</summary>
    public static string Gradient(int n)
    {
        var from = Colour(n * 31 + 7);
        var to = Colour(n * 17 + 53);

        // Percent-encoded rather than base64: it is shorter, and it stays readable in the database.
        return "data:image/svg+xml,"
            + "%3Csvg%20xmlns='http://www.w3.org/2000/svg'%20viewBox='0%200%2064%2064'%3E"
            + "%3Cdefs%3E%3ClinearGradient%20id='g'%20x1='0'%20y1='0'%20x2='1'%20y2='1'%3E"
            + $"%3Cstop%20offset='0'%20stop-color='%23{from}'/%3E"
            + $"%3Cstop%20offset='1'%20stop-color='%23{to}'/%3E"
            + "%3C/linearGradient%3E%3C/defs%3E"
            + "%3Crect%20width='64'%20height='64'%20fill='url(%23g)'/%3E%3C/svg%3E";
    }

    /// <summary>
    /// A readable colour: full saturation is avoided so a page of four hundred of these is calm.
    /// </summary>
    private static string Colour(int n)
    {
        var hue = Math.Abs(n * 47) % 360;
        var (r, g, b) = FromHue(hue, 0.42, 0.62);

        return r.ToString("x2", CultureInfo.InvariantCulture)
            + g.ToString("x2", CultureInfo.InvariantCulture)
            + b.ToString("x2", CultureInfo.InvariantCulture);
    }

    private static (int R, int G, int B) FromHue(int hue, double saturation, double lightness)
    {
        var c = (1 - Math.Abs((2 * lightness) - 1)) * saturation;
        var x = c * (1 - Math.Abs((hue / 60.0 % 2) - 1));
        var m = lightness - (c / 2);

        var (r, g, b) = hue switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x),
        };

        return ((int)Math.Round((r + m) * 255), (int)Math.Round((g + m) * 255), (int)Math.Round((b + m) * 255));
    }
}
