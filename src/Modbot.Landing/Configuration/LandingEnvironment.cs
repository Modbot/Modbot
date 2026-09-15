namespace Modbot.Landing.Configuration;

/// <summary>The environment variables Modbot.Landing reads, and nothing else.</summary>
/// <param name="Port">The port to listen on. Railway injects it.</param>
public sealed record LandingEnvironment(int Port)
{
    public const string PortVariable = "PORT";
    public const int DefaultPort = 8080;

    public static LandingEnvironment Read(Func<string, string?>? get = null)
    {
        get ??= Environment.GetEnvironmentVariable;

        var port = int.TryParse(get(PortVariable), out var parsed) && parsed is > 0 and <= 65535
            ? parsed
            : DefaultPort;

        return new LandingEnvironment(port);
    }
}
