using System.Text.Json;
using Modbot.Core.Google;
using Modbot.TestSupport;

namespace Modbot.Core.Tests.Google;

/// <summary>
/// The key file (Google Calendar design §3.1): only a service account key with its address, key id
/// and a private key that loads is taken, and only those parts are kept.
/// </summary>
public class GoogleKeyFileTests
{
    [Fact]
    public void AServiceAccountKeyIsTaken_AndOnlyItsUsefulPartsAreKept()
    {
        var (json, rsa) = FakeGoogle.KeyFile(tokenUri: "https://collector.example/steal");
        using var _ = rsa;

        var key = GoogleKeyFile.Parse(json, out var error);

        Assert.Null(error);
        Assert.NotNull(key);
        Assert.Equal("modbot@test-project.iam.gserviceaccount.com", key.ClientEmail);
        Assert.Equal("0123456789abcdef", key.KeyId);
        Assert.Equal("test-project", key.ProjectId);
        Assert.StartsWith("-----BEGIN PRIVATE KEY-----", key.PrivateKeyPem, StringComparison.Ordinal);

        // The token address in the file is not kept anywhere: there is nowhere to keep it.
        Assert.DoesNotContain(typeof(GoogleKeyFile).GetProperties(), p => p.Name.Contains("Token", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("PRIVATE KEY", key.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("authorized_user")]
    [InlineData("external_account")]
    [InlineData("")]
    public void AnotherKindOfGoogleFileIsRefused(string type)
    {
        var (json, rsa) = FakeGoogle.KeyFile(type: type);
        using var _ = rsa;

        Assert.Null(GoogleKeyFile.Parse(json, out var error));
        Assert.Equal("This is not a Google key file.", error);
    }

    [Theory]
    [InlineData("client_email")]
    [InlineData("private_key")]
    [InlineData("private_key_id")]
    public void AFileWithoutAnAddressAKeyOrAKeyIdIsRefused(string missing)
    {
        var (json, rsa) = FakeGoogle.KeyFile();
        using var _ = rsa;

        var fields = JsonSerializer.Deserialize<Dictionary<string, string>>(json)!;
        fields.Remove(missing);

        Assert.Null(GoogleKeyFile.Parse(JsonSerializer.Serialize(fields), out var error));
        Assert.Equal("This is not a Google key file.", error);
    }

    [Fact]
    public void APrivateKeyThatDoesNotLoadIsRefused()
    {
        var (json, rsa) = FakeGoogle.KeyFile();
        using var _ = rsa;

        var fields = JsonSerializer.Deserialize<Dictionary<string, string>>(json)!;
        fields["private_key"] = "-----BEGIN PRIVATE KEY-----\nbm90IGEga2V5\n-----END PRIVATE KEY-----\n";

        Assert.Null(GoogleKeyFile.Parse(JsonSerializer.Serialize(fields), out var error));
        Assert.Equal("This is not a Google key file.", error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"type\":\"service_account\"}")]
    public void TextThatIsNotAKeyFileIsRefused(string text)
    {
        Assert.Null(GoogleKeyFile.Parse(text, out var error));
        Assert.Equal("This is not a Google key file.", error);
    }

    [Fact]
    public void AFileOver16KBIsRefusedBeforeItIsRead()
    {
        var big = "{\"type\":\"service_account\",\"padding\":\"" + new string('x', GoogleKeyFile.MaxBytes) + "\"}";

        Assert.Null(GoogleKeyFile.Parse(big, out var error));
        Assert.Equal("The key file is too big.", error);
    }
}
