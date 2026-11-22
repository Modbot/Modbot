using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Modbot.UiBoard;

/// <summary>
/// Draws every tile in <see cref="Names"/> into a folder and writes the board page and
/// <c>manifest.json</c> beside the pictures. The manifest is written last, so a page that sees a new
/// version finds every picture in place.
/// </summary>
internal static class Board
{
    /// <summary>The widest a picture is shown on the page; a click opens it at full size.</summary>
    private const int MostShownWidth = 480;

    private sealed record Drawn(int Width, int Height, string Hash, string? Error);

    /// <summary>Draws every tile. A tile that fails shows why on the board instead of a picture; the rest still draw. Returns how many failed.</summary>
    public static int Render(string folder)
    {
        Directory.CreateDirectory(folder);

        var draws = Surfaces.All();
        var drawn = new Dictionary<string, Drawn>();
        var failed = 0;

        foreach (var tile in Names.Tiles)
        {
            var path = Path.Combine(folder, tile.Id + ".png");

            try
            {
                using var picture = draws[tile.Id]();
                using var bytes = new MemoryStream();
                picture.Save(bytes);
                var data = bytes.ToArray();

                // Written only when it changed, so a file watcher or a browser sees nothing for an unchanged picture.
                if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(data))
                    File.WriteAllBytes(path, data);

                drawn[tile.Id] = new Drawn(picture.PixelSize.Width, picture.PixelSize.Height, Convert.ToHexString(SHA256.HashData(data))[..12], null);
                Console.WriteLine($"  {tile.Id}.png {picture.PixelSize.Width}x{picture.PixelSize.Height}");
            }
            catch (Exception ex)
            {
                failed++;
                drawn[tile.Id] = new Drawn(0, 0, "error-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ex.Message)))[..8], ex.GetType().Name + ": " + ex.Message);
                Console.Error.WriteLine($"  {tile.Id} failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        var version = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join('|', Names.Tiles.Select(t => $"{t.Id}={drawn[t.Id].Hash}:{t.Name}:{t.Look}:{t.Side}")))))[..12];

        WriteAtomic(Path.Combine(folder, "board.html"), Page(drawn, version));
        WriteAtomic(Path.Combine(folder, "manifest.json"), JsonSerializer.Serialize(new
        {
            version,
            tiles = drawn.ToDictionary(p => p.Key, p => new { p.Value.Width, p.Value.Height, p.Value.Hash, p.Value.Error }),
        }));

        Console.WriteLine($"Board {version}: {drawn.Count - failed} of {drawn.Count} pictures drawn into {folder}");
        return failed;
    }

    private static void WriteAtomic(string path, string text)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, text, new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    private static string Page(Dictionary<string, Drawn> drawn, string version)
    {
        var body = new StringBuilder();

        foreach (var group in Names.Groups)
        {
            body.Append("<section class=\"group\"><h2>").Append(Html(group.Name)).Append("</h2>\n");

            foreach (var row in group.Rows)
            {
                body.Append("<section class=\"row\"><h3>").Append(Html(row.Name)).Append("</h3><div class=\"tiles\">\n");

                foreach (var tile in row.Tiles)
                    Figure(body, tile, drawn[tile.Id]);

                body.Append("</div></section>\n");
            }

            body.Append("</section>\n");
        }

        return Template.Replace("%VERSION%", version).Replace("%BODY%", body.ToString());
    }

    private static void Figure(StringBuilder body, Tile tile, Drawn d)
    {
        var side = tile.Side switch { Side.Vr => "VR", Side.Desktop => "Desktop", _ => "Companion app" };
        var size = d.Width > 0 ? $"{d.Width}x{d.Height}" : "not drawn";
        var caption = string.Join(" · ", new[] { side, tile.Look, size }.Where(s => s.Length > 0));
        var src = $"{tile.Id}.png?h={d.Hash}";

        body.Append("<figure class=\"tile ").Append(tile.Side.ToString().ToLowerInvariant()).Append("\" data-id=\"").Append(Html(tile.Id))
            .Append("\" data-hash=\"").Append(d.Hash).Append("\">")
            .Append("<figcaption>").Append(Html(caption)).Append("</figcaption>");

        if (d.Error is { } error)
            body.Append("<p class=\"error\">").Append(Html(error)).Append("</p>");

        if (d.Error is null)
        {
            body.Append("<a href=\"").Append(src).Append("\" target=\"_blank\"><img src=\"").Append(src).Append("\" width=\"")
                .Append(Math.Min(d.Width, MostShownWidth)).Append("\" alt=\"").Append(Html(tile.Name)).Append("\"></a>");
        }

        body.Append("</figure>\n");
    }

    private static string Html(string text) => WebUtility.HtmlEncode(text);

    private const string Template = """
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <title>Modbot UI board</title>
        <style>
        body { background: #101010; color: #e6e6e6; font: 14px/1.4 system-ui, sans-serif; margin: 0; padding: 16px 24px 48px; }
        h1 { font-size: 20px; margin: 0 0 4px; }
        h2 { font-size: 18px; margin: 28px 0 8px; padding-bottom: 4px; border-bottom: 1px solid #333; }
        h3 { font-size: 15px; margin: 18px 0 6px; font-weight: 600; }
        .version { color: #888; margin-bottom: 8px; }
        .tiles { display: flex; flex-wrap: wrap; gap: 20px; align-items: flex-start; }
        .tile { margin: 0; }
        figcaption { color: #aaa; font-size: 12px; margin-bottom: 4px; }
        .tile.vr figcaption { color: #8fb4ff; }
        .tile.desktop figcaption { color: #9fd68f; }
        .tile.companion figcaption { color: #e0b080; }
        img { display: block; height: auto; border: 1px solid #2c2c2c; }
        .error { color: #ff7a7a; max-width: 480px; margin: 4px 0; }
        </style>
        </head>
        <body data-version="%VERSION%">
        <h1>Modbot UI board</h1>
        <div class="version">Version <span id="version">%VERSION%</span>. Made-up data; redraws by itself when the tool is watching.</div>
        <div id="board">
        %BODY%</div>
        <script>
        let version = document.body.dataset.version;

        async function refresh() {
          const html = await (await fetch('board.html?t=' + Date.now(), { cache: 'no-store' })).text();
          const doc = new DOMParser().parseFromString(html, 'text/html');
          const next = doc.getElementById('board');
          const before = new Map([...document.querySelectorAll('figure')].map(f => [f.dataset.id, f]));
          const loading = [];

          for (const figure of next.querySelectorAll('figure')) {
            const old = before.get(figure.dataset.id);
            if (old && old.dataset.hash === figure.dataset.hash) { figure.replaceWith(old); continue; }
            const img = figure.querySelector('img');
            if (img) loading.push(new Promise(done => { const p = new Image(); p.onload = p.onerror = done; p.src = img.getAttribute('src'); }));
          }

          await Promise.all(loading);
          document.getElementById('board').replaceWith(next);
          version = doc.body.dataset.version;
          document.getElementById('version').textContent = version;
        }

        async function poll() {
          try {
            const manifest = await (await fetch('manifest.json?t=' + Date.now(), { cache: 'no-store' })).json();
            if (manifest.version !== version) await refresh();
          } catch (e) { /* the file is being written, or the tool is not running: try again */ }
          setTimeout(poll, 3000);
        }

        if (location.protocol.startsWith('http')) poll();
        </script>
        </body>
        </html>
        """;
}
