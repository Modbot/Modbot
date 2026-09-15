namespace Modbot.Core.Data.Entities;

/// <summary>
/// What one model costs, per million tokens, as the operator entered it. The table is
/// <c>ai_model_price</c>.
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
/// A cap on AI spend per day and per month, for everyone together, one role, or one account.
/// The table is <c>ai_spend_limit</c>.
/// </summary>
/// <remarks>
/// A role limit is a limit for each person holding the role, not a pot the role shares: it is
/// compared with that person's own spend, the same as a limit set on them directly (AI chat
/// design §10).
/// </remarks>
public class AiSpendLimit
{
    public const string Everyone = "everyone";
    public const string Role = "role";
    public const string User = "user";

    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary><see cref="Everyone"/>, <see cref="Role"/> or <see cref="User"/>.</summary>
    public string AppliesTo { get; set; } = Everyone;

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
