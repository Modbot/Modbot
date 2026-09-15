using System.Net;
using System.Runtime.CompilerServices;
using NSubstitute;
using VRChat.API.Api;
using VRChat.API.Client;
using VRChat.API.Model;

namespace Modbot.VRChat.Tests.Fakes;

/// <summary>
/// The group calendar writes -- create, update, delete -- recorded in order, answering 200 unless a
/// test queues another status.
/// </summary>
public sealed class FakeCalendar
{
    private readonly Queue<HttpStatusCode> _statuses = new();
    private int _nextId = 1;

    /// <summary>Every create body, in order.</summary>
    public List<CreateCalendarEventRequest> Creates { get; } = [];

    /// <summary>Every update: the VRChat event id and the body.</summary>
    public List<(string Id, UpdateCalendarEventRequest Body)> Updates { get; } = [];

    /// <summary>Every delete, by VRChat event id.</summary>
    public List<string> Deletes { get; } = [];

    /// <summary>Every write of any kind, in order, including ones answered with a failure.</summary>
    public int Calls { get; private set; }

    /// <summary>Makes the next write answer with this status instead of 200.</summary>
    public FakeCalendar Answer(HttpStatusCode status)
    {
        _statuses.Enqueue(status);
        return this;
    }

    public ICalendarApi Build()
    {
        var calendar = Substitute.For<ICalendarApi>();

        calendar
            .CreateGroupCalendarEventWithHttpInfoAsync(Arg.Any<string>(), Arg.Any<CreateCalendarEventRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                Calls++;
                var status = NextStatus();
                if (status != HttpStatusCode.OK)
                    return Task.FromResult(Failure<CalendarEvent>(status));

                Creates.Add(call.ArgAt<CreateCalendarEventRequest>(1));
                return Task.FromResult(Ok(Event($"cal_{_nextId++}")));
            });

        calendar
            .UpdateGroupCalendarEventWithHttpInfoAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<UpdateCalendarEventRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                Calls++;
                var status = NextStatus();
                if (status != HttpStatusCode.OK)
                    return Task.FromResult(Failure<CalendarEvent>(status));

                var id = call.ArgAt<string>(1);
                Updates.Add((id, call.ArgAt<UpdateCalendarEventRequest>(2)));
                return Task.FromResult(Ok(Event(id)));
            });

        calendar
            .DeleteGroupCalendarEventWithHttpInfoAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                Calls++;
                var status = NextStatus();
                if (status != HttpStatusCode.OK)
                    return Task.FromResult(Failure<Success>(status));

                Deletes.Add(call.ArgAt<string>(1));
                return Task.FromResult(new ApiResponse<Success>(HttpStatusCode.OK, new Multimap<string, string>(), new Success(), "{}"));
            });

        return calendar;
    }

    private HttpStatusCode NextStatus() => _statuses.Count > 0 ? _statuses.Dequeue() : HttpStatusCode.OK;

    private static ApiResponse<CalendarEvent> Ok(CalendarEvent body) =>
        new(HttpStatusCode.OK, new Multimap<string, string>(), body, "{}");

    private static ApiResponse<T> Failure<T>(HttpStatusCode status) =>
        new(status, new Multimap<string, string>(), default!, "{\"error\":{\"message\":\"no\"}}");

    /// <summary>A calendar event with only its id, built without its many required fields.</summary>
    private static CalendarEvent Event(string id)
    {
        var body = (CalendarEvent)RuntimeHelpers.GetUninitializedObject(typeof(CalendarEvent));
        body.Id = id;
        return body;
    }
}
