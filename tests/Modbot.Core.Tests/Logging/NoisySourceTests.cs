using Modbot.Core.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Modbot.Core.Tests.Logging;

/// <summary>
/// The two frameworks that narrate every request, and what an operator sees of them.
/// </summary>
/// <remarks>
/// <para>
/// ASP.NET Core writes four Information lines for every request it serves, and the factory behind
/// every <c>HttpClient</c> writes four more for every request Modbot makes. Modbot makes a lot:
/// the VRChat gate paces itself at about one and a half calls a second, all day. Left alone that
/// is the application record buried in narration about requests, which is the complaint these
/// floors exist to answer.
/// </para>
/// <para>
/// They are floors and not deletions, which is the part worth pinning: asking for Debug brings
/// every line back. A change that silenced them outright would pass a test that only checked the
/// quiet case, so both directions are checked here.
/// </para>
/// </remarks>
public class NoisySourceTests
{
    private const string HttpClientSource = "System.Net.Http.HttpClient.vrchat.LogicalHandler";
    private const string AspNetSource = "Microsoft.AspNetCore.Hosting.Diagnostics";

    /// <summary>Everything a logger built at this level actually let through, by source.</summary>
    private static List<string> Kept(LogEventLevel level, LogEventLevel wrote)
    {
        var sink = new Collecting();

        using var logger = ModbotConsoleLog.Start("Modbot.Tests", level)
            .WriteTo.Sink(sink)
            .CreateLogger();

        foreach (var source in new[] { HttpClientSource, AspNetSource, "Modbot.VRChat.VRChatGate" })
            logger.ForContext(Constants.SourceContextPropertyName, source).Write(wrote, "a line");

        return sink.Sources;
    }

    [Fact]
    public void AtInformation_NeitherFrameworkNarratesEveryRequest()
    {
        var kept = Kept(LogEventLevel.Information, wrote: LogEventLevel.Information);

        Assert.DoesNotContain(HttpClientSource, kept);
        Assert.DoesNotContain(AspNetSource, kept);
    }

    /// <summary>Modbot's own record is the thing the floors exist to leave room for.</summary>
    [Fact]
    public void AtInformation_ModbotsOwnLinesStillCome()
        => Assert.Contains("Modbot.VRChat.VRChatGate", Kept(LogEventLevel.Information, wrote: LogEventLevel.Information));

    /// <summary>
    /// Asking for Debug asks for the detail, and gets all of it. This is the half a floor put in
    /// as a silence would fail.
    /// </summary>
    [Fact]
    public void AtDebug_EveryLineComesBack()
    {
        var kept = Kept(LogEventLevel.Debug, wrote: LogEventLevel.Information);

        Assert.Contains(HttpClientSource, kept);
        Assert.Contains(AspNetSource, kept);
    }

    /// <summary>
    /// A floor is not a mute: something going wrong in either framework is still reported at
    /// Information, because the floor it is held to is Warning.
    /// </summary>
    [Theory]
    [InlineData(LogEventLevel.Warning)]
    [InlineData(LogEventLevel.Error)]
    public void WhateverGoesWrongInThemIsStillSaid(LogEventLevel wrote)
    {
        var kept = Kept(LogEventLevel.Information, wrote);

        Assert.Contains(HttpClientSource, kept);
        Assert.Contains(AspNetSource, kept);
    }

    private sealed class Collecting : ILogEventSink
    {
        public List<string> Sources { get; } = [];

        public void Emit(LogEvent logEvent)
        {
            if (logEvent.Properties.TryGetValue(Constants.SourceContextPropertyName, out var source)
                && source is ScalarValue { Value: string name })
            {
                Sources.Add(name);
            }
        }
    }
}
