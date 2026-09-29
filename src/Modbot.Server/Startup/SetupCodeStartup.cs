using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Features.Onboarding;
using Modbot.Core.Logging;
using Modbot.Core.Users;
using Serilog;

namespace Modbot.Server.Startup;

/// <summary>
/// Prints the setup code at startup while no account exists, and forgets it when one does.
/// </summary>
/// <remarks>
/// The line is tagged <see cref="LogArea.ConsoleOnly"/>, so it reaches the console (what
/// <c>docker compose logs</c> and a host's deploy log show) and nothing else: not the log files,
/// not Seq, and not the database log, which is sent on to Modbot Cloud. See the first-run setup
/// code design §2.
/// </remarks>
internal static class SetupCodeStartup
{
    public static async Task AnnounceAsync(IServiceProvider services, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        var code = services.GetRequiredService<SetupCode>();

        using var scope = services.CreateScope();
        var accounts = scope.ServiceProvider.GetRequiredService<UserAccountService>();

        if (await accounts.AnyUsersAsync(ct))
        {
            code.Drop();
            return;
        }

        Log.ForContext(LogArea.Name, LogArea.ConsoleOnly)
            .Information("Setup code: {SetupCode:l}", code.Current);
    }
}
