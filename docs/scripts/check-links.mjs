// Checks every link inside the built site (out/) points at a page that exists, and that a link to a
// heading (#part) finds that heading. Run after `npm run build`; exits 1 and lists what is broken.
//
// It reads the HTML the build wrote rather than the MDX, so it also catches links made by
// components, the sidebar and the API reference, and it resolves paths the same way
// scripts/serve.mjs serves them.

import { readdir, readFile, stat } from 'node:fs/promises';
import { join, relative, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(fileURLToPath(new URL('../out', import.meta.url)));

async function* htmlFiles(dir) {
  for (const entry of await readdir(dir, { withFileTypes: true })) {
    const path = join(dir, entry.name);
    if (entry.isDirectory()) {
      if (entry.name !== '_next') yield* htmlFiles(path);
    } else if (entry.name.endsWith('.html')) {
      yield path;
    }
  }
}

async function isFile(path) {
  try {
    return (await stat(path)).isFile();
  } catch {
    return false;
  }
}

async function pageFor(urlPath) {
  const local = join(root, decodeURIComponent(urlPath));
  for (const candidate of [local, `${local}.html`, join(local, 'index.html')]) {
    if (await isFile(candidate)) return candidate;
  }
  return null;
}

const ids = new Map();
async function idsIn(file) {
  if (!ids.has(file)) {
    const html = await readFile(file, 'utf8');
    ids.set(file, new Set([...html.matchAll(/\sid="([^"]+)"/g)].map((m) => m[1])));
  }
  return ids.get(file);
}

const broken = [];
let checked = 0;

for await (const file of htmlFiles(root)) {
  const html = await readFile(file, 'utf8');
  const from = '/' + relative(root, file).split(sep).join('/');

  for (const [, raw] of html.matchAll(/\shref="([^"]*)"/g)) {
    const href = raw.replaceAll('&amp;', '&');
    if (!href.startsWith('/') || href.startsWith('//') || href.startsWith('/_next/')) continue;

    const [beforeHash, hash] = href.split('#', 2);
    const path = beforeHash.split('?', 1)[0] || '/';
    checked++;

    const target = await pageFor(path);
    if (!target) {
      broken.push(`${from}: ${href} (no such page)`);
      continue;
    }

    if (hash && !(await idsIn(target)).has(decodeURIComponent(hash))) {
      broken.push(`${from}: ${href} (no heading #${hash})`);
    }
  }
}

const unique = [...new Set(broken)];
if (unique.length > 0) {
  console.error(`${unique.length} broken link(s):`);
  for (const line of unique) console.error(`  ${line}`);
  process.exit(1);
}

console.log(`All ${checked} internal links resolve.`);
