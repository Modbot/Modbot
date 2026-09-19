using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Analytics.Facts;
using Modbot.Api.Features.Audit;
using Modbot.Api.Features.Cases;
using Modbot.Api.Features.Notes;
using Modbot.Api.Tests.Features.Audit;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Notes;

/// <summary>
/// Notes about a person (notes design): writing one, reading them back in order, the two
/// permissions, the length cap, taking one back, and what all of it leaves in the audit log.
/// </summary>
/// <remarks>
/// A note is a fact and nothing else, so most of what is worth proving here is about the log: that
/// writing one appends exactly one fact, that taking one back appends a second rather than
/// changing the first, and that the log's own permission is the one that opens both.
/// </remarks>
[Collection(nameof(PostgresCollection))]
public class NoteTests
{
    private const string Person = "usr_troublemaker";

    private static readonly DateTimeOffset Day = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly PostgresFixture _db;

    public NoteTests(PostgresFixture db) => _db = db;

    // ── Helpers ────────────────────────────────────────────────────────────────────────────

    private static object Body(string userId, string text, string? platform = null)
        => new { userId, platform, text };

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
        => JsonSerializer.Deserialize<T>(await response.Content.ReadAsStringAsync(ct), Web)!;

    private static async Task<List<ModbotEvent>> FactsAboutAsync(
        ReadSurfaceTestHost host, string userId, CancellationToken ct)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        return await db.Events.AsNoTracking()
            .Where(e => e.SubjectId == userId)
            .OrderBy(e => e.Id)
            .ToListAsync(ct);
    }

    /// <summary>A note service bound to one scope, for the rules the HTTP surface cannot reach.</summary>
    private static NoteService ServiceIn(IServiceScope scope)
        => new(
            scope.ServiceProvider.GetRequiredService<ModbotContext>(),
            scope.ServiceProvider.GetRequiredService<IModbotClock>(),
            scope.ServiceProvider.GetRequiredService<IFactWriter>(),
            scope.ServiceProvider.GetRequiredService<EventPartitionMaintainer>());

    // ── Permissions ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WritingANote_NeedsItsOwnPermission_AndReadingTheLogIsNotEnough()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);
        host.Clock.UtcNow = Day;

        var reader = await host.SignedInAsync(ModbotPermissions.ViewAuditLog, ct);

        var refused = await host.PostJsonAsync("/api/notes", Body(Person, "Asked twice to stop."), reader, ct);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Empty(await FactsAboutAsync(host, Person, ct));

        var writer = await host.SignedInAsync(ModbotPermissions.WriteNotes | ModbotPermissions.ViewAuditLog, ct);
        var written = await host.PostJsonAsync("/api/notes", Body(Person, "Asked twice to stop."), writer, ct);

        Assert.Equal(HttpStatusCode.OK, written.StatusCode);
    }

    [Fact]
    public async Task ReadingNotes_NeedsTheAuditLogPermission_AndWritingThemIsNotEnough()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);
        host.Clock.UtcNow = Day;

        // Writing a note does not let somebody read what everybody else has written about a
        // person: the notes are facts in the moderation log, and that log has its own door.
        var writer = await host.SignedInAsync(ModbotPermissions.WriteNotes, ct);

        var refused = await host.GetAsync($"/api/notes?userId={Person}", writer, ct);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        // Nor does the operational log, which is the other half of spec 5.9.4's split.
        var operator_ = await host.SignedInAsync(ModbotPermissions.ViewOperationalLog, ct);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await host.GetAsync($"/api/notes?userId={Person}", operator_, ct)).StatusCode);
    }

    // ── Writing and reading back ───────────────────────────────────────────────────────────

    [Fact]
    public async Task AWrittenNote_IsOneFact_NamingWhoWroteItAndWhoItIsAbout()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);
        host.Clock.UtcNow = Day;

        var cookie = await host.SignedInAsync(
            ModbotPermissions.WriteNotes | ModbotPermissions.ViewAuditLog, ct);

        var response = await host.PostJsonAsync("/api/notes", Body(Person, "  Asked twice to stop.  "), cookie, ct);
        var note = await ReadAsync<NoteView>(response, ct);

        Assert.Equal("Asked twice to stop.", note.Text);
        Assert.Equal(Person, note.SubjectId);
        Assert.Equal("VRChat", note.SubjectPlatform);
        Assert.False(note.Imported);
        Assert.False(note.TakenBack);
        Assert.NotNull(note.AuthorAccountId);

        var facts = await FactsAboutAsync(host, Person, ct);
        var fact = Assert.Single(facts);

        Assert.Equal(FactType.NoteAdded, fact.Type);
        Assert.Equal(FactPlatform.VRChat, fact.SubjectPlatform);
        Assert.Equal(FactPlatform.Modbot, fact.ActorPlatform);
        Assert.Equal(note.AuthorAccountId.ToString(), fact.ActorId);
        Assert.Equal(fact.Id, note.Id);

        // The words are in the payload twice on purpose: `text` is the note, and `description` is
        // the key every timeline reader and the Discord card already look in.
        using var payload = JsonDocument.Parse(fact.Data);
        Assert.Equal("Asked twice to stop.", payload.RootElement.GetProperty("text").GetString());
        Assert.Equal("Asked twice to stop.", payload.RootElement.GetProperty("description").GetString());
    }

    [Fact]
    public async Task Notes_ReadBackNewestFirst()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var cookie = await host.SignedInAsync(
            ModbotPermissions.WriteNotes | ModbotPermissions.ViewAuditLog, ct);

        foreach (var (minutes, text) in new[] { (0, "first"), (5, "second"), (10, "third") })
        {
            host.Clock.UtcNow = Day.AddMinutes(minutes);
            var written = await host.PostJsonAsync("/api/notes", Body(Person, text), cookie, ct);
            written.EnsureSuccessStatusCode();
        }

        var list = await host.GetJsonAsync<NoteListResponse>($"/api/notes?userId={Person}", cookie, ct);

        Assert.Equal(["third", "second", "first"], list.Notes.Select(n => n.Text));
        Assert.Equal(3, list.Standing);
        Assert.True(list.CanWrite);
    }

    [Fact]
    public async Task Notes_AreOnlyAboutThePersonTheyWereWrittenAbout()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);
        host.Clock.UtcNow = Day;

        var cookie = await host.SignedInAsync(
            ModbotPermissions.WriteNotes | ModbotPermissions.ViewAuditLog, ct);

        (await host.PostJsonAsync("/api/notes", Body(Person, "about them"), cookie, ct)).EnsureSuccessStatusCode();
        (await host.PostJsonAsync("/api/notes", Body("usr_somebody_else", "about someone else"), cookie, ct))
            .EnsureSuccessStatusCode();

        var list = await host.GetJsonAsync<NoteListResponse>($"/api/notes?userId={Person}", cookie, ct);

        Assert.Equal("about them", Assert.Single(list.Notes).Text);
    }

    [Fact]
    public async Task ANoteOnDiscord_IsNotANoteOnVRChat()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);
        host.Clock.UtcNow = Day;

        var cookie = await host.SignedInAsync(
            ModbotPermissions.WriteNotes | ModbotPermissions.ViewAuditLog, ct);

        // The same text as an id on both platforms, which is the case that would blur them.
        (await host.PostJsonAsync("/api/notes", Body("1234", "on Discord", "Discord"), cookie, ct))
            .EnsureSuccessStatusCode();

        var discord = await host.GetJsonAsync<NoteListResponse>("/api/notes?userId=1234&platform=Discord", cookie, ct);
        var vrchat = await host.GetJsonAsync<NoteListResponse>("/api/notes?userId=1234", cookie, ct);

        Assert.Equal("on Discord", Assert.Single(discord.Notes).Text);
        Assert.Empty(vrchat.Notes);
    }

    // ── What is refused ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ANoteTooLong_IsRefused_AndNothingIsWritten()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);
        host.Clock.UtcNow = Day;

        var cookie = await host.SignedInAsync(ModbotPermissions.WriteNotes, ct);

        var refused = await host.PostJsonAsync(
            "/api/notes", Body(Person, new string('x', NoteService.MaxTextLength + 1)), cookie, ct);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Empty(await FactsAboutAsync(host, Person, ct));

        // And one exactly at the cap goes through, so the boundary is where it says it is.
        var accepted = await host.PostJsonAsync(
            "/api/notes", Body(Person, new string('x', NoteService.MaxTextLength)), cookie, ct);

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
    }

    [Fact]
    public async Task AnEmptyNote_AndANoteAboutNobody_AreBothRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);
        host.Clock.UtcNow = Day;

        var cookie = await host.SignedInAsync(ModbotPermissions.WriteNotes, ct);

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await host.PostJsonAsync("/api/notes", Body(Person, "   "), cookie, ct)).StatusCode);

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await host.PostJsonAsync("/api/notes", Body("  ", "somebody"), cookie, ct)).StatusCode);
    }

    /// <summary>
    /// A legacy VRChat id follows no structure at all (foundation §3.1.1), so a note about one has
    /// to be writable. This is the test that stops somebody adding a "looks like a user id" check.
    /// </summary>
    [Fact]
    public async Task ANoteAboutALegacyId_IsWritten_BecauseIdsAreNeverChecked()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);
        host.Clock.UtcNow = Day;

        var cookie = await host.SignedInAsync(
            ModbotPermissions.WriteNotes | ModbotPermissions.ViewAuditLog, ct);

        const string legacy = "8JoV9XEdpo";

        (await host.PostJsonAsync("/api/notes", Body(legacy, "old account"), cookie, ct)).EnsureSuccessStatusCode();

        var list = await host.GetJsonAsync<NoteListResponse>($"/api/notes?userId={legacy}", cookie, ct);

        Assert.Equal("old account", Assert.Single(list.Notes).Text);
    }

    // ── Taking one back ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TakingANoteBack_LeavesBothFacts_AndMarksItOnTheList()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);
        host.Clock.UtcNow = Day;

        var cookie = await host.SignedInAsync(
            ModbotPermissions.WriteNotes | ModbotPermissions.ViewAuditLog, ct);

        var written = await ReadAsync<NoteView>(
            await host.PostJsonAsync("/api/notes", Body(Person, "Wrong person."), cookie, ct), ct);

        host.Clock.UtcNow = Day.AddHours(1);

        var back = await host.PostJsonAsync($"/api/notes/{written.Id}/take-back", null, cookie, ct);
        var view = await ReadAsync<NoteView>(back, ct);

        Assert.True(view.TakenBack);
        Assert.Equal(Day.AddHours(1), view.TakenBackAt);
        Assert.False(view.CanTakeBack);

        // Nothing was edited and nothing was deleted: two facts, the first untouched.
        var facts = await FactsAboutAsync(host, Person, ct);

        Assert.Equal([FactType.NoteAdded, FactType.NoteTakenBack], facts.Select(f => f.Type));
        Assert.Equal("Wrong person.", JsonDocument.Parse(facts[0].Data).RootElement.GetProperty("text").GetString());
        Assert.Equal(
            written.Id,
            JsonDocument.Parse(facts[1].Data).RootElement.GetProperty("noteFactId").GetInt64());

        // The list still shows it, marked, because a note written and withdrawn is not the same
        // thing as one nobody ever wrote.
        var list = await host.GetJsonAsync<NoteListResponse>($"/api/notes?userId={Person}", cookie, ct);

        Assert.True(Assert.Single(list.Notes).TakenBack);
        Assert.Equal(0, list.Standing);
    }

    [Fact]
    public async Task TakingBackANoteTwice_ChangesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);
        host.Clock.UtcNow = Day;

        var cookie = await host.SignedInAsync(
            ModbotPermissions.WriteNotes | ModbotPermissions.ViewAuditLog, ct);

        var written = await ReadAsync<NoteView>(
            await host.PostJsonAsync("/api/notes", Body(Person, "Wrong person."), cookie, ct), ct);

        (await host.PostJsonAsync($"/api/notes/{written.Id}/take-back", null, cookie, ct)).EnsureSuccessStatusCode();
        (await host.PostJsonAsync($"/api/notes/{written.Id}/take-back", null, cookie, ct)).EnsureSuccessStatusCode();

        var facts = await FactsAboutAsync(host, Person, ct);

        Assert.Equal(2, facts.Count);
    }

    [Fact]
    public async Task TakingBackANoteThatDoesNotExist_Is404()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);
        host.Clock.UtcNow = Day;

        var cookie = await host.SignedInAsync(ModbotPermissions.WriteNotes, ct);

        var missing = await host.PostJsonAsync("/api/notes/999999/take-back", null, cookie, ct);

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    /// <summary>
    /// The author may take back what they wrote even after losing the permission, and a stranger
    /// who never had it may not. Exercised through the service, because the rule is about who
    /// wrote the note and a second sign-in makes a different account.
    /// </summary>
    [Fact]
    public async Task TakingANoteBack_IsOpenToItsAuthor_AndToNobodyElseWithoutThePermission()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);
        host.Clock.UtcNow = Day;

        var author = new Caller(Guid.CreateVersion7(), "Gunner24", ModbotPermissions.WriteNotes);
        var stranger = new Caller(Guid.CreateVersion7(), "Passerby", ModbotPermissions.ViewAuditLog);
        var sinceDemoted = author with { Held = ModbotPermissions.ViewAuditLog };

        using var scope = host.Services.CreateScope();
        var notes = ServiceIn(scope);

        var written = await notes.WriteAsync(new WriteNoteRequest(Person, null, "Mine."), author, ct);

        var refused = await Assert.ThrowsAsync<NoteRefused>(
            () => notes.TakeBackAsync(written.Id, stranger, ct));

        Assert.Equal(403, refused.Status);

        var back = await notes.TakeBackAsync(written.Id, sinceDemoted, ct);

        Assert.True(back.TakenBack);
    }

    [Fact]
    public async Task WritingANote_ThroughTheService_NeedsThePermission()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);
        host.Clock.UtcNow = Day;

        using var scope = host.Services.CreateScope();
        var notes = ServiceIn(scope);

        var refused = await Assert.ThrowsAsync<NoteRefused>(
            () => notes.WriteAsync(
                new WriteNoteRequest(Person, null, "Not allowed."),
                new Caller(Guid.CreateVersion7(), "Passerby", ModbotPermissions.ViewAuditLog),
                ct));

        Assert.Equal(403, refused.Status);
    }

    // ── What it leaves in the audit log ────────────────────────────────────────────────────

    [Fact]
    public async Task ANote_SurvivesInTheAuditLog_ForWhoeverMayReadThatLog()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);
        host.Clock.UtcNow = Day;

        var writer = await host.SignedInAsync(
            ModbotPermissions.WriteNotes | ModbotPermissions.ViewAuditLog, ct);

        var written = await ReadAsync<NoteView>(
            await host.PostJsonAsync("/api/notes", Body(Person, "Asked twice to stop."), writer, ct), ct);

        host.Clock.UtcNow = Day.AddHours(1);
        (await host.PostJsonAsync($"/api/notes/{written.Id}/take-back", null, writer, ct)).EnsureSuccessStatusCode();

        // Somebody who may read the moderation log, and cannot write a note, sees both entries.
        var reader = await host.SignedInAsync(ModbotPermissions.ViewAuditLog, ct);

        var page = await host.GetJsonAsync<AuditPage>(
            $"/api/audit?subject={Person}&subjectPlatform=VRChat", reader, ct);

        Assert.Contains(page.Entries, e => e.Type == FactType.NoteAdded);
        Assert.Contains(page.Entries, e => e.Type == FactType.NoteTakenBack);

        // Both are moderation history, so the operational log does not carry them.
        var operator_ = await host.SignedInAsync(ModbotPermissions.ViewOperationalLog, ct);

        var operational = await host.GetJsonAsync<AuditPage>(
            $"/api/audit?subject={Person}&subjectPlatform=VRChat", operator_, ct);

        Assert.DoesNotContain(operational.Entries, e => e.Type == FactType.NoteAdded);
        Assert.DoesNotContain(operational.Entries, e => e.Type == FactType.NoteTakenBack);
    }

    /// <summary>
    /// A note that came in from an import is the same fact type, and the list shows it beside the
    /// ones written here rather than in a second place nobody looks (notes design §3.1).
    /// </summary>
    [Fact]
    public async Task AnImportedNote_ShowsOnTheSameList_AndSaysWhereItCameFrom()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);
        host.Clock.UtcNow = Day;

        await host.WriteFactAsync(
            new FactRecord
            {
                Type = FactType.NoteAdded,
                OccurredAt = Day.AddYears(-2),
                SubjectPlatform = FactPlatform.VRChat,
                SubjectId = Person,
                Source = FactSource.Manual,
                Data = new System.Text.Json.Nodes.JsonObject
                {
                    ["text"] = "Warned on the old bot.",
                    ["importId"] = Guid.CreateVersion7().ToString(),
                    ["importSource"] = "old-bot",
                    ["actorDisplayName"] = "someone who has left",
                },
            },
            ct);

        var cookie = await host.SignedInAsync(
            ModbotPermissions.WriteNotes | ModbotPermissions.ViewAuditLog, ct);

        (await host.PostJsonAsync("/api/notes", Body(Person, "And again here."), cookie, ct))
            .EnsureSuccessStatusCode();

        var list = await host.GetJsonAsync<NoteListResponse>($"/api/notes?userId={Person}", cookie, ct);

        Assert.Equal(["And again here.", "Warned on the old bot."], list.Notes.Select(n => n.Text));
        Assert.False(list.Notes[0].Imported);
        Assert.True(list.Notes[1].Imported);
        Assert.Null(list.Notes[1].AuthorAccountId);
        Assert.Equal("someone who has left", list.Notes[1].AuthorName);
    }
}
