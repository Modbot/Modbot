using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Modbot.Analytics;
using Modbot.AI;
using Modbot.Api;
using Modbot.Api.Features.Companion;
using Modbot.Api.Auth;
using Modbot.Api.Features.Evidence;
using Modbot.Core.Configuration;
using Modbot.Core.Data;
using Modbot.Core.Discord;
using Modbot.Core.Email;
using Modbot.Core.Security;
using Modbot.Core.Time;
using Modbot.Evidence;
using Modbot.Evidence.Upload;
using Modbot.TestSupport;
using Modbot.VRChat;
using Modbot.VRChat.Scheduling;

namespace Modbot.Demo.Tests;

/// <summary>
/// A demo's container can be built, with dependency injection checking every registration.
/// </summary>
/// <remarks>
/// <para>
/// A demo composes less than a real deployment: no VRChat sync, no Discord bot, no mail relay
/// (demo mode design §3.2). Everything else is registered exactly as it always is — so a service
/// registered by the API that <em>requires</em> something only the sync registers does not fail on
/// the page that uses it. It fails when the container is built, which is the whole app refusing to
/// start, and only ever on a demo.
/// </para>
/// <para>
/// That is what happened when the bio check took <c>VRChatUserProfiles</c> as a required
/// dependency: <c>MODBOT_DEMO=1</c> would not start at all, and the failure looked like a broken
/// deployment rather than one missing registration. Anything the API needs and a demo does not
/// register has to be optional, and this is the test that says so.
/// </para>
/// <para>
/// Validation builds every call site without constructing anything, so this needs no database.
/// </para>
/// </remarks>
public class DemoContainerTests
{
    [Fact]
    public void ADemoContainerBuildsWithEveryRegistrationChecked()
    {
        using var app = DemoShapedHost();

        Assert.NotNull(app.Services.GetRequiredService<IVRChatGate>());

        // The one that broke: registered by the API, and nothing in a demo writes vrchat_user rows.
        using var scope = app.Services.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<Api.Features.Auth.VRChatLink.VRChatBioCheck>());
    }

    /// <summary>
    /// The pieces the host composes on a demo, with validation on: no VRChat sync, and the demo's
    /// own gate, Discord messenger and mail relay in place of the real ones.
    /// </summary>
    private static WebApplication DemoShapedHost()
    {
        var clock = new FakeClock();

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.QuietForTests();

        // Every registration is checked as the container is built, and nothing scoped may be
        // resolved from the root. Development does this by default; saying so here means the test
        // does not depend on which environment it happens to run in.
        builder.Host.UseDefaultServiceProvider(options =>
        {
            options.ValidateOnBuild = true;
            options.ValidateScopes = true;
        });

        builder.Services.AddDbContext<ModbotContext>(o => o.UseNpgsql("Host=localhost;Database=none"));
        builder.Services.AddSingleton<IModbotClock>(clock);
        builder.Services.AddSingleton<IMonotonicClock, StopwatchMonotonicClock>();
        builder.Services.AddSingleton(HostPlatform.SelfHosted);
        builder.Services.AddSingleton<ISecretProtector>(_ => throw new InvalidOperationException("Not built in this test."));

        builder.Services.AddModbotAnalytics();

        // What a demo puts in place of the things that reach outside itself.
        builder.Services.AddSingleton<IDiscordMessenger, DemoDiscordMessenger>();
        builder.Services.AddSingleton<IVRChatGate>(new DemoVRChatGate(clock));
        builder.Services.AddSingleton<Modbot.VRChat.Moderation.GroupModeration>();

        // The gate and its buckets, registered as the host registers them, and then replaced --
        // in that order, so the demo's gate is the one that wins.
        builder.Services.AddModbotVRChat();
        builder.Services.AddSingleton<IVRChatGate>(new DemoVRChatGate(clock));

        builder.Services.AddModbotAi();
        builder.Services.AddModbotAiModerationJobs();
        builder.Services.AddModbotAiCallLogPrune();
        builder.Services.AddModbotAiInsightSchedule();
        builder.Services.AddModbotAiPriceFetch();

        builder.Services.AddModbotEvidence();
        builder.Services.AddModbotEvidenceSettings();
        builder.Services.AddScoped<IEvidenceMetadata, DatabaseEvidenceMetadata>();

        builder.Services.AddModbotAuth();
        builder.Services.AddScoped<IMailRelay, DemoMailRelay>();

        builder.Services.AddClientApi();
        builder.Services.AddModbotApi();
        builder.Services.AddWebhookDelivery();
        builder.Services.AddEmailQueue();
        builder.Services.AddModbotDemo();

        // AddModbotVRChatSync is deliberately absent. That is the whole point of the test.
        return builder.Build();
    }
}
