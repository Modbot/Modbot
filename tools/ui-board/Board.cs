using System.Globalization;
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

        // The page is part of the version, so a change to a tab, a size or the page's own script shows too.
        var body = Body(drawn);
        var version = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join('|', Names.Tiles.Select(t => $"{t.Id}={drawn[t.Id].Hash}")) + body + Template)))[..12];

        WriteAtomic(Path.Combine(folder, "board.html"), Template.Replace("%BODY%", body).Replace("%VERSION%", version).Replace("%PAGE%", PageId));
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

    /// <summary>The page inside <c>#board</c>: the header (tabs, look switch, size slider) and one page per tab. The live refresh swaps all of it.</summary>
    private static string Body(Dictionary<string, Drawn> drawn)
    {
        var body = new StringBuilder();

        body.Append("<header><h1>Modbot UI board</h1><span class=\"version\">Version <span id=\"version\">%VERSION%</span></span>\n<nav class=\"tabs\">");

        foreach (var tab in Names.Tabs)
            body.Append("<button type=\"button\" data-tab=\"").Append(Slug(tab.Name)).Append("\">").Append(Html(tab.Name)).Append("</button>");

        body.Append("</nav>\n<nav class=\"looks\">")
            .Append("<button type=\"button\" data-look=\"modbot\">").Append(Html(Names.ModbotLook)).Append("</button>")
            .Append("<button type=\"button\" data-look=\"vrchat\">").Append(Html(Names.VRChatLook)).Append("</button>")
            .Append("<button type=\"button\" data-look=\"both\">Both</button></nav>\n")
            .Append("<label class=\"size\">Size <input id=\"size\" type=\"range\" min=\"20\" max=\"200\" step=\"5\"> <output id=\"percent\"></output></label></header>\n");

        foreach (var tab in Names.Tabs)
        {
            body.Append("<section class=\"page\" data-tab=\"").Append(Slug(tab.Name)).Append("\" data-percent=\"").Append(tab.DefaultPercent).Append("\">\n");

            foreach (var row in tab.Rows)
            {
                body.Append("<section class=\"row\"><h3>").Append(Html(row.Name)).Append("</h3><div class=\"tiles\">\n");

                // The tiles of one look stay together, so the VR picture and the desktop one of a surface are side by side.
                foreach (var look in row.Tiles.GroupBy(t => t.Look))
                {
                    body.Append("<div class=\"pair\" data-look=\"").Append(LookKey(look.Key)).Append("\">");

                    foreach (var tile in look)
                        Figure(body, tile, tab.Panel, drawn[tile.Id]);

                    body.Append("</div>\n");
                }

                body.Append("</div></section>\n");
            }

            body.Append("</section>\n");
        }

        return body.ToString();
    }

    private static void Figure(StringBuilder body, Tile tile, Panel panel, Drawn d)
    {
        var side = tile.Side switch { Side.Vr => "VR", Side.Desktop => "Desktop", _ => "Companion app" };
        var size = d.Width > 0 ? $"{d.Width}x{d.Height}" : "not drawn";
        var caption = string.Join(" · ", new[] { side, tile.Look, size }.Where(s => s.Length > 0));
        var src = $"{tile.Id}.png?h={d.Hash}";
        var shown = Sizes.Shown(tile, panel, d.Width).ToString("0.#", CultureInfo.InvariantCulture);

        body.Append("<figure class=\"tile ").Append(tile.Side.ToString().ToLowerInvariant()).Append("\" data-id=\"").Append(Html(tile.Id))
            .Append("\" data-hash=\"").Append(d.Hash).Append("\" style=\"--w:").Append(shown).Append("\">")
            .Append("<figcaption>").Append(Html(caption)).Append("</figcaption>");

        if (d.Error is { } error)
            body.Append("<p class=\"error\">").Append(Html(error)).Append("</p>");

        if (d.Error is null)
            body.Append("<img src=\"").Append(src).Append("\" alt=\"").Append(Html(tile.Name)).Append("\">");

        body.Append("</figure>\n");
    }

    /// <summary>The tab's name as the page's script knows it.</summary>
    private static string Slug(string name) => name.ToLowerInvariant().Replace(' ', '-');

    /// <summary>A tile's look as the page's script knows it; the companion's pictures have none and show in every look.</summary>
    private static string LookKey(string look) => look == Names.ModbotLook ? "modbot" : look == Names.VRChatLook ? "vrchat" : "";

    private static string Html(string text) => WebUtility.HtmlEncode(text);

    /// <summary>Names the page's own script and style, so a page that is open can tell its code changed and load again.</summary>
    private static readonly string PageId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Template)))[..12];

    private const string Template = """
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <title>Modbot UI board</title>
        <style>
        /* --scale is the size slider; --w on a tile is its width at 100 percent (Sizes.cs). */
        body { --scale: 1; background: #101010; color: #e6e6e6; font: 14px/1.4 system-ui, sans-serif; margin: 0; padding: 0 24px 48px; }
        header { position: sticky; top: 0; z-index: 5; display: flex; flex-wrap: wrap; align-items: center; gap: 8px 24px; padding: 10px 0; background: #101010; border-bottom: 1px solid #333; }
        h1 { font-size: 18px; margin: 0; }
        h3 { font-size: 15px; margin: 12px 0 6px; font-weight: 600; }
        .version { color: #888; }
        nav { display: flex; gap: 4px; }
        button { font: inherit; color: #ccc; background: #1c1c1c; border: 1px solid #333; border-radius: 6px; padding: 5px 12px; cursor: pointer; }
        button.on { background: #2f4a7a; border-color: #4a6fb0; color: #fff; }
        .size { display: flex; align-items: center; gap: 8px; color: #aaa; }
        output { min-width: 3.5em; }
        .page { display: flex; flex-wrap: wrap; align-items: flex-start; gap: 0 32px; }
        .page[hidden] { display: none; }
        .tiles, .pair { display: flex; gap: 16px; align-items: flex-start; }
        .tiles { flex-wrap: wrap; }
        body[data-look="modbot"] .pair[data-look="vrchat"], body[data-look="vrchat"] .pair[data-look="modbot"] { display: none; }
        .tile { margin: 0; }
        figcaption { color: #aaa; font-size: 12px; margin-bottom: 4px; }
        .tile.vr figcaption { color: #8fb4ff; }
        .tile.desktop figcaption { color: #9fd68f; }
        .tile.companion figcaption { color: #e0b080; }
        .tile img { display: block; box-sizing: border-box; width: calc(var(--w) * var(--scale) * 1px); height: auto; border: 1px solid #2c2c2c; cursor: zoom-in; }
        .error { color: #ff7a7a; max-width: 480px; margin: 4px 0; }
        #full { position: fixed; inset: 0; z-index: 10; display: flex; overflow: auto; background: rgba(0, 0, 0, 0.88); cursor: zoom-out; }
        #full[hidden] { display: none; }
        #full img { margin: auto; max-width: none; }
        </style>
        </head>
        <body data-version="%VERSION%" data-page="%PAGE%">
        <div id="board">
        %BODY%</div>
        <div id="full" hidden><img alt=""></div>
        <script>
        let version = document.body.dataset.version;

        // The tab, the look and each tab's size are kept in the browser; the live refresh swaps #board only, and show() puts them back on it.
        const KEY = 'modbot-ui-board';
        const LOOKS = ['modbot', 'vrchat', 'both'];
        const state = { tab: '', look: 'vrchat', sizes: {} };

        try {
          const saved = JSON.parse(localStorage.getItem(KEY) || '{}');
          if (typeof saved.tab === 'string') state.tab = saved.tab;
          if (LOOKS.includes(saved.look)) state.look = saved.look;
          if (saved.sizes && typeof saved.sizes === 'object') state.sizes = saved.sizes;
        } catch (e) { /* no storage, or something else is in it: start from the defaults */ }

        function save() {
          try { localStorage.setItem(KEY, JSON.stringify(state)); } catch (e) { /* no storage: the choice lasts until the page closes */ }
        }

        function show() {
          const pages = [...document.querySelectorAll('.page')];
          const page = pages.find(p => p.dataset.tab === state.tab) ?? pages[0];
          if (!page) return;
          state.tab = page.dataset.tab;
          const percent = Math.min(200, Math.max(20, Number(state.sizes[state.tab]) || Number(page.dataset.percent)));

          for (const p of pages) p.hidden = p !== page;
          for (const b of document.querySelectorAll('.tabs button')) b.classList.toggle('on', b.dataset.tab === state.tab);
          for (const b of document.querySelectorAll('.looks button')) b.classList.toggle('on', b.dataset.look === state.look);
          document.body.dataset.look = state.look;
          document.body.style.setProperty('--scale', percent / 100);
          document.getElementById('size').value = percent;
          document.getElementById('percent').textContent = percent + '%';
        }

        document.addEventListener('click', e => {
          const from = e.target instanceof Element ? e.target : null;
          const tab = from?.closest('.tabs button');
          const look = from?.closest('.looks button');
          const picture = from?.closest('.tile img');
          const full = document.getElementById('full');

          if (!full.hidden) full.hidden = true;
          else if (tab) { state.tab = tab.dataset.tab; save(); show(); window.scrollTo(0, 0); }
          else if (look) { state.look = look.dataset.look; save(); show(); }
          else if (picture) { full.querySelector('img').src = picture.getAttribute('src'); full.hidden = false; }
        });

        document.addEventListener('input', e => {
          if (e.target.id !== 'size') return;
          state.sizes[state.tab] = Number(e.target.value);
          save();
          show();
        });

        document.addEventListener('keydown', e => { if (e.key === 'Escape') document.getElementById('full').hidden = true; });

        async function refresh() {
          const html = await (await fetch('board.html?t=' + Date.now(), { cache: 'no-store' })).text();
          const doc = new DOMParser().parseFromString(html, 'text/html');
          if (doc.body.dataset.page !== document.body.dataset.page) { location.reload(); return; }
          const next = doc.getElementById('board');
          const before = new Map([...document.querySelectorAll('figure')].map(f => [f.dataset.id, f]));
          const loading = [];
          const keep = [];

          for (const figure of next.querySelectorAll('figure')) {
            const old = before.get(figure.dataset.id);
            if (old && old.dataset.hash === figure.dataset.hash) { keep.push([figure, old]); continue; }
            const img = figure.querySelector('img');
            if (img) loading.push(new Promise(done => { const p = new Image(); p.onload = p.onerror = done; p.src = img.getAttribute('src'); }));
          }

          await Promise.all(loading);
          // A kept picture takes the new size and caption, which can change while its picture does not.
          for (const [figure, old] of keep) {
            old.setAttribute('style', figure.getAttribute('style') || '');
            old.querySelector('figcaption').innerHTML = figure.querySelector('figcaption').innerHTML;
            figure.replaceWith(old);
          }
          document.getElementById('board').replaceWith(next);
          version = doc.body.dataset.version;
          show();
        }

        async function poll() {
          try {
            const manifest = await (await fetch('manifest.json?t=' + Date.now(), { cache: 'no-store' })).json();
            if (manifest.version !== version) await refresh();
          } catch (e) { /* the file is being written, or the tool is not running: try again */ }
          setTimeout(poll, 3000);
        }

        show();
        if (location.protocol.startsWith('http')) poll();
        </script>
        </body>
        </html>
        """;
}
