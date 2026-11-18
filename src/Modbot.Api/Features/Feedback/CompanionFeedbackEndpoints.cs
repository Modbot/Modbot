using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Modbot.AI;
using Modbot.AI.Calls;
using Modbot.AI.Chat;
using Modbot.AI.Usage;
using Modbot.Api.Auth;
using Modbot.Api.Features.Companion;
using Modbot.Api.Features.Companion.Devices;
using Modbot.Api.Features.Users;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;
using OpenAI.Chat;

namespace Modbot.Api.Features.Feedback;

/// <summary>
/// The Feedback button's way into the server: whether the owner of a paired companion may send
/// feedback, tidying what they said, and saving what they confirmed (feedback design §5).
/// </summary>
/// <remarks>
/// <para>
/// <strong>A device token speaks for its owner here, for sending only.</strong> The owner must still
/// be enabled and hold Pair a companion (the device check every endpoint here makes) and hold Send
/// feedback. Nothing else is asked: not AI chat, not a linked VRChat account, and not AI itself, so
/// the button shows on a server with AI off (feedback design §4). Reading feedback is the Feedback
/// page's and needs a signed-in admin; a device cannot list or open anything.
/// </para>
/// <para>
/// Tidying is a Chat call for the bill (it is recorded and limited under <c>chat</c>) but never
/// blocks a send: when AI is off, over a limit, down or answers with nothing usable, the sender gets
/// their own words back and writes the rest on the draft.
/// </para>
/// </remarks>
public static class CompanionFeedbackEndpoints
{
    public const string NotAllowed = "feedback_not_allowed";

    public static async Task<IResult> AvailableAsync(
        int apiVersion,
        HttpContext context,
        [FromServices] DeviceAuthenticator authenticator,
        [FromServices] IAiClients ai,
        CancellationToken ct)
    {
        if (!CompanionApiVersion.IsSupported(apiVersion))
            return CompanionApiErrors.VersionUnsupported(apiVersion);

        var authentication = await authenticator.AuthenticateAsync(context, ct);
        if (!authentication.Succeeded)
            return authentication.Failure!;

        if (!MaySend(authentication))
            return Results.Ok(new CompanionFeedbackAvailability(false, false));

        return Results.Ok(new CompanionFeedbackAvailability(true, await ai.GetChatAsync(ct) is not null));
    }

    public static async Task<IResult> TidyAsync(
        int apiVersion,
        CompanionFeedbackTidyRequest? body,
        HttpContext context,
        [FromServices] DeviceAuthenticator authenticator,
        [FromServices] ModbotContext db,
        [FromServices] IAiClients ai,
        [FromServices] AiSpendLimits limits,
        [FromServices] AiCallRunner runner,
        [FromServices] IAiCallLog calls,
        CancellationToken ct)
    {
        if (!CompanionApiVersion.IsSupported(apiVersion))
            return CompanionApiErrors.VersionUnsupported(apiVersion);

        var authentication = await authenticator.AuthenticateAsync(context, ct);
        if (!authentication.Succeeded)
            return authentication.Failure!;

        if (!MaySend(authentication))
            return Refused();

        var text = body?.Text?.Trim();
        if (string.IsNullOrEmpty(text))
            return CompanionApiErrors.Malformed("Say something.");

        var raw = new CompanionFeedbackDraft(null, null, Cut(text, FeedbackItem.MaxDetailsLength), false);

        var chat = await ai.GetChatAsync(ct);
        if (chat is null)
            return Results.Ok(raw);

        var userId = authentication.Device!.IssuedToUserId;
        var username = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => u.Username)
            .FirstOrDefaultAsync(ct) ?? string.Empty;

        var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);
        var model = string.IsNullOrWhiteSpace(settings?.AiChatModel) ? chat.Model : settings.AiChatModel.Trim();

        if (await limits.CheckAsync(AiFeatures.Chat, userId, authentication.OwnerPermissions, ct) is { } reached)
        {
            await calls.RecordAsync(
                new AiCallEntry(
                    AiFeatures.Chat, model, null, chat.Provider, AiCallOutcomes.Limited, 0,
                    Error: reached.Message, UserId: userId, Username: username),
                ct);

            return Results.Ok(raw);
        }

        var spoken = Cut(text, FeedbackTidy.MaxTextLength);

        var plan = new AiCallPlan(
            AiFeatures.Chat, chat, model,
            UserId: userId,
            Username: username,
            KeepText: false,
            ChatTimeLimitSeconds: settings?.AiChatTimeLimitSeconds);

        var result = await runner.RunAsync(plan, async (client, token) =>
        {
            var options = new ChatCompletionOptions { MaxOutputTokenCount = FeedbackTidy.MaxOutputTokens };
            AiReportedCost.AskFor(options, chat.Provider);

            ChatCompletion completion = await client.CompleteChatAsync(
                [AiPromptCache.Instructions(FeedbackTidy.Instructions, chat.Provider), new UserChatMessage(spoken)],
                options,
                token);

            var answer = string.Concat(completion.Content
                .Where(p => p.Kind == ChatMessageContentPartKind.Text)
                .Select(p => p.Text));

            return new AiCallAnswer<string>(answer, completion.Model, completion.Usage, null);
        }, ct);

        var draft = result.Answered ? FeedbackTidy.Parse(result.Value) : null;

        return Results.Ok(draft is null
            ? raw
            : new CompanionFeedbackDraft(draft.Kind, draft.Title, draft.Details, true));
    }

    public static async Task<IResult> SendAsync(
        int apiVersion,
        FeedbackRequest? body,
        HttpContext context,
        [FromServices] DeviceAuthenticator authenticator,
        [FromServices] ModbotContext db,
        [FromServices] AccountFacts facts,
        [FromServices] IModbotClock clock,
        CancellationToken ct)
    {
        if (!CompanionApiVersion.IsSupported(apiVersion))
            return CompanionApiErrors.VersionUnsupported(apiVersion);

        var authentication = await authenticator.AuthenticateAsync(context, ct);
        if (!authentication.Succeeded)
            return authentication.Failure!;

        if (!MaySend(authentication))
            return Refused();

        if (body is null)
            return CompanionApiErrors.Malformed("Send the feedback.");

        var words = FeedbackWriter.Check(body.Kind, body.Title, body.Details, out var problem);
        if (words is null)
            return CompanionApiErrors.Malformed(problem!);

        var userId = authentication.Device!.IssuedToUserId;
        var username = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => u.Username)
            .FirstOrDefaultAsync(ct) ?? string.Empty;

        var item = await new FeedbackWriter(db, facts, clock).SaveAsync(new Actor(userId, username), words, ct);

        return Results.Ok(new CompanionFeedbackSaved(item.Id));
    }

    private static bool MaySend(DeviceAuthentication authentication) =>
        ModbotAuth.Allows(authentication.OwnerPermissions, ModbotPermissions.SendFeedback);

    private static IResult Refused() =>
        Results.Json(
            new CompanionError(NotAllowed, "This account may not send feedback."),
            statusCode: StatusCodes.Status403Forbidden);

    private static string Cut(string text, int length) =>
        text.Length <= length ? text : text[..length];
}
