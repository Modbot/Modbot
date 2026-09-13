using Microsoft.AspNetCore.Http;
using Modbot.Core.Data;
using Modbot.Core.Security;
using Modbot.Core.Time;
using Modbot.VRChat;
using Modbot.VRChat.Scheduling;

namespace Modbot.Api.Features.Onboarding.VerifyVRChat;

/// <summary>
/// Spec 7.1 step 2: store the VRChat account, then prove it works before moving on.
/// </summary>
/// <remarks>
/// <para>
/// The live check is the entire point. Credentials accepted without one fail at the first sync,
/// hours later, in a log the operator is not reading — and the symptom then is "Modbot does not
/// work", which is a much worse problem to be handed than "that password was wrong".
/// </para>
/// <para>
/// The credentials are written <em>before</em> the check and kept even when it fails, which looks
/// backwards and is not. The gate authenticates from what is stored (spec 4.1), so there is
/// nothing to test until they are there; and a failure is very often a WAF block rather than a
/// bad password, in which case the next step is to add a proxy and retry — which would mean
/// retyping a password and a TOTP secret that had just been thrown away. What is <em>not</em>
/// written until VRChat says yes is <c>VRChatVerifiedAt</c>, so nothing downstream mistakes
/// "stored" for "works".
/// </para>
/// </remarks>
public static class VerifyVRChatHandler
{
    public static async Task<IResult> HandleAsync(
        VerifyVRChatRequest request,
        ModbotContext db,
        ISecretProtector protector,
        IVRChatGate gate,
        IModbotClock clock,
        IMonotonicClock elapsed,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(elapsed);

        if (string.IsNullOrWhiteSpace(request.Username))
            return Results.BadRequest(new { error = "A VRChat email address or username is required." });

        if (string.IsNullOrEmpty(request.Password))
            return Results.BadRequest(new { error = "The VRChat account's password is required." });

        var settings = await db.GetSettingsAsync(ct);

        settings.VRChatUsername = request.Username.Trim();
        settings.VRChatPasswordEncrypted = protector.Protect(request.Password);
        settings.VRChatTotpSecretEncrypted = string.IsNullOrWhiteSpace(request.TotpSecret)
            ? null
            // Authenticator apps display the secret in spaced groups of four and people copy it
            // that way. Base32 has no whitespace, so stripping it here turns a guaranteed failure
            // into a working account.
            : protector.Protect(request.TotpSecret.Replace(" ", string.Empty, StringComparison.Ordinal).Trim());

        // A session cookie belonging to whatever account was configured before is not merely
        // stale, it is dangerous: the gate would present it, VRChat would accept it, and Modbot
        // would report the *previous* account as verified while holding the new one's password.
        settings.VRChatAuthCookieEncrypted = null;
        settings.VRChatDisplayName = null;
        settings.VRChatVerifiedAt = null;

        await db.SaveChangesAsync(ct);

        var started = elapsed.Elapsed;
        var result = await gate.SignInAsync(ct);
        var took = (long)Math.Max(0, (elapsed.Elapsed - started).TotalMilliseconds);

        var diagnosis = ConnectionDiagnosis.Describe(result, result.Value?.DisplayName, took);

        if (!diagnosis.Succeeded)
        {
            // 422, not 400: the request was well-formed and Modbot understood it perfectly. What
            // failed is the thing it describes. A 400 would tell the SPA to blame the form.
            return Results.UnprocessableEntity(diagnosis);
        }

        settings.VRChatDisplayName = result.Value?.DisplayName;
        settings.VRChatVerifiedAt = clock.UtcNow;

        // A successful login is a completed round trip to api.vrchat.cloud, which is exactly what
        // the connection check asks. Recording it here means an operator whose egress plainly
        // works is not made to prove it twice; step 3 remains available and re-runnable, which is
        // what it is actually for (spec 7.1.1 — the gate's WafBlocked state links back to it).
        settings.ConnectionCheckedAt = clock.UtcNow;

        await db.SaveChangesAsync(ct);

        return Results.Ok(new VerifyVRChatResponse(
            result.Value?.DisplayName, result.Value?.Id, settings.VRChatVerifiedAt.Value));
    }
}
