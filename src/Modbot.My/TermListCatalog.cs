namespace Modbot.My;

/// <summary>
/// Loads the curated term lists from disk once at startup and serves them from memory.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately trivial. These are static files; the only reason this is a class rather than
/// <c>UseStaticFiles</c> is to validate the ids and refuse anything that is not a known list,
/// rather than letting a path fragment reach the filesystem.
/// </para>
/// <para>
/// Foundation section 4.2.7: lists are data, not policy. Nothing here decides anything — a group
/// chooses which lists to import, reviews updates before applying them, and can disable individual
/// rules. This service only hands out files.
/// </para>
/// </remarks>
public sealed class TermListCatalog
{
    private readonly Dictionary<string, string> _lists = new(StringComparer.Ordinal);

    public string? Index { get; }
    public string? Schema { get; }

    public TermListCatalog(IWebHostEnvironment environment, ILogger<TermListCatalog> logger)
    {
        var dir = Path.Combine(environment.ContentRootPath, "termlists");
        if (!Directory.Exists(dir))
        {
            logger.LogWarning("Term list directory not found at {Directory}; Hub will serve nothing", dir);
            return;
        }

        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            var id = Path.GetFileNameWithoutExtension(file);
            var body = File.ReadAllText(file);

            if (id == "index") Index = body;
            else if (id == "_schema") Schema = body;
            else _lists[id] = body;
        }

        logger.LogInformation("Modbot Hub loaded {Count} term lists", _lists.Count);
    }

    /// <summary>Returns a list by id, or null. Unknown ids never touch the filesystem.</summary>
    public string? Get(string id) => _lists.GetValueOrDefault(id);
}
