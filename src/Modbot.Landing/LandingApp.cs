using System.IO.Compression;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modbot.Landing.Configuration;
using Modbot.Landing.Features.Health;
using Modbot.Landing.Features.Pages;
using Modbot.Landing.Features.Instances;
using Modbot.Landing.Features.Security;
using Modbot.Landing.Features.StaticFiles;

namespace Modbot.Landing;

/// <summary>
/// Wires the features together. Program.cs and the test host both call this, so the app under test
/// is the app that ships.
/// </summary>
public static class LandingApp
{
    public static void AddServices(IServiceCollection services, LandingEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(environment);

        services.AddSingleton(new MyModbotAddress(environment.MyUrl));
        services.AddSingleton<BuiltPages>();

        services.AddSingleton(environment);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<OpenInstances>();

        services.AddHttpClient(OpenInstances.HttpClientName, http =>
        {
            // Short, because a visitor is waiting. A Cloud that is slower than this costs one
            // read, and the last good answer is served meanwhile.
            http.Timeout = TimeSpan.FromSeconds(10);
        });

        services.AddResponseCompression(o =>
        {
            // Everything served here is public and the same for every visitor, so compressing over
            // HTTPS gives nothing away (the BREACH concern is secrets reflected beside user input).
            o.EnableForHttps = true;
            o.Providers.Add<BrotliCompressionProvider>();
            o.Providers.Add<GzipCompressionProvider>();
            o.MimeTypes = [.. ResponseCompressionDefaults.MimeTypes, "image/svg+xml"];
        });

        // The default, Fastest, left the script bundle at 39% of its size, larger than gzip would
        // have made it. Optimal costs a few milliseconds on a page this size and is what makes the
        // phone load fast.
        services.Configure<BrotliCompressionProviderOptions>(o => o.Level = CompressionLevel.Optimal);
        services.Configure<GzipCompressionProviderOptions>(o => o.Level = CompressionLevel.Optimal);
    }

    public static void MapEndpoints(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.UseSecurityHeaders();
        app.UseResponseCompression();

        // Static files run before routing, and stand aside for the two built pages, which are only
        // served through their routes so they carry the page headers.
        app.UseBuiltFiles();

        app.UseRouting();

        app.MapHealth();
        app.MapInstances();
        app.MapPages();
    }
}
