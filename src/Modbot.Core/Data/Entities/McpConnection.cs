namespace Modbot.Core.Data.Entities;

/// <summary>
/// An AI app that registered itself with Modbot's MCP server so its users can sign in
/// (MCP server design; RFC 7591 dynamic client registration).
/// </summary>
/// <remarks>
/// <para>
/// A hosted chat such as Claude.ai or ChatGPT registers once, on its own, before the first person
/// signs in through it. Modbot keeps the name it gave and the addresses it may be sent back to,
/// and nothing else about it. A registration nobody ever signed in through is deleted after a day,
/// so an anonymous endpoint cannot fill the table.
/// </para>
/// <para>
/// A client that asked for a secret gets one, and only its hash is stored. A client that asked
/// for none is a public client, which is what most AI apps are.
/// </para>
/// </remarks>
public class McpClient
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>What the app called itself, shown on the sign-in page and in the connections list.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>SHA-256 of the client secret, lowercase hex. Null for a public client.</summary>
    public string? SecretHash { get; set; }

    /// <summary>The addresses the app may be sent back to after sign-in, as a JSON array of strings.</summary>
    public string RedirectUris { get; set; } = "[]";

    /// <summary>Where the app's own information page is, when it gave one.</summary>
    public string? ClientUri { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// A sign-in the person approved but the app has not yet exchanged for tokens. Lives ten minutes
/// and is used once.
/// </summary>
public class McpAuthorizationCode
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>SHA-256 of the code, lowercase hex.</summary>
    public string CodeHash { get; set; } = string.Empty;

    public Guid ClientId { get; set; }

    public Guid UserId { get; set; }

    /// <summary>The address the code was sent to. The exchange must name the same one.</summary>
    public string RedirectUri { get; set; } = string.Empty;

    /// <summary>The PKCE challenge (S256) the exchange must answer.</summary>
    public string CodeChallenge { get; set; } = string.Empty;

    /// <summary>The MCP server address the app asked for, so the token is only good there.</summary>
    public string? Resource { get; set; }

    public string Scope { get; set; } = string.Empty;

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? UsedAt { get; set; }
}

/// <summary>
/// One connection between a person and an AI app: the tokens the app holds to act as that person
/// on the MCP server.
/// </summary>
/// <remarks>
/// <para>
/// Only hashes of the tokens are stored. The access token is short-lived; the refresh token is
/// replaced each time it is used, so a copy of an old one is worthless. Revoking the connection
/// ends both at once, whichever side asked.
/// </para>
/// <para>
/// What the app may do is never stored here: on every request the person's own permissions are
/// read again, exactly as for a session, so a demoted moderator's connected app is demoted with
/// them.
/// </para>
/// </remarks>
public class McpGrant
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid ClientId { get; set; }

    public McpClient? Client { get; set; }

    /// <summary>The person the app acts as. Requests made with the tokens are attributed to them.</summary>
    public Guid UserId { get; set; }

    public string Scope { get; set; } = string.Empty;

    /// <summary>SHA-256 of the current access token, lowercase hex.</summary>
    public string AccessTokenHash { get; set; } = string.Empty;

    public DateTimeOffset AccessExpiresAt { get; set; }

    /// <summary>SHA-256 of the current refresh token, lowercase hex.</summary>
    public string RefreshTokenHash { get; set; } = string.Empty;

    /// <summary>When the refresh token stops working, and with it the whole connection.</summary>
    public DateTimeOffset RefreshExpiresAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Written at most once a minute, like an API key's.</summary>
    public DateTimeOffset? LastUsedAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>Whether the connection is still open at <paramref name="now"/>: not revoked and not past its refresh expiry.</summary>
    public bool IsOpen(DateTimeOffset now) => RevokedAt is null && now < RefreshExpiresAt;
}
