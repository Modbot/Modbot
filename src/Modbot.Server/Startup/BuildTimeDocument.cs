using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Modbot.Server.Startup;

/// <summary>
/// The run of Program.cs that only writes the OpenAPI document, during <c>dotnet build</c>.
/// </summary>
/// <remarks>
/// <para>
/// Microsoft.Extensions.ApiDescription.Server starts the entry point through its own tool,
/// <c>GetDocument.Insider</c>, with a server that listens on nothing. It waits for the app to
/// start, so every endpoint is mapped, then reads the document and stops the app.
/// </para>
/// <para>
/// Everything that needs a real deployment is skipped in that run: there is no database to
/// migrate, no key ring to read and no VRChat account to sync. The registrations still happen,
/// because minimal APIs work out each parameter's source from the container while routes are
/// mapped, and the document must describe the same endpoints a real start maps.
/// </para>
/// </remarks>
internal static class BuildTimeDocument
{
    /// <summary>True when this process is the build writing the OpenAPI document.</summary>
    public static bool IsRunning { get; } =
        string.Equals(Assembly.GetEntryAssembly()?.GetName().Name, "GetDocument.Insider", StringComparison.Ordinal);

    /// <summary>
    /// Stands in for DATABASE_URL. Never connected to: nothing that opens a connection runs.
    /// </summary>
    public const string PlaceholderDatabaseUrl = "Host=localhost;Database=modbot_openapi_document";

    /// <summary>
    /// Removes every background service except the one that starts the web host, which builds the
    /// request pipeline the document is read from. The rest -- Modbot's sync jobs, and the
    /// framework's data protection key ring loader -- would reach for the database.
    /// </summary>
    public static void RemoveBackgroundServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        for (var i = services.Count - 1; i >= 0; i--)
        {
            var descriptor = services[i];
            var webHost = string.Equals(
                descriptor.ImplementationType?.Assembly.GetName().Name,
                "Microsoft.AspNetCore.Hosting",
                StringComparison.Ordinal);

            if (descriptor.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService) && !webHost)
                services.RemoveAt(i);
        }
    }
}
