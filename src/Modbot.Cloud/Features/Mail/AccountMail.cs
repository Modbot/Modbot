namespace Modbot.Cloud.Features.Mail;

/// <summary>
/// The three messages Cloud sends. Plain text, short, and each one carries a single link.
/// </summary>
/// <remarks>
/// Plain text on purpose: an HTML mail from a moderation tool is a phishing lesson nobody needs, and
/// a link somebody can read before they click is worth more than a button.
/// </remarks>
public static class AccountMail
{
    public static (string Subject, string Body) VerifyEmail(Uri publicAddress, string token)
    {
        ArgumentNullException.ThrowIfNull(publicAddress);

        return (
            "Confirm your Modbot Cloud address",
            $"""
            Confirm this address to finish setting up your Modbot Cloud account:

            {Link(publicAddress, "/verify", token)}

            The link works for 24 hours. If you did not ask for an account, ignore this message.
            """);
    }

    public static (string Subject, string Body) ResetPassword(Uri publicAddress, string token)
    {
        ArgumentNullException.ThrowIfNull(publicAddress);

        return (
            "Reset your Modbot Cloud password",
            $"""
            Set a new password for your Modbot Cloud account:

            {Link(publicAddress, "/reset-password", token)}

            The link works for one hour. If you did not ask for this, ignore this message; your
            password has not changed.
            """);
    }

    public static (string Subject, string Body) ChangeEmail(Uri publicAddress, string token)
    {
        ArgumentNullException.ThrowIfNull(publicAddress);

        return (
            "Confirm your new Modbot Cloud address",
            $"""
            Confirm this address to move your Modbot Cloud account to it:

            {Link(publicAddress, "/verify-email-change", token)}

            The link works for 24 hours. Until you use it, the account keeps its old address.
            """);
    }

    private static string Link(Uri publicAddress, string path, string token) =>
        new Uri(publicAddress, $"{path}?token={Uri.EscapeDataString(token)}").ToString();
}
