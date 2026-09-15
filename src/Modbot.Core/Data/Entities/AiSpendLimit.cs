namespace Modbot.Core.Data.Entities;

/// <summary>
/// What one model costs, per million tokens, as the operator entered it. The table is
/// <c>ai_model_price</c>. An entered price always wins over a fetched one.
/// </summary>
public class AiModelPrice
{
    /// <summary>The model id, exactly as used in settings.</summary>
    public string Model { get; set; } = string.Empty;

    public decimal InputPerMillion { get; set; }

    /// <summary>Null means cached input costs the same as other input.</summary>
    public decimal? CachedInputPerMillion { get; set; }

    public decimal OutputPerMillion { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// What one model costs, per million tokens, as fetched from OpenRouter's public model list. The
/// table is <c>ai_fetched_price</c>.
/// </summary>
/// <remarks>
/// Kept apart from <see cref="AiModelPrice"/> so a fetch can never overwrite what the operator
/// entered. A model OpenRouter stops listing keeps its last fetched price.
/// </remarks>
public class AiFetchedPrice
{
    public string Model { get; set; } = string.Empty;

    public decimal InputPerMillion { get; set; }

    /// <summary>Null means cached input costs the same as other input.</summary>
    public decimal? CachedInputPerMillion { get; set; }

    public decimal OutputPerMillion { get; set; }

    public DateTimeOffset FetchedAt { get; set; }
}

/// <summary>
/// That a spend limit was reached in one day or month, so it is recorded once. The table is
/// <c>ai_limit_reached</c>.
/// </summary>
public class AiLimitReachedRecord
{
    /// <summary>Which limit, for whom and which period: <c>everyone:day</c>, <c>feature:moderation:month</c>.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>The start of the UTC day or month the limit was reached in.</summary>
    public DateTimeOffset PeriodStart { get; set; }

    public DateTimeOffset ReachedAt { get; set; }
}

/// <summary>
/// A cap on AI spend per day and per month, for everyone together, one feature, one role, or one
/// account. The table is <c>ai_spend_limit</c>.
/// </summary>
/// <remarks>
/// A role limit is a limit for each person holding the role, not a pot the role shares: it is
/// compared with that person's own spend, the same as a limit set on them directly. Role and user
/// limits are Chat's and count Chat's spend; a feature limit counts that feature's spend, and the
/// limit for everyone counts every feature together (AI chat design §10).
/// </remarks>
public class AiSpendLimit
{
    public const string Everyone = "everyone";
    public const string ForFeature = "feature";
    public const string Role = "role";
    public const string User = "user";

    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary><see cref="Everyone"/>, <see cref="ForFeature"/>, <see cref="Role"/> or <see cref="User"/>.</summary>
    public string AppliesTo { get; set; } = Everyone;

    /// <summary>The feature, e.g. <c>moderation</c>, for a <see cref="ForFeature"/> limit.</summary>
    public string? Feature { get; set; }

    public Guid? RoleId { get; set; }

    public ModbotRole? RoleRow { get; set; }

    public Guid? UserId { get; set; }

    public ModbotUser? UserRow { get; set; }

    /// <summary>Null means no daily limit.</summary>
    public decimal? PerDay { get; set; }

    /// <summary>Null means no monthly limit.</summary>
    public decimal? PerMonth { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
