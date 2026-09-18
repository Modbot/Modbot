namespace Modbot.Core.Configuration;

/// <summary>
/// Where this Modbot server asks what the newest release is.
/// </summary>
/// <remarks>
/// <para>
/// The same address as <see cref="ModbotCloudAddress"/> — Modbot Cloud, or whatever
/// <c>MODBOT_CLOUD_ENDPOINT</c> names — and <strong>deliberately not its
/// <see cref="ModbotCloudAddress.Disabled"/> flag</strong>.
/// </para>
/// <para>
/// <c>MODBOT_CLOUD_DISABLED</c> turns off the Cloud features: reporting, analytics, shipping app
/// logs, term lists, the public instances listing. Asking whether a newer Modbot exists is none of
/// those. It sends nothing about the deployment, it is answered the same way for everybody out of
/// one cached copy, and an operator who turned Cloud off to avoid being counted did not thereby ask
/// to stop being told that their server is out of date. Bundling the two would mean the
/// deployments least in touch with the project were also the ones never told about a security fix.
/// </para>
/// <para>
/// An operator who wants no outbound calls at all has a switch of their own, on the settings row,
/// and that one does turn this off. See <c>Settings.CheckForUpdates</c>.
/// </para>
/// </remarks>
/// <param name="Endpoint">Where the question goes.</param>
public sealed record ModbotUpdateAddress(Uri Endpoint)
{
    public static ModbotUpdateAddress Default { get; } = new(ModbotCloudAddress.DefaultEndpoint);

    public static ModbotUpdateAddress From(ModbotEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        // Same reading as ModbotCloudAddress, minus the flag. A deployment that points Cloud at its
        // own copy is asking that copy about releases too.
        var endpoint = Uri.TryCreate(environment.CloudEndpoint, UriKind.Absolute, out var parsed)
                       && parsed.Scheme is "http" or "https"
            ? parsed
            : ModbotCloudAddress.DefaultEndpoint;

        return new ModbotUpdateAddress(endpoint);
    }
}
