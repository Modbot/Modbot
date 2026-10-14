using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Auth;
using Modbot.Api.Features.People;
using Modbot.Api.Features.Users;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.Api.Features.StaffTrials;

/// <summary>One line of a trial's checklist.</summary>
/// <param name="Kind">One of <see cref="StaffTrialItemKinds"/>.</param>
/// <param name="Answer">The trial mod's written answer, for a task.</param>
/// <param name="SignedOffAt">When the buddy signed the line off, for a sign-off line.</param>
/// <param name="SignedOffBy">The username of whoever signed it off.</param>
public sealed record StaffTrialItemView(
    Guid Id,
    string Kind,
    string Text,
    string? Answer,
    DateTimeOffset? AnsweredAt,
    DateTimeOffset? SignedOffAt,
    string? SignedOffBy);

/// <summary>One new staff member's trial, with what the caller may do to it.</summary>
/// <param name="CanAnswer">The caller is the one on trial, so the tasks are theirs to fill in.</param>
/// <param name="CanSignOff">The caller is the buddy, or manages accounts, and is not the one on trial.</param>
/// <param name="CanManage">The caller may change the buddy and take the trial off the account.</param>
public sealed record StaffTrialView(
    Guid AccountId,
    string Username,
    Guid BuddyId,
    string BuddyName,
    DateTimeOffset StartedAt,
    string? BuddyNote,
    DateTimeOffset? BuddyNoteAt,
    IReadOnlyList<StaffTrialItemView> Items,
    bool CanAnswer,
    bool CanSignOff,
    bool CanManage);

/// <summary>An account's trial, or none yet.</summary>
/// <param name="CanStart">The caller may start one.</param>
public sealed record StaffTrialResponse(StaffTrialView? Trial, bool CanStart);

/// <summary>The caller's own trial, and the trials they are buddy to.</summary>
public sealed record MyStaffTrialsResponse(StaffTrialView? Own, IReadOnlyList<StaffTrialView> AsBuddy);

public sealed record StartStaffTrialBody(Guid AccountId, Guid BuddyId);

public sealed record StaffTrialBuddyBody(Guid BuddyId);

public sealed record StaffTrialAnswerBody(string? Answer);

public sealed record StaffTrialSignOffBody(bool SignedOff);

public sealed record StaffTrialNoteBody(string? Note);

