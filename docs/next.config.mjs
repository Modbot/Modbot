import { createMDX } from 'fumadocs-mdx/next';

const withMDX = createMDX();

/**
 * A static export: `next build` writes the whole site, API reference and search index included, to
 * `out/`. The API reference pages are rendered at build time from docs/openapi/modbot.json, and
 * the search index is a JSON file, so nothing needs a server at request time.
 *
 * Trailing slashes make every page `<path>/index.html`, which any static file server can serve
 * without rewrite rules.
 *
 * @type {import('next').NextConfig}
 */
const config = {
  output: 'export',
  trailingSlash: true,
  reactStrictMode: true,
};

export default withMDX(config);
