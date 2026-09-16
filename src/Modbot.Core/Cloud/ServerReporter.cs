using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Security;
using Modbot.Core.Time;

namespace Modbot.Core.Cloud;

/// <summary>
/// Builds this server's report, registers it with Modbot Cloud, and sends it.
/// </summary>
/// <remarks>
/// <para>
/// Scoped, because it reads the database. <see cref="ServerReportingService"/> is what calls it on a
/// schedule; the settings page calls <see cref="NewLinkCodeAsync"/> when an owner asks for a code.
/// </para>
/// <para>
/// Every failure is recorded on the settings row so the Health page can show it, and nothing else
/// about the deployment changes.
/// </para>
/// </remarks>
public sealed class ServerReporter(
    ModbotContext db,
    CloudServerClient client,
    ISecretProtector protector,
    IModbotClock clock)
{
    /// <summary>
    /// The alphabet a link code is written in: no <c>I</c>, <c>L</c>, <c>O</c> or <c>U</c>, so
    /// nothing reads as a one, a zero, or a word nobody wants on their screen. The same alphabet
    /// Cloud cleans a typed code against.
    /// </summary>
    public const string LinkCodeAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public const int LinkCodeLength = 8;

    /// <summary>
    /// Registers if this server has not, then sends one report. Returns what to tell the Health page.
    /// </summary>
    public async Task<CloudCallResult> ReportAsync(CancellationToken ct)
    {
        var settings = await db.GetSettingsAsync(ct).ConfigureAwait(false);
        var report = await BuildAsync(settings, ct).ConfigureAwait(false);

        var result = await SendAsync(settings, report, ct).ConfigureAwait(false);

        settings.CloudLastReportAt = clock.UtcNow;
        settings.CloudLastReportOk = result.Ok;
        settings.CloudLastReportProblem = result.Problem;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return result;
    }

    /// <summary>
    /// Makes a code, tells Cloud its hash, and hands the code back to be shown once.
    /// </summary>
    /// <remarks>
    /// This direction — Modbot shows, the person pastes into Cloud — is what keeps Cloud from having
    /// to call a Modbot server back at an address a stranger supplied. The proof arrives on a
    /// connection this server opened, authenticated with the secret only it holds.
    /// </remarks>
    /// <returns>The code, or null with the problem when Cloud would not take it.</returns>
    public async Task<(string? Code, string? Problem)> NewLinkCodeAsync(CancellationToken ct)
    {
        var settings = await db.GetSettingsAsync(ct).ConfigureAwait(false);

        if (Bearer(settings) is not { } bearer)
        {
            // Not registered yet: register now rather than telling the owner to wait six hours.
            var report = await BuildAsync(settings, ct).ConfigureAwait(false);
            var registered = await RegisterAsync(settings, report, ct).ConfigureAwait(false);

            if (!registered.Ok)
                return (null, registered.Problem);

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            bearer = Bearer(settings)!;
        }

        var code = RandomNumberGenerator.GetString(LinkCodeAlphabet, LinkCodeLength);
        var sent = await client.SendLinkCodeAsync(bearer, HashCode(code), ct).ConfigureAwait(false);

        return sent.Ok ? (code, null) : (null, sent.Problem);
    }

    /// <summary>Lower-case hex SHA-256 of a link code. Cloud stores only this.</summary>
    public static string HashCode(string code) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(code)));

    /// <summary>
    /// The group's description out of the stored group-info snapshot, or null.
    /// </summary>
    /// <remarks>
    /// Read out of the JSON rather than through the snapshot type, which lives in
    /// <c>Modbot.VRChat</c> — a project this one is underneath. One field is not worth inverting the
    /// dependency for, and a snapshot written by an older shape simply yields null.
    /// </remarks>
    public static string? GroupDescription(string? snapshotJson)
    {
        if (string.IsNullOrWhiteSpace(snapshotJson))
            return null;

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(snapshotJson);

            return document.RootElement.TryGetProperty("Description", out var description)
                   && description.ValueKind == System.Text.Json.JsonValueKind.String
                ? description.GetString()
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private async Task<CloudCallResult> SendAsync(Settings settings, ServerReport report, CancellationToken ct)
    {
        if (Bearer(settings) is not { } bearer)
        {
            var registered = await RegisterAsync(settings, report, ct).ConfigureAwait(false);
            if (!registered.Ok)
                return registered;

            bearer = Bearer(settings)!;
        }

        var result = await client.ReportAsync(bearer, report, ct).ConfigureAwait(false);

        if (!result.Forgotten)
            return result;

        // Cloud does not know this server any more. Forget the secret so the next pass registers.
        settings.CloudServerId = null;
        settings.CloudServerSecretEncrypted = null;
        return result;
    }

    private async Task<CloudCallResult> RegisterAsync(Settings settings, ServerReport report, CancellationToken ct)
    {
        if (await client.RegisterAsync(report, ct).ConfigureAwait(false) is not { } registration)
            return new CloudCallResult(false, false, "Modbot Cloud would not register this server.");

        settings.CloudServerId = registration.ServerId.ToString();
        settings.CloudServerSecretEncrypted = protector.Protect(registration.Secret);

        return CloudCallResult.Success;
    }

    /// <summary><c>serverId.secret</c>, or null when this server has not registered.</summary>
    private string? Bearer(Settings settings)
    {
        if (settings.CloudServerId is not { Length: > 0 } id || settings.CloudServerSecretEncrypted is not { } stored)
            return null;

        var secret = protector.Unprotect(stored);
        return string.IsNullOrEmpty(secret) ? null : $"{id}.{secret}";
    }

    private async Task<ServerReport> BuildAsync(Settings settings, CancellationToken ct)
    {
        // Since the last report, so a spike is a spike rather than a running total that only ever
        // climbs. Everything before the first report counts once.
        var since = settings.CloudLastReportAt ?? DateTimeOffset.UnixEpoch;

        var coldStops = await db.Events
            .CountAsync(e => e.Type == FactType.RateLimitColdStop && e.OccurredAt > since, ct)
            .ConfigureAwait(false);

        var wafBlocks = await db.Events
            .CountAsync(e => e.Type == FactType.WafBlocked && e.OccurredAt > since, ct)
            .ConfigureAwait(false);

        var termLists = await db.ModerationTermLists
            .Where(l => l.HubId != null)
            .Select(l => l.HubId!)
            .Distinct()
            .OrderBy(id => id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return new ServerReport
        {
            PublicAddress = settings.PublicAddress,
            Version = ModbotVersion.Release,
            HostPlatform = $"{RuntimeInformation.RuntimeIdentifier}",
            GroupId = settings.ManagedGroupId,
            GroupName = settings.ManagedGroupName,
            GroupDescription = GroupDescription(settings.GroupInfoSnapshot),
            GroupIconUrl = settings.ManagedGroupIconUrl,
            GroupBannerUrl = settings.ManagedGroupBannerUrl,
            DiscordConnected = settings.DiscordBotTokenEncrypted is not null,
            TermListsImported = termLists,
            RateLimitColdStops = coldStops,
            WafBlocks = wafBlocks,
            AiModerationEnabled = settings.AiModerationEnabled,
        };
    }
}
