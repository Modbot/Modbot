namespace Modbot.AI.Tests;

public class AiProvidersTests
{
    [Theory]
    [InlineData("openrouter", "https://openrouter.ai/api/v1")]
    [InlineData("xai", "https://api.x.ai/v1")]
    [InlineData("anthropic", "https://api.anthropic.com/v1/")]
    [InlineData("openai", "https://api.openai.com/v1")]
    public void EachHostedPresetFillsInItsProvidersAddress(string id, string endpoint)
    {
        var provider = AiProviders.Find(id);

        Assert.NotNull(provider);
        Assert.Equal(endpoint, provider.Endpoint);
        Assert.False(provider.AllowsHttp);

        // A preset's own address has to pass the checks the save runs, or choosing it and pressing
        // save would fail with nothing typed.
        Assert.Null(AiSettingsRules.EndpointProblem(provider, provider.Endpoint, out _));
    }

    [Fact]
    public void CustomIsOfferedLastAndStartsEmpty()
    {
        Assert.Equal("custom", AiProviders.All[^1].Id);
        Assert.Equal("", AiProviders.Custom.Endpoint);
        Assert.True(AiProviders.Custom.AllowsHttp);
    }

    [Fact]
    public void IdsAreUniqueAndFoundWhateverTheirCase()
    {
        Assert.Equal(AiProviders.All.Count, AiProviders.All.Select(p => p.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Same(AiProviders.XAi, AiProviders.Find(" XAI "));
        Assert.Null(AiProviders.Find("gemini"));
        Assert.Null(AiProviders.Find(null));
    }
}

public class AiSettingsRulesTests
{
    [Fact]
    public void AFullSetupPasses_AndIsTrimmed()
    {
        var check = AiSettingsRules.Check(true, "openrouter", " https://openrouter.ai/api/v1 ", " openai/gpt-4o-mini ", "key");

        Assert.True(check.Ok);
        Assert.Same(AiProviders.OpenRouter, check.Provider);
        Assert.Equal(new Uri("https://openrouter.ai/api/v1"), check.Endpoint);
        Assert.Equal("openai/gpt-4o-mini", check.Model);
    }

    [Fact]
    public void AnUnknownProviderIsRefused()
    {
        Assert.Equal("Choose a provider.", AiSettingsRules.Check(false, "nope", null, null, null).Error);
    }

    [Fact]
    public void WithAiOff_AnUnfinishedFormStillSaves()
    {
        var check = AiSettingsRules.Check(false, "custom", "", "", null);

        Assert.True(check.Ok);
        Assert.Null(check.Endpoint);
        Assert.Null(check.Model);
    }

    [Fact]
    public void WithAiOn_TheEndpointAndModelAreRequired()
    {
        Assert.Equal("Enter the endpoint address.", AiSettingsRules.Check(true, "custom", " ", "m", null).Error);
        Assert.Equal("Enter a model.", AiSettingsRules.Check(true, "openai", "https://api.openai.com/v1", null, null).Error);
    }

    [Fact]
    public void TheModelListDoesNotNeedAModel()
    {
        Assert.True(AiSettingsRules.Check(true, "openai", "https://api.openai.com/v1", null, null, requireModel: false).Ok);
    }

    /// <summary>M8 4.2: a local model server is a first-class setup, and those listen on plain http.</summary>
    [Theory]
    [InlineData("http://localhost:11434/v1")]
    [InlineData("http://192.168.1.20:8080/v1")]
    [InlineData("https://llm.example.org/v1")]
    public void ACustomEndpointMayBePlainHttp(string endpoint)
    {
        Assert.True(AiSettingsRules.Check(true, "custom", endpoint, "llama3.2", null).Ok);
    }

    [Theory]
    [InlineData("openrouter")]
    [InlineData("xai")]
    [InlineData("anthropic")]
    [InlineData("openai")]
    public void AHostedPresetRefusesPlainHttp(string provider)
    {
        var check = AiSettingsRules.Check(true, provider, "http://api.example.com/v1", "m", "key");

        Assert.Equal("Use an https:// address, or choose Custom for a local server.", check.Error);
    }

    [Theory]
    [InlineData("api.openai.com/v1")]
    [InlineData("ftp://example.com/v1")]
    [InlineData("not a url")]
    [InlineData("/v1")]
    public void SomethingThatIsNotAFullAddressIsRefused(string endpoint)
    {
        Assert.Equal(
            "The endpoint must be a full address starting with https://.",
            AiSettingsRules.Check(true, "custom", endpoint, "m", null).Error);
    }

    [Theory]
    [InlineData("https://example.com/v1?x=1")]
    [InlineData("https://example.com/v1#top")]
    public void AQueryOrFragmentIsRefused(string endpoint)
    {
        Assert.Equal("The endpoint address cannot contain ? or #.", AiSettingsRules.Check(true, "custom", endpoint, "m", null).Error);
    }

    [Fact]
    public void CredentialsInTheAddressAreRefused()
    {
        Assert.Equal(
            "Put the API key in the API key field, not in the address.",
            AiSettingsRules.Check(true, "custom", "https://user:secret@example.com/v1", "m", null).Error);
    }

    [Fact]
    public void OverlongValuesAreRefused()
    {
        Assert.Equal("The API key is too long.",
            AiSettingsRules.Check(true, "openai", "https://api.openai.com/v1", "m", new string('k', AiSettingsRules.MaxApiKeyLength + 1)).Error);
        Assert.Equal("The model name is too long.",
            AiSettingsRules.Check(true, "openai", "https://api.openai.com/v1", new string('m', AiSettingsRules.MaxModelLength + 1), null).Error);
        Assert.Equal("The endpoint address is too long.",
            AiSettingsRules.Check(true, "custom", "https://example.com/" + new string('a', AiSettingsRules.MaxEndpointLength), "m", null).Error);
    }

    [Theory]
    [InlineData("https://api.anthropic.com/v1/", "https://api.anthropic.com/v1", true)]
    [InlineData("HTTPS://API.OPENAI.COM/v1", "https://api.openai.com/v1", true)]
    [InlineData("https://api.openai.com/v1", "https://api.openai.com.evil.example/v1", false)]
    [InlineData("https://api.openai.com/v1", "http://api.openai.com/v1", false)]
    [InlineData("https://api.openai.com/v1", "https://api.openai.com:8443/v1", false)]
    [InlineData("https://api.openai.com/v1", "https://api.openai.com/v2", false)]
    [InlineData(null, "https://api.openai.com/v1", false)]
    public void SameEndpointDecidesWhetherAStoredKeyMayBeSent(string? a, string b, bool same)
    {
        Assert.Equal(same, AiSettingsRules.SameEndpoint(a, b));
    }
}
