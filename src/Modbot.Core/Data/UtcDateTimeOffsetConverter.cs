using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Modbot.Core.Data;

/// <summary>
/// Sends every <see cref="DateTimeOffset"/> to the database as UTC and reads it back unchanged.
/// </summary>
/// <remarks>
/// The same instant, written with offset zero, which is the only offset Npgsql accepts for a
/// <c>timestamp with time zone</c>. Nothing is lost: an offset is how a moment was written down,
/// not part of the moment. Applied to every DateTimeOffset property by
/// <see cref="ModbotContext.ConfigureConventions"/>.
/// </remarks>
public sealed class UtcDateTimeOffsetConverter : ValueConverter<DateTimeOffset, DateTimeOffset>
{
    public UtcDateTimeOffsetConverter()
        : base(value => value.ToUniversalTime(), value => value)
    {
    }
}
