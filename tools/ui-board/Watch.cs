using System.Diagnostics;
using System.Net;

namespace Modbot.UiBoard;

/// <summary>
/// <c>--watch</c>: serves the board folder on a local port and draws it again whenever a source file
/// under the overlay, the companion or this tool is saved.
/// </summary>
/// <remarks>
/// The drawing is done by a second copy of this tool, built into a folder of its own and run as a
/// child process. The watcher itself never loads the code it watches, so rebuilding that code never
/// finds a file the watcher holds open, and a build that fails leaves the last pictures where they were.
/// </remarks>
internal static class Watch
{
    /// <summary>Folders (under the repo) whose files change what the board draws.</summary>
    private static readonly string[] Watched =
    [
        Path.Combine("src", "Modbot.Overlay"),
        Path.Combine("src", "Modbot.Companion"),
        Path.Combine("src", "Modbot.Companion.App"),
        Path.Combine("tools", "ui-board"),
    ];

    /// <summary>How long the files must be quiet before a draw starts, so one save (or a branch switch) is one draw.</summary>
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(800);

    private static readonly object Gate = new();
    private static DateTime _lastChange = DateTime.MinValue;
    private static bool _dirty;

    public static int Run(string repo, string folder, int port)
    {
        Directory.CreateDirectory(folder);

        HttpListener server = new();
        server.Prefixes.Add($"http://localhost:{port}/");

        try
        {
            server.Start();
        }
        catch (HttpListenerException ex)
        {
            Console.Error.WriteLine($"Could not listen on port {port}: {ex.Message} Use --port <number> for another one.");
            return 1;
        }

        var watchers = new List<FileSystemWatcher>();

        foreach (var relative in Watched)
        {
            var watcher = new FileSystemWatcher(Path.Combine(repo, relative)) { IncludeSubdirectories = true, EnableRaisingEvents = true };
            watcher.Changed += (_, e) => Touched(e.FullPath);
            watcher.Created += (_, e) => Touched(e.FullPath);
            watcher.Deleted += (_, e) => Touched(e.FullPath);
            watcher.Renamed += (_, e) => Touched(e.FullPath);
            watchers.Add(watcher);
        }

        _ = Task.Run(() => Serve(server, folder));

        Console.WriteLine($"Board: http://localhost:{port}/  (watching {string.Join(", ", Watched)})");

        // Draw once now, so the page has something to show, then again after every quiet spell.
        Draw(repo, folder, "Start");

        while (true)
        {
            Thread.Sleep(200);

            lock (Gate)
            {
                if (!_dirty || DateTime.UtcNow - _lastChange < Quiet)
                    continue;

                _dirty = false;
            }

            Draw(repo, folder, "Change seen");
        }
    }

    private static void Touched(string path)
    {
        // The tool's own pictures and the build folders change on every draw and mean nothing.
        var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (parts.Any(p => p is "bin" or "obj" or "out" or ".vs") || path.EndsWith('~') || path.EndsWith(".tmp", StringComparison.Ordinal))
            return;

        lock (Gate)
        {
            _dirty = true;
            _lastChange = DateTime.UtcNow;
        }
    }

    /// <summary>Builds the tool into a folder of its own, then runs that copy to draw into <paramref name="folder"/>.</summary>
    private static void Draw(string repo, string folder, string why)
    {
        var started = Stopwatch.StartNew();
        var built = Path.Combine(Path.GetTempPath(), "modbot-ui-board-render");
        var project = Path.Combine(repo, "tools", "ui-board", "Modbot.UiBoard.csproj");

        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {why}: building...");

        if (Child("dotnet", ["build", project, "-c", "Release", "-o", built, "-nologo", "-v", "q", "-clp:ErrorsOnly"], repo, out var buildOutput) != 0)
        {
            Console.Error.WriteLine("The build failed, so the pictures stay as they were:");
            Console.Error.WriteLine(Tail(buildOutput, 20));
            return;
        }

        if (Child("dotnet", [Path.Combine(built, "Modbot.UiBoard.dll"), "--out", folder], repo, out var drawOutput) is not (0 or 2))
        {
            Console.Error.WriteLine("The drawing stopped:");
            Console.Error.WriteLine(Tail(drawOutput, 20));
            return;
        }

        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {Tail(drawOutput, 1)} ({started.Elapsed.TotalSeconds:0.0} s)");
    }

    private static int Child(string file, IEnumerable<string> arguments, string workingFolder, out string output)
    {
        var start = new ProcessStartInfo(file) { WorkingDirectory = workingFolder, RedirectStandardOutput = true, RedirectStandardError = true };

        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {file}.");
        var text = new System.Text.StringBuilder();

        void Add(string? line)
        {
            if (line is not null)
                lock (text)
                    text.AppendLine(line);
        }

        process.OutputDataReceived += (_, e) => Add(e.Data);
        process.ErrorDataReceived += (_, e) => Add(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.WaitForExit();
        process.WaitForExit();

        output = text.ToString();
        return process.ExitCode;
    }

    private static string Tail(string text, int lines)
        => string.Join(Environment.NewLine, text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(lines));

    private static void Serve(HttpListener server, string folder)
    {
        while (true)
        {
            HttpListenerContext context;

            try
            {
                context = server.GetContext();
            }
            catch
            {
                return;
            }

            try
            {
                Answer(context, folder);
            }
            catch
            {
                // A browser that went away mid-answer is not worth a message.
            }
        }
    }

    private static void Answer(HttpListenerContext context, string folder)
    {
        var name = Uri.UnescapeDataString(context.Request.Url?.AbsolutePath ?? "/").TrimStart('/');

        if (name.Length == 0)
            name = "board.html";

        var path = Path.GetFullPath(Path.Combine(folder, name));

        // Only files inside the board folder, whatever the address says.
        if (!path.StartsWith(Path.GetFullPath(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
        {
            context.Response.StatusCode = 404;
            context.Response.Close();
            return;
        }

        byte[] bytes;

        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (IOException)
        {
            // Caught mid-write: ask again in a moment.
            context.Response.StatusCode = 503;
            context.Response.Close();
            return;
        }

        context.Response.ContentType = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".html" => "text/html; charset=utf-8",
            ".json" => "application/json",
            ".png" => "image/png",
            _ => "application/octet-stream",
        };
        context.Response.Headers["Cache-Control"] = "no-store";
        context.Response.ContentLength64 = bytes.Length;
        context.Response.OutputStream.Write(bytes);
        context.Response.Close();
    }
}