/// <summary>
/// A new staff member's checklist (the new staff tracker). One trial per account: a buddy, a start date,
/// and the lines copied from Settings when it began.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Who may do what.</strong> The one on trial fills in the tasks, and only they do. The buddy
/// signs the other lines off and writes the note; somebody who manages accounts may do both of those in
/// the buddy's place, so a buddy who has left does not hold a trial up. Nobody signs off their own
/// trial. Starting a trial, changing the buddy and taking a trial off need Manage users.
/// </para>
/// <para>
/// <strong>Who may read.</strong> The one on trial, their buddy, and whoever may see the account side of
/// a person (<see cref="PersonSight.Account"/>): the checklist is part of that profile.
/// </para>
/// </remarks>
public static class StaffTrialEndpoints
{
    public static IEndpointRouteBuilder MapStaffTrials(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/staff-trials").WithTags("Users").RequireAuthorization();

        group.MapGet("/mine", async (
                HttpContext http,
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
            {
                var me = Me(http);

                var trials = await db.StaffTrials
                    .Include(t => t.Items)
                    .Where(t => t.AccountId == me || t.BuddyId == me)
                    .OrderBy(t => t.StartedAt)
                    .ToListAsync(ct);

                var views = await ViewsAsync(db, trials, http, ct);

                return Results.Ok(new MyStaffTrialsResponse(
                    views.FirstOrDefault(v => v.AccountId == me),
                    views.Where(v => v.AccountId != me).ToList()));
            })
            .WithName("MyStaffTrials")
            .WithSummary("Get my trial and the trials I am buddy to")
            .WithDescription("The signed-in person's own checklist, if they are on a trial, and the checklists of the people they are buddy to.")
            .Produces<MyStaffTrialsResponse>()
            .Produces(StatusCodes.Status401Unauthorized);

        group.MapGet("/{accountId:guid}", async (
                Guid accountId,
                HttpContext http,
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
            {
                var trial = await LoadAsync(db, accountId, ct);
                var held = ModbotAuth.PermissionsOf(http.User);
                var canStart = ModbotAuth.Allows(held, ModbotPermissions.ManageUsers);

                if (trial is null)
                {
                    // Told whether there is one only to whoever could be told it exists.
                    if (Me(http) != accountId && !PersonSight.Of(held).Account)
                        return Results.Forbid();

                    return Results.Ok(new StaffTrialResponse(null, canStart));
                }

                if (!MaySee(http, trial))
                    return Results.Forbid();

                return Results.Ok(new StaffTrialResponse((await ViewsAsync(db, [trial], http, ct))[0], canStart));
            })
            .WithName("GetStaffTrial")
            .WithSummary("Get an account's trial")
            .WithDescription("The account's checklist, or none yet. For the person on trial, their buddy, and anyone who may see the account side of a person.")
            .Produces<StaffTrialResponse>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPost("/", async (
                HttpContext http,
                [FromBody] StartStaffTrialBody body,
                [FromServices] ModbotContext db,
                [FromServices] AccountFacts facts,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                if (!Manages(http))
                    return Results.Forbid();

                var account = await db.Users.FirstOrDefaultAsync(u => u.Id == body.AccountId, ct);
                if (account is null || account.IsDeleted)
                    return Results.NotFound(new { error = "That account does not exist." });

                if (await BuddyProblemAsync(db, account.Id, body.BuddyId, ct) is { } problem)
                    return problem;

                if (await db.StaffTrials.AnyAsync(t => t.AccountId == account.Id, ct))
                    return Results.Conflict(new { error = "That account already has a trial." });

                var buddy = await db.Users.FirstAsync(u => u.Id == body.BuddyId, ct);
                var settings = await db.GetSettingsAsync(ct);
                var actor = Actor.Of(http);

                var trial = new StaffTrial
                {
                    AccountId = account.Id,
                    BuddyId = buddy.Id,
                    StartedAt = clock.UtcNow,
                    StartedById = actor!.Value.Id,
                };

                var position = 0;
                foreach (var text in settings.NewStaffTasks ?? [])
                    trial.Items.Add(new StaffTrialItem { Kind = StaffTrialItemKinds.Task, Text = text, Position = position++ });

                position = 0;
                foreach (var text in settings.NewStaffSignOffs ?? [])
                    trial.Items.Add(new StaffTrialItem { Kind = StaffTrialItemKinds.SignOff, Text = text, Position = position++ });

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                db.StaffTrials.Add(trial);
                await db.SaveChangesAsync(ct);

                await facts.RecordAsync(
                    FactType.StaffTrialStarted,
                    account,
                    actor,
                    new JsonObject { ["buddy"] = buddy.Username, ["buddyId"] = buddy.Id.ToString() },
                    ct);

                await transaction.CommitAsync(ct);

                return Results.Ok((await ViewsAsync(db, [trial], http, ct))[0]);
            })
            .WithName("StartStaffTrial")
            .WithSummary("Start a trial")
            .WithDescription("Give an account a checklist and a buddy. The lines are copied from Settings now. Needs Manage users.")
            .Accepts<StartStaffTrialBody>("application/json")
            .Produces<StaffTrialView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);

        group.MapPut("/{accountId:guid}/buddy", async (
                Guid accountId,
                HttpContext http,
                [FromBody] StaffTrialBuddyBody body,
                [FromServices] ModbotContext db,
                [FromServices] AccountFacts facts,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                if (!Manages(http))
                    return Results.Forbid();

                var trial = await LoadAsync(db, accountId, ct);
                if (trial is null)
                    return Results.NotFound(new { error = "That account has no trial." });

                if (trial.BuddyId == body.BuddyId)
                    return Results.Ok((await ViewsAsync(db, [trial], http, ct))[0]);

                if (await BuddyProblemAsync(db, accountId, body.BuddyId, ct) is { } problem)
                    return problem;

                var buddy = await db.Users.FirstAsync(u => u.Id == body.BuddyId, ct);
                var account = await db.Users.FirstAsync(u => u.Id == accountId, ct);

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                trial.BuddyId = buddy.Id;
                await db.SaveChangesAsync(ct);

                await facts.RecordAsync(
                    FactType.StaffTrialBuddyChanged,
                    account,
                    Actor.Of(http),
                    new JsonObject { ["buddy"] = buddy.Username, ["buddyId"] = buddy.Id.ToString() },
                    ct);

                await transaction.CommitAsync(ct);

                return Results.Ok((await ViewsAsync(db, [trial], http, ct))[0]);
            })
            .WithName("SetStaffTrialBuddy")
            .WithSummary("Change a trial's buddy")
            .WithDescription("Needs Manage users. Sign-offs already given stay as they are.")
            .Accepts<StaffTrialBuddyBody>("application/json")
            .Produces<StaffTrialView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        group.MapDelete("/{accountId:guid}", async (
                Guid accountId,
                HttpContext http,
                [FromServices] ModbotContext db,
                [FromServices] AccountFacts facts,
                CancellationToken ct) =>
            {
                if (!Manages(http))
                    return Results.Forbid();

                var trial = await LoadAsync(db, accountId, ct);
                if (trial is null)
                    return Results.NotFound(new { error = "That account has no trial." });

                var account = await db.Users.FirstAsync(u => u.Id == accountId, ct);
                var buddyName = await db.Users.Where(u => u.Id == trial.BuddyId).Select(u => u.Username).FirstOrDefaultAsync(ct);

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                db.StaffTrials.Remove(trial);
                await db.SaveChangesAsync(ct);

                await facts.RecordAsync(
                    FactType.StaffTrialRemoved,
                    account,
                    Actor.Of(http),
                    new JsonObject { ["buddy"] = buddyName },
                    ct);

                await transaction.CommitAsync(ct);

                return Results.NoContent();
            })
            .WithName("RemoveStaffTrial")
            .WithSummary("Take a trial off an account")
            .WithDescription("Deletes the checklist with its answers and sign-offs. Needs Manage users.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        group.MapPut("/{accountId:guid}/items/{itemId:guid}/answer", async (
                Guid accountId,
                Guid itemId,
                HttpContext http,
                [FromBody] StaffTrialAnswerBody body,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                // Only the one on trial writes their answers.
                if (Me(http) != accountId)
                    return Results.Forbid();

                var trial = await LoadAsync(db, accountId, ct);
                var item = trial?.Items.FirstOrDefault(i => i.Id == itemId && i.Kind == StaffTrialItemKinds.Task);
                if (trial is null || item is null)
                    return Results.NotFound(new { error = "That task does not exist." });

                var answer = body.Answer?.Trim();
                if (answer is { Length: > StaffTrialLimits.AnswerLength })
                    return Results.BadRequest(new { error = $"An answer is at most {StaffTrialLimits.AnswerLength} characters." });

                if (string.IsNullOrEmpty(answer))
                    answer = null;

                if (item.Answer != answer)
                {
                    item.Answer = answer;
                    item.AnsweredAt = answer is null ? null : clock.UtcNow;
                    await db.SaveChangesAsync(ct);
                }

                return Results.Ok((await ViewsAsync(db, [trial], http, ct))[0]);
            })
            .WithName("AnswerStaffTrialTask")
            .WithSummary("Write the answer to a task")
            .WithDescription("Only the person on the trial. An empty answer clears it.")
            .Accepts<StaffTrialAnswerBody>("application/json")
            .Produces<StaffTrialView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        group.MapPut("/{accountId:guid}/items/{itemId:guid}/sign-off", async (
                Guid accountId,
                Guid itemId,
                HttpContext http,
                [FromBody] StaffTrialSignOffBody body,
                [FromServices] ModbotContext db,
                [FromServices] AccountFacts facts,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var trial = await LoadAsync(db, accountId, ct);
                if (trial is null)
                    return Results.NotFound(new { error = "That account has no trial." });

                if (!MaySign(http, trial))
                    return Results.Forbid();

                var item = trial.Items.FirstOrDefault(i => i.Id == itemId && i.Kind == StaffTrialItemKinds.SignOff);
                if (item is null)
                    return Results.NotFound(new { error = "That line does not exist." });

                if ((item.SignedOffAt is not null) != body.SignedOff)
                {
                    var account = await db.Users.FirstAsync(u => u.Id == accountId, ct);
                    var actor = Actor.Of(http);

                    await using var transaction = await db.Database.BeginTransactionAsync(ct);

                    item.SignedOffAt = body.SignedOff ? clock.UtcNow : null;
                    item.SignedOffById = body.SignedOff ? actor!.Value.Id : null;
                    await db.SaveChangesAsync(ct);

                    await facts.RecordAsync(
                        FactType.StaffTrialSignedOff,
                        account,
                        actor,
                        new JsonObject
                        {
                            ["item"] = item.Text,
                            ["action"] = body.SignedOff ? "signed-off" : "taken-back",
                        },
                        ct);

                    await transaction.CommitAsync(ct);
                }

                return Results.Ok((await ViewsAsync(db, [trial], http, ct))[0]);
            })
            .WithName("SignOffStaffTrialLine")
            .WithSummary("Sign a line off, or take the sign-off back")
            .WithDescription("The buddy, or anyone who manages users, but never the person on the trial.")
            .Accepts<StaffTrialSignOffBody>("application/json")
            .Produces<StaffTrialView>()
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        group.MapPut("/{accountId:guid}/note", async (
                Guid accountId,
                HttpContext http,
                [FromBody] StaffTrialNoteBody body,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var trial = await LoadAsync(db, accountId, ct);
                if (trial is null)
                    return Results.NotFound(new { error = "That account has no trial." });

                if (!MaySign(http, trial))
                    return Results.Forbid();

                var note = body.Note?.Trim();
                if (note is { Length: > StaffTrialLimits.NoteLength })
                    return Results.BadRequest(new { error = $"The note is at most {StaffTrialLimits.NoteLength} characters." });

                if (string.IsNullOrEmpty(note))
                    note = null;

                if (trial.BuddyNote != note)
                {
                    trial.BuddyNote = note;
                    trial.BuddyNoteAt = note is null ? null : clock.UtcNow;
                    await db.SaveChangesAsync(ct);
                }

                return Results.Ok((await ViewsAsync(db, [trial], http, ct))[0]);
            })
            .WithName("SetStaffTrialNote")
            .WithSummary("Write the buddy's note")
            .WithDescription("What the buddy says about how the trial went. The buddy, or anyone who manages users, but never the person on the trial. An empty note clears it.")
            .Accepts<StaffTrialNoteBody>("application/json")
            .Produces<StaffTrialView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        return app;
    }

