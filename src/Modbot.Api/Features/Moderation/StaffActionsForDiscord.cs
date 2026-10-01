using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Analytics.Facts;
using Modbot.Api.Features.Cases;
using Modbot.Api.Features.Notes;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Time;
using Modbot.Evidence.Health;
using Modbot.Evidence.Options;
using Modbot.Evidence.Storage;
using Modbot.VRChat.Moderation;
using Modbot.VRChat.Users;

namespace Modbot.Api.Features.Moderation;

/// <summary>
/// The bot's way into the moderation and note services: the same services, built the same way the
/// endpoints build them, with the staff account the bot resolved as the caller (acting from Discord
/// design §5).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Built as the endpoint builds it, piece for piece.</strong> Kick, ban and unban get the
/// case-file writer, the profile refresh and the linked Discord bans, as
/// <see cref="ModerationEndpoints"/> gives them; approve and reject get the join request reader, as
/// the request endpoints do. A ban from Discord is then the same ban, the same fact and the same
/// case file as a ban from the web app, and the linked Discord account is banned the same way.
/// </para>
/// <para>
/// <strong>The permission is checked here too</strong>, with the web app's rule (Administrator may
/// do everything, otherwise every flag required). The bot checks before it shows a form; this is
/// the check that stands if a caller ever forgets.
/// </para>
/// <para>
/// The optional pieces are read from the container exactly as the endpoints' <c>[FromServices]</c>
/// read them: absent in a host that did not register them, and the answer then says the deployment
/// is not set up to act.
/// </para>
/// </remarks>
public sealed class StaffActionsForDiscord : IStaffActions
{
    private const string NotSetUp = "This deployment is not set up to act in VRChat.";

    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly IServiceProvider _services;

    public StaffActionsForDiscord(ModbotContext db, IModbotClock clock, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(services);

        _db = db;
        _clock = clock;
        _services = services;
    }

    /// <summary>The permission each action needs, as the endpoints require it.</summary>
    public static ModbotPermissions? Requires(string action) => action switch
    {
        ModerationActionService.Kick => ModbotPermissions.Kick,
        ModerationActionService.Ban => ModbotPermissions.Ban,
        ModerationActionService.Unban => ModbotPermissions.Unban,
        ModerationActionService.Approve or ModerationActionService.Reject => ModbotPermissions.AnswerJoinRequests,
        _ => null,
    };

    public async Task<StaffReasons> ReasonsAsync(string action, CancellationToken ct = default)
    {
        var use = ReasonUses.ForAction(action);
        if (use == ReasonUse.None)
            return StaffReasons.None;

        var requireOne = await _db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => s.RequireModerationClassification)
            .FirstOrDefaultAsync(ct);

        var all = await BanReasonList.AllAsync(_db, _clock, ct);

        // The web app's own filter (moderationActions.ts reasonsFor) and the service's own rule for
        // when one is required (ModerationActionService.ReasonsAsync).
        var offered = all
            .Where(r => r.IsActive && r.UsedFor.HasFlag(use))
            .Select(r => new StaffReason(r.Id, r.Label, r.Description, r.NeedsWrittenReason))
            .ToList();

        return new StaffReasons(offered, action == ModerationActionService.Ban || requireOne);
    }

    public async Task<string?> CheckAsync(
        string action, string vrchatUserId, IReadOnlyList<Guid> reasonIds, string note, StaffMember by, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(by);

        if (Refusal(action, by) is { } refusal)
            return refusal;

        if (Service(action) is not { } service)
            return NotSetUp;

        try
        {
            await service.CheckAsync(action, new ModerationActionRequest(vrchatUserId, string.Empty, reasonIds, note), ct);
            return null;
        }
        catch (ModerationRefused refused)
        {
            return refused.Message;
        }
    }

    public async Task<StaffActionAnswer> RunAsync(
        string action,
        string key,
        string vrchatUserId,
        IReadOnlyList<Guid> reasonIds,
        string note,
        StaffMember by,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(by);

        if (Refusal(action, by) is { } refusal)
            return StaffActionAnswer.RefusedWith(refusal);

        if (Service(action) is not { } service)
            return StaffActionAnswer.RefusedWith(NotSetUp);

        try
        {
            var result = await service.RunAsync(
                action,
                new ModerationActionRequest(vrchatUserId, key, reasonIds, note),
                new Caller(by.UserId, by.Username, by.Held),
                ct);

            return new StaffActionAnswer(
                result.Done,
                result.Error,
                Refused: false,
                result.Repeat,
                result.Gone,
                result.CaseId,
                result.DiscordDone,
                result.DiscordError,
                result.CaseFileError);
        }
        catch (ModerationRefused refused)
        {
            return StaffActionAnswer.RefusedWith(refused.Message);
        }
    }

    public async Task<StaffNoteAnswer> WriteNoteAsync(
        FactPlatform platform, string userId, string text, JsonObject? context, StaffMember by, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(by);

        var notes = new NoteService(
            _db,
            _clock,
            _services.GetService<IFactWriter>(),
            _services.GetService<EventPartitionMaintainer>());

        try
        {
            var written = await notes.WriteAsync(
                new WriteNoteRequest(userId, platform.ToString(), text),
                new Caller(by.UserId, by.Username, by.Held),
                context,
                ct);

            return new StaffNoteAnswer(written.Id, null);
        }
        catch (NoteRefused refused)
        {
            return new StaffNoteAnswer(null, refused.Message);
        }
    }

    /// <summary>The web app's permission rule for this action, or the sentence for a refusal.</summary>
    private static string? Refusal(string action, StaffMember by)
    {
        if (Requires(action) is not { } required)
            return $"'{action}' is not something Modbot can do.";

        return by.Held.HasFlag(ModbotPermissions.Administrator) || (by.Held & required) == required
            ? null
            : "You do not have permission to do that.";
    }

    /// <summary>The service, built as the endpoint for this action builds it; null when the host cannot act.</summary>
    private ModerationActionService? Service(string action)
    {
        var vrchat = _services.GetService<GroupModeration>();
        var facts = _services.GetService<IFactWriter>();
        var partitions = _services.GetService<EventPartitionMaintainer>();

        if (vrchat is null || facts is null || partitions is null)
            return null;

        var profiles = _services.GetService<VRChatUserProfiles>();
        var cases = new CaseFileService(
            _db,
            _clock,
            facts,
            partitions,
            profiles,
            _services.GetService<EvidenceOptions>(),
            _services.GetService<IEvidenceStore>(),
            _services.GetService<EvidenceStoreMonitor>());

        if (action is ModerationActionService.Approve or ModerationActionService.Reject)
        {
            // RequestEndpoints.Answer: the join request reader, and nothing for Discord or profiles.
            var requests = _services.GetService<GroupJoinRequests>();
            return requests is null
                ? null
                : new ModerationActionService(_db, _clock, vrchat, facts, partitions, cases, requests: requests);
        }

        // ModerationEndpoints.Map: profiles and the linked Discord bans.
        return new ModerationActionService(
            _db, _clock, vrchat, facts, partitions, cases,
            profiles: profiles,
            discord: _services.GetService<ILinkedDiscordBans>());
    }
}
