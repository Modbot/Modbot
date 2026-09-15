using Modbot.Landing.Features.Pages;

namespace Modbot.Landing.Features.StaticFiles;

/// <summary>Everything else Web/ builds into wwwroot: scripts, styles, fonts, icons, the share image.</summary>
public static class BuiltFiles
{
    public const string ForeverCache = "public, max-age=31536000, immutable";
    public const string DayCache = "public, max-age=86400";

    public static IApplicationBuilder UseBuiltFiles(this IApplicationBuilder app) =>
        app.UseWhen(
            context => !IsPage(context.Request.Path),
            branch => branch.UseStaticFiles(new StaticFileOptions
            {
                OnPrepareResponse = file =>
                {
                    // Vite names every file under assets/ by its content, so one never changes. The
                    // rest keep their names across builds (favicon, share image, robots.txt), so a
                    // day is as long as a stale copy may live.
                    file.Context.Response.Headers.CacheControl =
                        file.Context.Request.Path.StartsWithSegments("/assets") ? ForeverCache : DayCache;
                },
            }));

    /// <summary>The built pages are only ever served through their routes.</summary>
    private static bool IsPage(PathString path) =>
        BuiltPages.All.Any(page => path.Equals("/" + page, StringComparison.OrdinalIgnoreCase));
}