    private static Guid Me(HttpContext http) => ModbotAuth.UserIdOf(http.User) ?? Guid.Empty;

    private static bool Manages(HttpContext http)
        => ModbotAuth.Allows(ModbotAuth.PermissionsOf(http.User), ModbotPermissions.ManageUsers);

    private static bool MaySee(HttpContext http, StaffTrial trial)
    {
        var me = Me(http);
        return me == trial.AccountId || me == trial.BuddyId || PersonSight.Of(ModbotAuth.PermissionsOf(http.User)).Account;
    }

    /// <summary>The buddy, or whoever manages users, but never the person on trial.</summary>
    private static bool MaySign(HttpContext http, StaffTrial trial)
    {
        var me = Me(http);
        return me != trial.AccountId && (me == trial.BuddyId || Manages(http));
    }

    private static Task<StaffTrial?> LoadAsync(ModbotContext db, Guid accountId, CancellationToken ct)
        => db.StaffTrials.Include(t => t.Items).FirstOrDefaultAsync(t => t.AccountId == accountId, ct);

    /// <summary>Why this account cannot be the buddy, as a response; null when it can.</summary>
    private static async Task<IResult?> BuddyProblemAsync(ModbotContext db, Guid accountId, Guid buddyId, CancellationToken ct)
    {
        if (buddyId == accountId)
            return Results.BadRequest(new { error = "Nobody is their own buddy." });

        var buddy = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == buddyId, ct);
        if (buddy is null || buddy.IsDeleted || buddy.IsDisabled)
            return Results.BadRequest(new { error = "Pick an account that is turned on." });

