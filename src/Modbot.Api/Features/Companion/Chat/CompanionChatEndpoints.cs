using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Modbot.AI;
using Modbot.AI.Calls;
using Modbot.AI.Chat;
using Modbot.AI.Usage;
using Modbot.Analytics.Facts;
using Modbot.Api.Auth;
using Modbot.Api.Features.Chat;
using Modbot.Api.Features.Companion.Devices;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;
using Modbot.Core.Users;

namespace Modbot.Api.Features.Companion.Chat;

/// <param name="Available">Chat answers right now and the device's owner may use it.</param>
public sealed record CompanionChatAvailability([property: JsonPropertyName("available")] bool Available);

/// <summary>
/// Dictate's way into AI Chat: whether the owner of a paired companion may use it, and sending the
/// words they dictated (Dictate design §6).
/// </summary>
/// <remarks>
/// <para><strong>The one place a device token speaks for a person.</strong> Every other endpoint in
/// this folder is ingest-scoped. This one acts as the device's owner, so it asks what the Chat page
/// asks, at the same moment and from the same read of the account: the owner is still enabled and
/// holds Pair a companion (the device check every endpoint here makes), holds Use AI chat or
/// Administrator, and has linked their VRChat account, which is the link every signed-in endpoint
/// requires. Chat on, the spend limits and the conversation belonging to the person are the Chat
/// page's own code (<see cref="ChatEndpoints.SendAsAsync"/>); nothing is copied.</para>
/// <para><strong>Nothing else is reachable.</strong> There is no way to list, open, rename or delete
/// a conversation from here, and a conversation id that is not the owner's answers 404 the way it does
/// on the Chat page. A stolen device token can ask the AI questions as its owner, within that
/// owner's spend limits, and read the answer to what it asked.</para>
/// </remarks>
public static class CompanionChatEndpoints
{
    public const string NotAllowed = "chat_not_allowed";

    public static async Task<IResult> AvailableAsync(
        int apiVersion,
        HttpContext context,
        DeviceAuthenticator authenticator,
        UserAccountService accounts,
        ModbotContext database,
        CancellationToken ct)
    {
        if (!CompanionApiVersion.IsSupported(apiVersion))
            return CompanionApiErrors.VersionUnsupported(apiVersion);

        var authentication = await authenticator.AuthenticateAsync(context, ct);
        if (!authentication.Succeeded)
            return authentication.Failure!;

        var allowed = await OwnerMayChatAsync(authentication, accounts, ct);
        if (!allowed)
            return Results.Ok(new CompanionChatAvailability(false));

        var settings = await database.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);
        return Results.Ok(new CompanionChatAvailability(ChatSwitch.On(settings)));
    }

    public static async Task<IResult> SendAsync(
        int apiVersion,
        ChatSendRequest? body,
        HttpContext context,
        DeviceAuthenticator authenticator,
        UserAccountService accounts,
        ModbotContext db,
        IAiClients ai,
        ChatToolRegistry registry,
        ChatLoop loop,
        AiSpendLimits limits,
        IAiUsage usage,
        IAiCallLog calls,
        IModbotClock clock,
        IFactWriter facts,
        EventPartitionMaintainer partitions,
        IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> jsonOptions,
        CancellationToken ct)
    {
        if (!CompanionApiVersion.IsSupported(apiVersion))
            return CompanionApiErrors.VersionUnsupported(apiVersion);

        var authentication = await authenticator.AuthenticateAsync(context, ct);
        if (!authentication.Succeeded)
            return authentication.Failure!;

        if (body is null)
            return CompanionApiErrors.Malformed("Send a message.");

        if (!await OwnerMayChatAsync(authentication, accounts, ct))
        {
            return Results.Json(
                new CompanionError(NotAllowed, "This account may not use AI chat."),
                statusCode: StatusCodes.Status403Forbidden);
        }

        var userId = authentication.Device!.IssuedToUserId;
        var username = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => u.Username)
            .FirstOrDefaultAsync(ct);

        // Only the text and the conversation: a companion has no use for editing or asking again,
        // and those would reach into the owner's other conversations by message id.
        var asked = new ChatSendRequest(body.ConversationId, body.Text, null, null);

        return await ChatEndpoints.SendAsAsync(
            context,
            asked,
            new ChatCaller(userId, username, authentication.OwnerPermissions),
            db, ai, registry, loop, limits, usage, calls, clock, facts, partitions, jsonOptions, ct);
    }

    /// <summary>The Chat page's own two requirements, read for the device's owner now.</summary>
    private static async Task<bool> OwnerMayChatAsync(
        DeviceAuthentication authentication, UserAccountService accounts, CancellationToken ct)
    {
        if (!ModbotAuth.Allows(authentication.OwnerPermissions, ModbotPermissions.UseAiChat))
            return false;

        var state = await accounts.StateAsync(authentication.Device!.IssuedToUserId, ct);
        return state is { VRChatLinked: true };
    }
}
