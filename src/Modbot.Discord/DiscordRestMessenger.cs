using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Discord;
using Modbot.Core.Email;
using Modbot.Core.Security;

namespace Modbot.Discord;

/// <summary>
/// <see cref="IDiscordMessenger"/> over Discord's REST API: open a DM channel, post one message.
/// </summary>
/// <remarks>
/// <para>
/// No gateway session and no library. The bot of foundation §9 is not built yet, and a direct
/// message needs neither: <c>POST /users/@me/channels</c> with the recipient, then
/// <c>POST /channels/{id}/messages</c>. When the gateway bot arrives it can take over this
/// interface; until then two calls with <see cref="HttpClient"/> are the whole integration.
/// </para>
/// <para>
/// The token is decrypted for one send and put in a request header, never stored on the client
/// (foundation §8.3). Discord answers 403 when the person shares no server with the bot or has
/// direct messages closed, and that sentence is passed on, because it is the one the operator
/// needs.
/// </para>
/// </remarks>
public sealed class DiscordRestMessenger : IDiscordMessenger
{
    private const string ApiBase = "https://discord.com/api/v10/";

    private readonly ModbotContext _db;
    private readonly ISecretProtector _protector;
    private readonly IHttpClientFactory _http;

    public DiscordRestMessenger(ModbotContext db, ISecretProtector protector, IHttpClientFactory http)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(http);

        _db = db;
        _protector = protector;
        _http = http;
    }

    public async Task<bool> IsConfiguredAsync(CancellationToken ct = default)
    {
        var settings = await _db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);
        return settings?.DiscordBotTokenEncrypted is { Length: > 0 };
    }

    public async Task<SendOutcome> SendDirectMessageAsync(
        string discordUserId, string text, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(discordUserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var settings = await _db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);
        var token = _protector.Unprotect(settings?.DiscordBotTokenEncrypted);

        if (string.IsNullOrEmpty(token))
            return SendOutcome.NotConfigured("The Discord bot");

        try
        {
            using var client = _http.CreateClient(nameof(DiscordRestMessenger));
            client.BaseAddress = new Uri(ApiBase);
            client.Timeout = TimeSpan.FromSeconds(20);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bot", token);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Modbot (https://github.com/Modbot/Modbot, 1.0)");

            using var channel = await client.PostAsJsonAsync(
                "users/@me/channels", new { recipient_id = discordUserId }, ct);

            if (!channel.IsSuccessStatusCode)
                return SendOutcome.Failed(await Explain("open a direct message", channel, ct));

            var channelId = (await channel.Content.ReadFromJsonAsync<JsonElement>(ct))
                .GetProperty("id").GetString();

            if (string.IsNullOrEmpty(channelId))
                return SendOutcome.Failed("Discord did not return a channel to send the message to.");

            using var message = await client.PostAsJsonAsync(
                $"channels/{channelId}/messages", new { content = text }, ct);

            return message.IsSuccessStatusCode
                ? SendOutcome.Ok
                : SendOutcome.Failed(await Explain("send the message", message, ct));
        }
        catch (HttpRequestException e)
        {
            return SendOutcome.Failed($"Could not reach Discord: {e.Message}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return SendOutcome.Failed("Discord did not answer in time.");
        }
    }

    private static async Task<string> Explain(string step, HttpResponseMessage response, CancellationToken ct)
    {
        var detail = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "the bot token was rejected",
            HttpStatusCode.Forbidden =>
                "Discord refused; the person may have direct messages closed, or share no server with the bot",
            HttpStatusCode.NotFound => "Discord does not know that user id",
            HttpStatusCode.TooManyRequests => "Discord is rate limiting the bot; try again in a minute",
            _ => $"Discord answered {(int)response.StatusCode}",
        };

        // Discord's own message when it gave one; it names the field that was wrong.
        try
        {
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            if (body.ValueKind == JsonValueKind.Object
                && body.TryGetProperty("message", out var m)
                && m.GetString() is { Length: > 0 } text)
            {
                detail += $" ({text})";
            }
        }
        catch (JsonException)
        {
            // A non-JSON error body adds nothing to the status code.
        }

        return $"Could not {step}: {detail}.";
    }
}
