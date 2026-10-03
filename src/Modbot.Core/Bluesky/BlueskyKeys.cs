using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Modbot.Core.Bluesky;

/// <summary>
/// The ES256 keys and signed JWTs Bluesky's sign-in (OAuth) needs: Modbot's own client key, each
/// sign-in's DPoP key, PKCE, and random values. No package: <see cref="ECDsa"/> and
/// <see cref="Base64Url"/> do it.
/// </summary>
/// <remarks>
/// A key is kept as a JSON Web Key with its private part (<c>d</c>), and always handled as text, so it
/// can go into an encrypted column as it is. <see cref="PublicJwk"/> is the only form of a key that
/// ever leaves Modbot.
/// </remarks>
public static class BlueskyKeys
{
    /// <summary>The one signing method Modbot uses and names in its client document.</summary>
    public const string Algorithm = "ES256";

    /// <summary>A new P-256 key, as a private JSON Web Key with its <c>kid</c> (its own thumbprint).</summary>
    public static string NewPrivateJwk()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var parameters = key.ExportParameters(includePrivateParameters: true);

        var jwk = new JsonObject
        {
            ["kty"] = "EC",
            ["crv"] = "P-256",
            ["x"] = Base64Url.EncodeToString(parameters.Q.X),
            ["y"] = Base64Url.EncodeToString(parameters.Q.Y),
        };

        jwk["kid"] = Thumbprint(jwk);
        jwk["d"] = Base64Url.EncodeToString(parameters.D);

        return jwk.ToJsonString();
    }

    /// <summary>
    /// The public half of a key, for a client document's <c>jwks</c> or a DPoP proof's header: never
    /// the private part. Null when the text is not a key Modbot made.
    /// </summary>
    /// <param name="withUse">Add <c>kid</c>, <c>use</c> and <c>alg</c>, as a client document lists a key.</param>
    public static JsonObject? PublicJwk(string? privateJwk, bool withUse)
    {
        if (Read(privateJwk) is not { } jwk)
            return null;

        var shown = new JsonObject
        {
            ["kty"] = "EC",
            ["crv"] = "P-256",
            ["x"] = jwk.X,
            ["y"] = jwk.Y,
        };

        if (withUse)
        {
            shown["kid"] = jwk.Kid;
            shown["use"] = "sig";
            shown["alg"] = Algorithm;
        }

        return shown;
    }

    /// <summary>The key's id, as the client document and the client's signed proofs name it.</summary>
    public static string? KidOf(string? privateJwk) => Read(privateJwk)?.Kid;

    /// <summary>
    /// A signed JWT: <paramref name="header"/> gets <c>alg</c> ES256, and the signature is the raw
    /// 64-byte form JWS uses.
    /// </summary>
    public static string Sign(string privateJwk, JsonObject header, JsonObject payload)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(payload);

        var jwk = Read(privateJwk) ?? throw new CryptographicException("The Bluesky signing key could not be read.");

        header["alg"] = Algorithm;

        var head = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(header.ToJsonString()));
        var body = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(payload.ToJsonString()));
        var signed = Encoding.ASCII.GetBytes(head + "." + body);

        using var key = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = Base64Url.DecodeFromChars(jwk.X), Y = Base64Url.DecodeFromChars(jwk.Y) },
            D = Base64Url.DecodeFromChars(jwk.D),
        });

        var signature = key.SignData(signed, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return head + "." + body + "." + Base64Url.EncodeToString(signature);
    }

    /// <summary>A random value of <paramref name="bytes"/> bytes, base64url: a state, a jti, a PKCE verifier.</summary>
    public static string Random(int bytes = 32) => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(bytes));

    /// <summary>SHA-256 of the text, base64url: a PKCE challenge, or a DPoP proof's <c>ath</c>.</summary>
    public static string Sha256(string text) => Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(text)));

    /// <summary>The JWK thumbprint (RFC 7638) of an EC public key: its required members, in order.</summary>
    public static string Thumbprint(JsonObject publicJwk)
    {
        ArgumentNullException.ThrowIfNull(publicJwk);

        static string Member(JsonObject jwk, string name) => jwk[name]?.GetValue<string>() ?? string.Empty;

        var canonical = $$"""{"crv":"{{Member(publicJwk, "crv")}}","kty":"{{Member(publicJwk, "kty")}}","x":"{{Member(publicJwk, "x")}}","y":"{{Member(publicJwk, "y")}}"}""";
        return Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private sealed record Parts(string X, string Y, string D, string Kid);

    private static Parts? Read(string? privateJwk)
    {
        if (string.IsNullOrWhiteSpace(privateJwk))
            return null;

        try
        {
            return JsonNode.Parse(privateJwk) is JsonObject jwk
                && jwk["kty"]?.GetValue<string>() == "EC"
                && jwk["crv"]?.GetValue<string>() == "P-256"
                && jwk["x"]?.GetValue<string>() is { Length: > 0 } x
                && jwk["y"]?.GetValue<string>() is { Length: > 0 } y
                && jwk["d"]?.GetValue<string>() is { Length: > 0 } d
                    ? new Parts(x, y, d, jwk["kid"]?.GetValue<string>() ?? Thumbprint(jwk))
                    : null;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }
}
