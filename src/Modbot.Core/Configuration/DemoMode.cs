namespace Modbot.Core.Configuration;

/// <summary>
/// Whether this process is a public demo, and the one place that decides it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Read this before changing anything here.</strong> Demo mode has one security model and
/// it is the whole of it: <em>every visitor is an administrator</em>. There is no sign-in page, no
/// account, no invite and no permission check that can fail, because every request is served as a
/// staff account holding <c>Administrator</c>. A demo deployment must therefore never hold real
/// data about real people, and it never connects to VRChat or Discord.
/// </para>
/// <para>
/// That is only tolerable because it cannot be switched on over a deployment somebody set up for
/// real. <see cref="Decide"/> is the single gate. It is called once, during startup, before
/// anything is registered and before the first request is served, and nothing else in Modbot is
/// allowed to work out for itself whether this is a demo — everything asks <see cref="IsOn"/>.
/// </para>
/// <para>
/// A deployment counts as already set up when a staff account exists or the setup wizard was
/// finished, unless the database says it holds demo data — which only the demo seeder ever writes.
/// So the first boot of an empty database with <c>MODBOT_DEMO=1</c> becomes a demo and stays one;
/// a database with a real staff account in it ignores the variable completely.
/// </para>
/// </remarks>
public sealed class DemoMode
{
    public const string Variable = "MODBOT_DEMO";
    public const string ResetHoursVariable = "MODBOT_DEMO_RESET_HOURS";

    /// <summary>How often a demo puts its data back when nothing says otherwise.</summary>
    public const int DefaultResetHours = 24;

    private bool _decided;
    private bool _on;

    /// <summary>The demo administrator every visitor is served as. Fixed, so it survives a reset.</summary>
    public static Guid AdministratorId { get; } = new("d3300000-0000-4000-8000-000000000001");

    /// <summary>The name shown in the top bar and recorded as the actor of anything done here.</summary>
    public const string AdministratorUsername = "demo";

    /// <summary><c>MODBOT_DEMO</c> was set. On its own this means nothing — see <see cref="IsOn"/>.</summary>
    public bool Requested { get; init; }

    /// <summary>
    /// <c>MODBOT_DEMO_RESET_HOURS</c>: how many hours between automatic resets. 0 means never.
    /// </summary>
    public int ResetHours { get; init; } = DefaultResetHours;

    /// <summary>
    /// True only when the variable was set <em>and</em> the deployment was never set up for real.
    /// </summary>
    /// <exception cref="InvalidOperationException">Read before startup decided.</exception>
    public bool IsOn => _decided
        ? _on
        : throw new InvalidOperationException(
            "Demo mode was read before startup decided it. Call DemoMode.Decide first.");

    /// <summary>
    /// True once <see cref="Decide"/> has run. For the few places that run before it does.
    /// </summary>
    public bool Decided => _decided;

    public static DemoMode From(ModbotEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        return new DemoMode
        {
            Requested = environment.Demo,
            ResetHours = environment.DemoResetHours ?? DefaultResetHours,
        };
    }

    /// <summary>
    /// Works out, once, whether this process is a demo.
    /// </summary>
    /// <param name="hasStaffAccount">Whether any staff account exists.</param>
    /// <param name="onboardingComplete">Whether the setup wizard was finished.</param>
    /// <param name="holdsDemoData">Whether the database says the demo seeder filled it.</param>
    /// <returns>The same value <see cref="IsOn"/> then returns.</returns>
    public bool Decide(bool hasStaffAccount, bool onboardingComplete, bool holdsDemoData)
    {
        var setUpForReal = !holdsDemoData && (hasStaffAccount || onboardingComplete);

        _on = Requested && !setUpForReal;
        _decided = true;
        return _on;
    }

    /// <summary>One sentence for the startup log.</summary>
    public string Explain(bool hasStaffAccount, bool onboardingComplete, bool holdsDemoData)
    {
        if (!Requested)
            return "Demo mode is off.";

        if (IsOn)
        {
            return holdsDemoData
                ? "Demo mode is on. Everyone who opens this site is an administrator."
                : "Demo mode is on for an empty database. Everyone who opens this site is an administrator.";
        }

        var because = hasStaffAccount ? "a staff account exists" : "setup was finished";

        return $"{Variable} is set and is being ignored: this deployment is already set up ({because}). "
            + "Nothing was seeded or removed.";
    }
}
