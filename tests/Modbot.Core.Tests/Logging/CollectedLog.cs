using Serilog.Core;
using Serilog.Events;

namespace Modbot.Core.Tests.Logging;

/// <summary>
/// A sink that keeps what it is given, so a test can ask which events a logger let through.
/// </summary>
internal sealed class CollectedLog : ILogEventSink
{
    private readonly List<LogEvent> _events = [];

    public IReadOnlyList<LogEvent> Events => _events;

    public void Emit(LogEvent logEvent) => _events.Add(logEvent);
}
