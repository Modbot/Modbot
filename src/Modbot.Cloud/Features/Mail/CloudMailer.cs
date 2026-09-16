using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Modbot.Cloud.Features.Mail;

/// <summary>What Cloud needs in order to send mail, from the environment.</summary>
/// <param name="ApiKey">The Resend key. Null means Cloud sends nothing.</param>
/// <param name="From">The From address, such as <c>Modbot &lt;noreply@modbot.co&gt;</c>.</param>
/// <param name="PublicAddress">Where Cloud is reachable, for the links in the mail.</param>
public sealed record MailSettings(string? ApiKey, string? From, Uri PublicAddress)
{
    public bool CanSend => !string.IsNullOrEmpty(ApiKey) && !string.IsNullOrEmpty(From);
}

/// <summary>Sending one message. An interface so a test can read what was sent without a network.</summary>
public interface ICloudMailer
{
    /// <summary>Whether mail is configured at all. False means every endpoint that sends is refused.</summary>
    bool CanSend { get; }

    /// <summary>True when the message was accepted. False when it was not; the reason is logged, not returned.</summary>
    Task<bool> SendAsync(string to, string subject, string body, CancellationToken ct);
}

/// <summary>
/// Sends through Resend's HTTP API.
/// </summary>
/// <remarks>
/// <para>
/// One <c>POST</c> to <c>https://api.resend.com/emails</c> with a bearer key and a small JSON body.
/// No SDK: a package to make one request is a dependency to keep current forever, and its surface is
/// larger than the thing it replaces.
/// </para>
/// <para>
/// <strong>The key never leaves the environment.</strong> It is not returned by any endpoint and not
/// written to any log line — a failure logs the status code and the address's domain, never the key
/// and never the body.
/// </para>
/// </remarks>
public sealed class ResendMailer(MailSettings settings, IHttpClientFactory http, ILogger<ResendMailer> log)
    : ICloudMailer
{
    public const string HttpClientName = "Resend";

    public const string Endpoint = "https://api.resend.com/emails";

    /// <summary>Resend's fields are lower case: <c>from</c>, <c>to</c>, <c>subject</c>, <c>text</c>.</summary>
    private static readonly System.Text.Json.JsonSerializerOptions Json =
        new(System.Text.Json.JsonSerializerDefaults.Web);

    public bool CanSend => settings.CanSend;

    public async Task<bool> SendAsync(string to, string subject, string body, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(to);

        if (!settings.CanSend)
        {
            log.LogWarning("No Resend key is set, so no mail was sent.");
            return false;
        }

        try
        {
            using var client = http.CreateClient(HttpClientName);
            client.Timeout = TimeSpan.FromSeconds(20);

            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                Content = JsonContent.Create(new ResendMessage(settings.From!, [to], subject, body), options: Json),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);

            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
                return true;

            // The status and nothing else. The body can repeat back what was sent.
            log.LogWarning("Resend answered {Status} for a message to {Domain}.", (int)response.StatusCode, Domain(to));
            return false;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            log.LogWarning("Could not reach Resend to send a message to {Domain}.", Domain(to));
            return false;
        }
    }

    private static string Domain(string address)
    {
        var at = address.LastIndexOf('@');
        return at >= 0 && at < address.Length - 1 ? address[(at + 1)..] : "unknown";
    }

    private sealed record ResendMessage(string From, string[] To, string Subject, string Text);
}
