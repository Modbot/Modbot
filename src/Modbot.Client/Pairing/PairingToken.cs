using System.Text;
using System.Text.Json;

namespace Modbot.Client.Pairing;

/// <summary>
/// What the pairing page hands this client: which server, and a one-time code for it.
/// </summary>
/// <remarks>
/// <para><strong>The shape.</strong> base64url of <c>{"server": "https://…", "code": "ABCD-EFGH"}</c>.
/// It arrives either inside a <c>modbot-client://pair?token=…</c> link that Windows hands to the
/// client when the moderator presses "Open in Modbot" in their browser, or pasted into the client
/// window as a bare token when that button did not do anything. Both are the same bytes and end up
/// here.</para>
/// <para><strong>What it carries, and what it never does.</strong> The short-lived, single-use
/// pairing code — the thing that was previously read off a screen and retyped. Never a device
/// token, never anything that lasts: links end up in browser history, in shell logs and in
/// Windows' record of protocol launches, and a code found there a week later is worthless where a
/// token would not be.</para>
/// <para><strong>Everything in it is checked before anything is sent.</strong> A link is untrusted
/// input from wherever the browser got it. The server address has to be an HTTPS origin (or plain
/// HTTP to this machine, see <see cref="ServerAddresses"/>), with no path, query, fragment or
/// user name attached; the code has to look like a code. A token that fails is refused with a
/// sentence saying what was wrong, and no request is made anywhere.</para>
/// <para>Nothing here reads the disk or the network. It turns text into an address and a code.</para>
/// </remarks>
public sealed record PairingToken(Uri Server, string Code)
{
    /// <summary>The URL scheme the client registers, so a browser can hand it a link.</summary>
    public const string Scheme = "modbot-client";

    /// <summary>What a pairing link starts with. The token follows.</summary>
    public const string LinkPrefix = "modbot-client://pair?token=";

    /// <summary>
    /// Far more than a real token needs. A real one is under two hundred characters; the bound
    /// exists so a pasted novel is refused in one comparison rather than decoded.
    /// </summary>
    public const int MaxLength = 4096;

    /// <summary>Codes are nine characters today. The bound leaves room without inviting abuse.</summary>
    public const int MaxCodeLength = 64;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The token as text: what the pairing page produces, and what "copy" copies.</summary>
    public string Encode()
    {
        var json = JsonSerializer.Serialize(new Body(Server.ToString(), Code), Json);
        return Base64Url(Encoding.UTF8.GetBytes(json));
    }

    /// <summary>
    /// The link form: what "Open in Modbot" opens. Text rather than a <see cref="Uri"/>, because
    /// <see cref="Uri"/> would rewrite <c>pair?token</c> as <c>pair/?token</c> and the page, the
    /// docs and Windows should all see one spelling.
    /// </summary>
    public string ToLink() => LinkPrefix + Encode();

    /// <summary>Whether text starts like a pairing link, as opposed to a bare token or anything else.</summary>
    public static bool LooksLikeLink(string? text)
        => text is not null
           && text.TrimStart().StartsWith(Scheme + ":", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads a pairing link or a bare pairing token. On failure, <paramref name="problem"/> is a
    /// sentence for the moderator that says what to do next.
    /// </summary>
    public static bool TryParse(string? text, out PairingToken? token, out string problem)
    {
        token = null;

        var trimmed = (text ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            problem = "Paste the pairing token from your group's Modbot pairing page.";
            return false;
        }

        if (trimmed.Length > MaxLength + LinkPrefix.Length)
        {
            problem = "That is not a Modbot pairing token: it is far too long. Copy it again from the pairing page.";
            return false;
        }

        string blob;
        if (LooksLikeLink(trimmed))
        {
            if (!TryReadLink(trimmed, out blob!, out problem))
                return false;
        }
        else
        {
            blob = trimmed;
        }

        if (blob.Length > MaxLength)
        {
            problem = "That is not a Modbot pairing token: it is far too long. Copy it again from the pairing page.";
            return false;
        }

        if (blob.Length == 0 || !IsBase64Url(blob))
        {
            problem = "That is not a Modbot pairing token. Copy it again from the pairing page.";
            return false;
        }

        Body? body;
        try
        {
            body = JsonSerializer.Deserialize<Body>(FromBase64Url(blob), Json);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
        {
            problem = "That is not a Modbot pairing token. Copy it again from the pairing page.";
            return false;
        }

        if (body is not { Server.Length: > 0, Code.Length: > 0 })
        {
            problem = "That pairing token is missing its server address or its code. Copy it again from the pairing page.";
            return false;
        }

        var serverText = body.Server!;
        var code = body.Code!.Trim();
        if (code.Length == 0 || code.Length > MaxCodeLength || !code.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
        {
            problem = "That pairing token does not contain a pairing code. Copy it again from the pairing page.";
            return false;
        }

        if (!Uri.TryCreate(serverText.Trim(), UriKind.Absolute, out var server) || server.Host.Length == 0)
        {
            problem = "That pairing token does not name a server address Modbot can use. Copy it again from the pairing page.";
            return false;
        }

        if (!ServerAddresses.IsAllowed(server))
        {
            problem = $"This pairing token points at {server.GetLeftPart(UriPartial.Authority)}, which is not "
                      + "a secure (https) address. Modbot only pairs over HTTPS.";
            return false;
        }

        // An origin and nothing more. A path, a query or a user name in the address has no business
        // in a token; refusing them keeps a token from steering the client somewhere its author
        // did not intend.
        if (server.UserInfo.Length > 0
            || server.AbsolutePath is not ("" or "/")
            || server.Query.Length > 0
            || server.Fragment.Length > 0)
        {
            problem = "That pairing token's server address has extra parts on the end. Copy it again from the pairing page.";
            return false;
        }

        token = new PairingToken(new Uri(server.GetLeftPart(UriPartial.Authority) + "/"), code);
        problem = string.Empty;
        return true;
    }

    /// <summary>Pulls the token out of <c>modbot-client://pair?token=…</c>.</summary>
    private static bool TryReadLink(string link, out string? blob, out string problem)
    {
        blob = null;

        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(uri.Host, "pair", StringComparison.OrdinalIgnoreCase))
        {
            problem = "That link is not a Modbot pairing link. Open the pairing page again and press \"Open in Modbot\".";
            return false;
        }

        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            var name = equals < 0 ? pair : pair[..equals];
            if (!string.Equals(name, "token", StringComparison.OrdinalIgnoreCase))
                continue;

            blob = equals < 0 ? string.Empty : Uri.UnescapeDataString(pair[(equals + 1)..]);
            problem = string.Empty;
            return true;
        }

        problem = "That pairing link has no token in it. Open the pairing page again and press \"Open in Modbot\".";
        return false;
    }

    private static bool IsBase64Url(string text)
    {
        var body = text.TrimEnd('=');
        return body.Length > 0
               && body.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    }

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string text)
    {
        var standard = text.TrimEnd('=').Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(standard.PadRight(standard.Length + (4 - standard.Length % 4) % 4, '='));
    }

    /// <summary>The JSON inside the token. Property names are the contract with the pairing page.</summary>
    private sealed record Body(string? Server, string? Code);
}
