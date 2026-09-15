using Microsoft.AspNetCore.Http;
using Modbot.Core.Data;
using Modbot.Core.Security;
using Modbot.Core.Time;
using Modbot.VRChat;
using Modbot.VRChat.Scheduling;

namespace Modbot.Api.Features.Onboarding.TestConnection;

/// <summary>
/// Spec 7.1.1: Modbot tests its own egress and says which of four different problems it has.
/// </summary>
/// <remarks>
/// <para>
/// The whole feature turns on one distinction. A Cloudflare WAF block and a DNS failure and a
/// timeout and a rejected password all look identical from the outside — no data, an error — and
/// exactly one of them is fixed by a proxy. Offering a proxy for the other three costs an
/// operator a subscription, an evening, and their belief that the software knows what it is
/// doing; not offering one for the first leaves a VPS deployment permanently unable to run.
/// </para>
/// <para>
/// The classification is the gate's, not this handler's (spec 4.1 — the gate already tells a
/// Cloudflare interstitial apart from a VRChat error, and now tells DNS apart from a timeout).
/// This slice's job is to persist the proxy under test and to turn the answer into sentences.
/// </para>
/// </remarks>
public static class TestConnectionHandler
{
    public static async Task<IResult> HandleAsync(
        TestConnectionRequest? request,
        ModbotContext db,
        ISecretProtector protector,
        IVRChatGate gate,
        IModbotClock clock,
        IMonotonicClock elapsed,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(elapsed);

        var settings = await db.GetSettingsAsync(ct);

        if (request is not null && request.UseProxy is { } useProxy)
        {
            if (useProxy)
            {
                if (string.IsNullOrWhiteSpace(request.ProxyUrl))
                    return Results.BadRequest(new { error = "A proxy URL is required to use a proxy." });

                if (!Uri.TryCreate(request.ProxyUrl.Trim(), UriKind.Absolute, out var uri)
                    || uri.Scheme is not ("http" or "https" or "socks5"))
                {
                    return Results.BadRequest(new
                    {
                        error = "That proxy URL needs a scheme and a port.",
                    });
                }

                settings.ProxyUrl = uri.ToString();
                settings.ProxyUsername = string.IsNullOrWhiteSpace(request.ProxyUsername)
                    ? null
                    : request.ProxyUsername.Trim();

                // Null means "leave what is stored alone", empty means "clear it". Without that
                // distinction, re-testing after fixing a typo in the URL would silently wipe a
                // password the operator has no way to read back and retype.
                if (request.ProxyPassword is not null)
                {
                    settings.ProxyPasswordEncrypted = request.ProxyPassword.Length == 0
                        ? null
                        : protector.Protect(request.ProxyPassword);
                }
            }
            else
            {
                settings.ProxyUrl = null;
                settings.ProxyUsername = null;
                settings.ProxyPasswordEncrypted = null;
            }

            await db.SaveChangesAsync(ct);
        }

        // The stored session is kept. The gate rebuilds its client from the settings just saved,
        // proxy included, and checks the session through it: a round trip through the proxy proves
        // the proxy works as well as a sign-in would, without spending one of the few VRChat
        // allows an hour (spec 4.1.2). This used to drop the session and force a sign-in on every
        // press of "Test connection".

        var started = elapsed.Elapsed;
        var result = await gate.SignInAsync(ct);
        var took = (long)Math.Max(0, (elapsed.Elapsed - started).TotalMilliseconds);

        var diagnosis = ConnectionDiagnosis.Describe(result, result.Value?.DisplayName, took);

        if (diagnosis.Succeeded)
        {
            settings.ConnectionCheckedAt = clock.UtcNow;
            settings.VRChatDisplayName = result.Value?.DisplayName ?? settings.VRChatDisplayName;
            settings.VRChatVerifiedAt = clock.UtcNow;
            await db.SaveChangesAsync(ct);
        }

        // 200 either way. A failed check is a successful diagnosis, and it is the answer the
        // operator asked for -- the SPA renders the same shape in both cases and re-tests in
        // place until it passes (spec 7.1.1).
        return Results.Ok(diagnosis);
    }
}
