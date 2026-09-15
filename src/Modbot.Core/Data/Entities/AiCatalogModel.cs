namespace Modbot.Core.Data.Entities;

/// <summary>
/// One model on OpenRouter's public model list, as last fetched, for the model picker on the AI
/// settings pages. The table is <c>ai_catalog_model</c> (AI chat design §10.9).
/// </summary>
/// <remarks>
/// <para>
/// Kept beside <see cref="AiFetchedPrice"/> rather than in it: that table is what spend is priced
/// from and holds only models with a real price, while this one also lists routers whose price
/// varies and models with no price at all, so the picker can show them for what they are.
/// </para>
/// <para>
/// Replaced as a whole on every fetch: a model OpenRouter stops listing leaves this table, though
/// its fetched price stays in <see cref="AiFetchedPrice"/> so past spend keeps its price.
/// </para>
/// </remarks>
public class AiCatalogModel
{
    /// <summary>The model id, exactly as OpenRouter lists it, e.g. <c>anthropic/claude-sonnet-5</c>.</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>OpenRouter's name for it, e.g. <c>Anthropic: Claude Sonnet 5</c>. Null when it gave none.</summary>
    public string? Name { get; set; }

    /// <summary>The part of the id before the <c>/</c>, e.g. <c>anthropic</c>. Empty when the id has none.</summary>
    public string Maker { get; set; } = string.Empty;

    /// <summary>How many tokens a request and its answer may hold together.</summary>
    public int? ContextLength { get; set; }

    /// <summary>The most tokens one answer may be, from OpenRouter's <c>top_provider.max_completion_tokens</c>.</summary>
    public int? MaxOutputTokens { get; set; }

    /// <summary>What the model reads: <c>text</c>, <c>image</c>, <c>file</c>, <c>audio</c>, <c>video</c>.</summary>
    public List<string> InputModalities { get; set; } = [];

    /// <summary>What the model writes: <c>text</c>, <c>image</c>, <c>audio</c>.</summary>
    public List<string> OutputModalities { get; set; } = [];

    /// <summary>The request fields OpenRouter says the model takes, e.g. <c>tools</c>, <c>structured_outputs</c>.</summary>
    public List<string> SupportedParameters { get; set; } = [];

    /// <summary>When OpenRouter added the model (its <c>created</c>). Null when it gave none.</summary>
    public DateTimeOffset? AddedAt { get; set; }

    /// <summary>US dollars per million input tokens. Null when there is no usable price.</summary>
    public decimal? InputPerMillion { get; set; }

    /// <summary>US dollars per million cached input tokens. Null when OpenRouter lists none.</summary>
    public decimal? CachedInputPerMillion { get; set; }

    /// <summary>US dollars per million output tokens. Null when there is no usable price.</summary>
    public decimal? OutputPerMillion { get; set; }

    /// <summary>OpenRouter priced it <c>-1</c>: a router whose price depends on the model it picks.</summary>
    public bool PriceVaries { get; set; }

    /// <summary>
    /// Every price field OpenRouter listed, as a JSON object of name to US dollars per unit exactly
    /// as listed (per token for most, per request or per image for some), <c>-1</c> included.
    /// Time-of-day <c>overrides</c> are left out.
    /// </summary>
    public string Prices { get; set; } = "{}";

    public DateTimeOffset FetchedAt { get; set; }
}
