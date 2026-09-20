using Modbot.Landing.Features.Pages;
using Modbot.Landing.Features.Setup;

namespace Modbot.Landing.Features.StaticFiles;

/// <summary>Everything else Web/ builds into wwwroot: scripts, styles, fonts, icons, the share image.</summary>
public static class BuiltFiles
{
    public const string ForeverCache = "public, max-age=31536000, immutable";
    public const string DayCache = "public, max-age=86400";

    public static IApplicationBuilder UseBuiltFiles(this IApplicationBuilder app) =>
        app.UseWhen(
            context => !IsServedByARoute(context.Request.Path),
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

    /// <summary>
    /// The built pages and the two setup files are only ever served through their routes: the
    /// pages so they carry their own content security policy, the setup files so they arrive as
    /// plain text a browser shows rather than downloads.
    /// </summary>
    private static bool IsServedByARoute(PathString path) =>
        BuiltPages.All.Concat(SetupFiles.All)
            .Any(file => path.Equals("/" + file, StringComparison.OrdinalIgnoreCase));
}
