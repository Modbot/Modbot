namespace Modbot.Explore;

/// <summary>
/// Minimal .env reader. Hand-rolled rather than taking a dependency, because this is a scratch
/// tool and the format we need is four lines of KEY=VALUE.
/// </summary>
public static class DotEnv
{
    public static Dictionary<string, string>? Load(string path)
    {
        if (!File.Exists(path))
            return null;

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var split = line.IndexOf('=');
            if (split <= 0)
                continue;

            var key = line[..split].Trim();
            var value = line[(split + 1)..].Trim();

            // Strip surrounding quotes if present -- a 2FA secret pasted with quotes should work.
            if (value.Length >= 2 &&
                ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
                value = value[1..^1];

            if (value.Length > 0)
                values[key] = value;
        }

        return values;
    }
}
