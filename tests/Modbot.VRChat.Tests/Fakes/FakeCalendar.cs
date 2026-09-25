using System.Net;
using System.Runtime.CompilerServices;
using NSubstitute;
using VRChat.API.Api;
using VRChat.API.Client;
using VRChat.API.Model;

namespace Modbot.VRChat.Tests.Fakes;

/// <summary>
/// The group calendar writes -- create, update, delete -- recorded in order, answering 200 unless a
/// test queues another status; and the group's list of events, which every create that went through
/// is added to.
/// </summary>
public sealed class FakeCalendar
{
    private readonly Queue<(HttpStatusCode Status, bool Saved, string Message)> _statuses = new();
    private int _nextId = 1;

    /// <summary>What the group's calendar holds, as the list call returns it.</summary>
    public List<CalendarEvent> OnVRChat { get; } = [];

    /// <summary>How many times the list was read.</summary>
    public int Lists { get; private set; }

    /// <summary>What the list call answers with.</summary>
    public HttpStatusCode ListStatus { get; set; } = HttpStatusCode.OK;

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
        _statuses.Enqueue((status, false, "no"));
        return this;
    }

    /// <summary>
    /// Makes the next create save the event and still answer with this status, the way VRChat's
    /// 500 can.
    /// </summary>
    public FakeCalendar SaveButAnswer(HttpStatusCode status)
    {
        _statuses.Enqueue((status, true, "no"));
        return this;
    }

    /// <summary>Makes the next write answer with this status and VRChat's refusal body.</summary>
    public FakeCalendar Refuse(HttpStatusCode status, string message)
    {
        _statuses.Enqueue((status, false, message));
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
                var (status, saved, message) = NextStatus();
                var request = call.ArgAt<CreateCalendarEventRequest>(1);

                if (status != HttpStatusCode.OK && !saved)
                    return Task.FromResult(Failure<CalendarEvent>(status, message));

                Creates.Add(request);
                var created = Event($"cal_{_nextId++}", request.Title, request.StartsAt);
                OnVRChat.Add(created);

                return Task.FromResult(status == HttpStatusCode.OK ? Ok(created) : Failure<CalendarEvent>(status, message));
            });

        calendar
            .UpdateGroupCalendarEventWithHttpInfoAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<UpdateCalendarEventRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                Calls++;
                var (status, _, message) = NextStatus();
                if (status != HttpStatusCode.OK)
                    return Task.FromResult(Failure<CalendarEvent>(status, message));

                var id = call.ArgAt<string>(1);
                Updates.Add((id, call.ArgAt<UpdateCalendarEventRequest>(2)));
                return Task.FromResult(Ok(Event(id)));
            });

        calendar
            .DeleteGroupCalendarEventWithHttpInfoAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                Calls++;
                var (status, _, message) = NextStatus();
                if (status != HttpStatusCode.OK)
                    return Task.FromResult(Failure<Success>(status, message));

                var id = call.ArgAt<string>(1);
                Deletes.Add(id);
                OnVRChat.RemoveAll(e => e.Id == id);
                return Task.FromResult(new ApiResponse<Success>(HttpStatusCode.OK, new Multimap<string, string>(), new Success(), "{}"));
            });

        calendar
            .GetGroupCalendarEventsWithHttpInfoAsync(
                Arg.Any<string>(), Arg.Any<DateTime?>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<int?>(),
                Arg.Any<DateTime?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                Lists++;
                if (ListStatus != HttpStatusCode.OK)
                    return Task.FromResult(Failure<PaginatedCalendarEventList>(ListStatus));

                var page = (PaginatedCalendarEventList)RuntimeHelpers.GetUninitializedObject(typeof(PaginatedCalendarEventList));
                page.Results = [.. OnVRChat];
                return Task.FromResult(new ApiResponse<PaginatedCalendarEventList>(HttpStatusCode.OK, new Multimap<string, string>(), page, "{}"));
            });

        return calendar;
    }

    private (HttpStatusCode Status, bool Saved, string Message) NextStatus() =>
        _statuses.Count > 0 ? _statuses.Dequeue() : (HttpStatusCode.OK, false, "no");

    private static ApiResponse<CalendarEvent> Ok(CalendarEvent body) =>
        new(HttpStatusCode.OK, new Multimap<string, string>(), body, "{}");

    private static ApiResponse<T> Failure<T>(HttpStatusCode status, string message = "no") =>
        new(status, new Multimap<string, string>(), default!, $"{{\"error\":{{\"message\":\"{message}\"}}}}");

    /// <summary>A calendar event with only what the tests read, built without its many required fields.</summary>
    private static CalendarEvent Event(string id, string? title = null, DateTime startsAt = default)
    {
        var body = (CalendarEvent)RuntimeHelpers.GetUninitializedObject(typeof(CalendarEvent));
        body.Id = id;
        body.Title = title!;
        body.StartsAt = startsAt;
        return body;
    }
}
