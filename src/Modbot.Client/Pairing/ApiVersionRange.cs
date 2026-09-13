using Modbot.Core;

namespace Modbot.Client.Pairing;

/// <summary>
/// The span of API versions something speaks, and the rule for finding common ground.
/// </summary>
/// <remarks>
/// <para>A moderator may be staff in several groups, each running its own Modbot on its own
/// operator's upgrade schedule. One client therefore has to speak to servers of different ages at
/// the same time, and the version is settled once, per server, at pairing — not per request.</para>
/// <para>When there is no overlap the client says so with <strong>both numbers named</strong>. A
/// version mismatch must never present as a parse error, a silent no-op, or reporting that quietly
/// stops: those are indistinguishable from the log parser having broken, and equally
/// unrecoverable.</para>
/// </remarks>
public readonly record struct ApiVersionRange(int Minimum, int Maximum)
{
    /// <summary>What this build of the client speaks.</summary>
    public static ApiVersionRange Client { get; } = new(ModbotVersion.ApiMinimum, ModbotVersion.Api);

    public bool IsEmpty => Maximum < Minimum;

    /// <summary>
    /// The highest version both sides speak, or <c>null</c> when the ranges do not overlap.
    /// </summary>
    public int? HighestInCommonWith(ApiVersionRange other)
    {
        var low = Math.Max(Minimum, other.Minimum);
        var high = Math.Min(Maximum, other.Maximum);

        return high >= low ? high : null;
    }

    public override string ToString() => Minimum == Maximum ? $"v{Minimum}" : $"v{Minimum}–v{Maximum}";
}