        return null;
    }

    private static async Task<List<StaffTrialView>> ViewsAsync(
        ModbotContext db,
        IReadOnlyList<StaffTrial> trials,
        HttpContext http,
        CancellationToken ct)
    {
        var ids = trials
            .SelectMany(t => new[] { t.AccountId, t.BuddyId }.Concat(t.Items.Where(i => i.SignedOffById is not null).Select(i => i.SignedOffById!.Value)))
            .Distinct()
            .ToList();

        var names = await db.Users.AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.Username, ct);

        var me = Me(http);
        var manages = Manages(http);

        return
        [
            .. trials.Select(t => new StaffTrialView(
                t.AccountId,
                names.GetValueOrDefault(t.AccountId, string.Empty),
                t.BuddyId,
                names.GetValueOrDefault(t.BuddyId, string.Empty),
                t.StartedAt,
                t.BuddyNote,
                t.BuddyNoteAt,
                [
                    .. t.Items
                        .OrderBy(i => i.Kind == StaffTrialItemKinds.Task ? 0 : 1)
                        .ThenBy(i => i.Position)
                        .Select(i => new StaffTrialItemView(
                            i.Id,
                            i.Kind,
                            i.Text,
                            i.Answer,
                            i.AnsweredAt,
                            i.SignedOffAt,
                            i.SignedOffById is { } by ? names.GetValueOrDefault(by) : null)),
                ],
                me == t.AccountId,
                me != t.AccountId && (me == t.BuddyId || manages),
                manages)),
        ];
    }
}
