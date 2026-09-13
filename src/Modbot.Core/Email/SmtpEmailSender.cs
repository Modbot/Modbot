using System.Net;
using System.Net.Mail;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Security;

namespace Modbot.Core.Email;

/// <summary>
/// <see cref="IEmailSender"/> over the SMTP settings the wizard stored.
/// </summary>
/// <remarks>
/// <para>
/// Reads the settings row on every send rather than at construction: the relay can be changed on
/// the settings page and the next test message must use what was just typed. The password is
/// decrypted for the duration of one send and held nowhere else (foundation §8.3).
/// </para>
/// <para>
/// <c>System.Net.Mail</c> rather than a mail library. It speaks submission-with-STARTTLS and
/// password authentication, which is what every relay a community group is likely to use wants,
/// and it adds no dependency. If a relay turns up that it cannot talk to, this class is the one
/// place to swap the transport.
/// </para>
/// </remarks>
public sealed class SmtpEmailSender : IEmailSender
{
    private readonly ModbotContext _db;
    private readonly ISecretProtector _protector;

    public SmtpEmailSender(ModbotContext db, ISecretProtector protector)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(protector);

        _db = db;
        _protector = protector;
    }

    public async Task<bool> IsConfiguredAsync(CancellationToken ct = default)
    {
        var settings = await _db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);
        return settings?.SmtpHost is { Length: > 0 } && settings.SmtpFromAddress is { Length: > 0 };
    }

    public async Task<SendOutcome> SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        var settings = await _db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);

        if (settings?.SmtpHost is not { Length: > 0 } host)
            return SendOutcome.NotConfigured("Email");

        if (settings.SmtpFromAddress is not { Length: > 0 } from)
            return SendOutcome.Failed("Email needs a from address before anything can be sent.");

        try
        {
            using var client = new SmtpClient(host, settings.SmtpPort ?? 587)
            {
                EnableSsl = settings.SmtpUseTls,
                DeliveryMethod = SmtpDeliveryMethod.Network,
                Timeout = (int)TimeSpan.FromSeconds(20).TotalMilliseconds,
            };

            if (settings.SmtpUsername is { Length: > 0 } username)
            {
                client.Credentials = new NetworkCredential(
                    username, _protector.Unprotect(settings.SmtpPasswordEncrypted) ?? string.Empty);
            }

            using var mail = new MailMessage(from, message.To, message.Subject, message.Body)
            {
                IsBodyHtml = false,
            };

            await client.SendMailAsync(mail, ct);
            return SendOutcome.Ok;
        }
        catch (SmtpException e)
        {
            // The relay's own words, because "could not send" helps nobody fix a relay setting.
            return SendOutcome.Failed($"The mail server refused the message: {e.Message}");
        }
        catch (FormatException)
        {
            return SendOutcome.Failed("One of the addresses is not a valid email address.");
        }
        catch (Exception e) when (e is InvalidOperationException or System.Net.Sockets.SocketException or IOException)
        {
            return SendOutcome.Failed($"Could not reach the mail server: {e.Message}");
        }
    }
}
