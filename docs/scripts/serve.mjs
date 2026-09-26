// Serves the static site `next build` writes to out/, for the Docker image and `npm start`.
//
// Plain Node with no packages: the site is only files, so the server needs to map a path to a
// file, send the right content type, answer the health check and fall back to the 404 page.
//
// PORT sets the port (default 8080). GET /health answers 200 "ok" for the platform's health check.

import { createReadStream } from 'node:fs';
import { stat } from 'node:fs/promises';
import { createServer } from 'node:http';
import { extname, join, normalize, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(fileURLToPath(new URL('../out', import.meta.url)));
const port = Number.parseInt(process.env.PORT ?? '', 10) || 8080;

/**
 * Pages that moved. The site is static files, so the old address is answered here with a
 * permanent redirect rather than by a page that says "this moved". Keys and values are paths
 * without a trailing slash; the request's own slash is ignored when matching.
 */
const moved = new Map([
  ['/self-hosting/open-rooms', '/self-hosting/open-instances/'],
  // The app's analytics pages were renamed: My Server became Discord.
  ['/discord/messages-and-my-server', '/discord/messages-and-analytics/'],
  ['/screenshots/my-team.png', '/screenshots/team.png'],
]);

const types = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.txt': 'text/plain; charset=utf-8',
  '.svg': 'image/svg+xml',
  '.png': 'image/png',
  '.ico': 'image/x-icon',
  '.webp': 'image/webp',
  '.woff': 'font/woff',
  '.woff2': 'font/woff2',
};

async function fileAt(path) {
  try {
    const info = await stat(path);
    return info.isFile() ? { path, size: info.size } : null;
  } catch {
    return null;
  }
}

/** The file a URL path names: the file itself, its .html, or its folder's index.html. */
async function find(urlPath) {
  const local = normalize(join(root, decodeURIComponent(urlPath)));
  if (local !== root && !local.startsWith(root + sep)) return null;

  return (
    (await fileAt(local)) ??
    (await fileAt(`${local}.html`)) ??
    (await fileAt(join(local, 'index.html'))) ??
    (await fileAt(segmentFile(local)))
  );
}

/**
 * Next.js asks for a page's prefetch data as `__next.<layout>.<segment>.__PAGE__.txt`, but the
 * export writes it as `__next.<layout>/<segment>/__PAGE__.txt`. Without this every link hover
 * is a 404 and the click falls back to loading the whole page.
 */
function segmentFile(local) {
  const slash = local.lastIndexOf(sep);
  const name = local.slice(slash + 1);
  if (!name.startsWith('__next.') || !name.endsWith('.txt')) return local;

  const parts = name.slice(0, -'.txt'.length).split('.');
  if (parts.length < 3) return local;

  return join(local.slice(0, slash), `${parts[0]}.${parts[1]}`, ...parts.slice(2, -1), `${parts.at(-1)}.txt`);
}

function send(res, status, file, method, headers = {}) {
  const ext = extname(file.path);
  // The search index is written without an extension.
  const type = types[ext] ?? (file.path.endsWith(`${sep}search`) ? types['.json'] : 'application/octet-stream');

  res.writeHead(status, { 'Content-Type': type, 'Content-Length': file.size, ...headers });
  if (method === 'HEAD') return res.end();
  createReadStream(file.path).pipe(res);
}

const server = createServer(async (req, res) => {
  const method = req.method ?? 'GET';
  const url = new URL(req.url ?? '/', 'http://localhost');

  if (url.pathname === '/health') {
    res.writeHead(200, { 'Content-Type': 'text/plain; charset=utf-8' });
    return res.end(method === 'HEAD' ? undefined : 'ok');
  }

  if (method !== 'GET' && method !== 'HEAD') {
    res.writeHead(405, { Allow: 'GET, HEAD' });
    return res.end();
  }

  const to = moved.get(url.pathname.replace(/\/+$/, ''));
  if (to) {
    res.writeHead(301, { Location: to + url.search, 'Cache-Control': 'public, max-age=300' });
    return res.end();
  }

  let file;
  try {
    file = await find(url.pathname);
  } catch {
    file = null; // a malformed escape in the path
  }

  if (file) {
    // Next.js puts a content hash in every file name under _next/static, so those never change.
    const immutable = url.pathname.startsWith('/_next/static/');
    return send(res, 200, file, method, {
      'Cache-Control': immutable ? 'public, max-age=31536000, immutable' : 'public, max-age=300',
    });
  }

  const notFound = await fileAt(join(root, '404.html'));
  if (notFound) return send(res, 404, notFound, method);

  res.writeHead(404, { 'Content-Type': 'text/plain; charset=utf-8' });
  res.end('Not found');
});

server.listen(port, () => {
  console.log(`Modbot docs on port ${port}`);
});

for (const signal of ['SIGINT', 'SIGTERM']) {
  process.on(signal, () => server.close(() => process.exit(0)));
}
