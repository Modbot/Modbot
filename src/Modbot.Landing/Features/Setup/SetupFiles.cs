namespace Modbot.Landing.Features.Setup;

/// <summary>
/// The two files somebody setting Modbot up on their own machine downloads: the install script at
/// <c>/get.sh</c>, and the Compose file that script fetches.
/// </summary>
/// <remarks>
/// They live in <c>Web/public</c>, so the Vite build copies them into <c>wwwroot</c> beside the
/// pages and the image carries them without a second copy step. They are read here rather than
/// left to the static file middleware because <c>.sh</c> and <c>.yml</c> are not types it knows,
/// and because both must arrive as plain text: a self-hoster is asked to read the script before
/// running it, and a browser that downloads a file instead of showing it makes that harder.
/// </remarks>
public sealed class SetupFiles(IWebHostEnvironment environment)
{
    public const string ScriptFile = "get.sh";
    public const string ComposeFile = "docker-compose.yml";

    public static readonly string[] All = [ScriptFile, ComposeFile];

    private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>
    /// The file, or null when it has not been built. Read once found; not remembered while missing,
    /// so a build that lands after start-up is picked up.
    /// </summary>
    public string? Find(string file)
    {
        lock (_gate)
        {
            if (_files.TryGetValue(file, out var known))
                return known;

            var root = environment.WebRootPath ?? Path.Combine(environment.ContentRootPath, "wwwroot");
            var path = Path.Combine(root, file);

            if (!File.Exists(path))
                return null;

            var text = File.ReadAllText(path);
            _files[file] = text;
            return text;
        }
    }
}
