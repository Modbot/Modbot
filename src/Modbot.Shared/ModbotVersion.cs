namespace Modbot.Core;

/// <summary>
/// Modbot's two version numbers, which answer different questions.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Release"/> is a calendar version, <c>YYYY.M.PATCH</c>, patch resetting monthly
/// (spec 2.7.1). Components are not zero-padded, so comparison must be componentwise and numeric —
/// string sorting puts <c>2026.1.10</c> before <c>2026.1.2</c>.
/// </para>
/// <para>
/// <see cref="Api"/> is a plain integer, incremented <strong>only</strong> on a breaking change to
/// the interfaces the Windows client depends on. It is deliberately not the calendar version:
/// releases ship constantly, compatibility changes rarely, and a client forced to match a calendar
/// version would break every time the server shipped a typo fix.
/// </para>
/// <para>
/// The server advertises a supported <em>range</em> and so does the client; they negotiate the
/// highest both support (spec 2.7.3). A moderator may be staff in several groups whose Modbots are
/// on different versions, so one client must speak to all of them.
/// </para>
/// </remarks>
public static partial class ModbotVersion
{
    // Release lives in the generated half of this class, ModbotVersion.g.cs, written by
    // Modbot.Shared.csproj from the ModbotRelease build property. The development default is set
    // there; a release build passes the real number in.

    /// <summary>Current API version.</summary>
    public const int Api = 1;

    /// <summary>
    /// Oldest API version this server still speaks. Servers support the current version plus at
    /// least the two previous, and never drop one under 12 months old — that window is what makes
    /// "install the newest client" reliably correct.
    /// </summary>
    public const int ApiMinimum = 1;
}
