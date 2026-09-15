// Writes the rendered pages into the built HTML, so the words are in the file a search engine or a
// link preview fetches, not only in what the script draws after it runs. The browser then hydrates
// the same markup rather than drawing it again.
//
// The privacy page is rendered from PRIVACY_POLICY.md at the repository root. While that file does
// not exist, privacy.html is removed from the build, the server answers /privacy with its 404, and
// the footer leaves the link out.

import { access, readFile, readdir, rm, writeFile } from 'node:fs/promises'
import path from 'node:path'
import { pathToFileURL } from 'node:url'
import { marked } from 'marked'

const here = import.meta.dirname
const out = path.resolve(here, '../../wwwroot')
const policyFile = path.resolve(here, '../../../../PRIVACY_POLICY.md')
const entry = pathToFileURL(path.resolve(here, '../node_modules/.prerender/entry-server.js')).href

const server = (await import(entry)) as {
  renderLanding: (privacy: boolean) => string
  renderNotFound: () => string
  renderPrivacy: (html: string) => string
}

const marker = '<!--app-->'

// The stylesheet goes inline: on a phone connection the round trip for it was most of the time to
// first paint, and at about 12 KB compressed it costs less than the request did. The headline's font
// is preloaded so the fallback face is swapped out as early as it can be.
const assets = await readdir(path.join(out, 'assets'))
const headlineFont = assets.find((f) => /^ibm-plex-sans-condensed-latin-600-normal-.*\.woff2$/.test(f))
const stylesheet = /<link rel="stylesheet" crossorigin href="\/assets\/([^"]+\.css)">/

async function fill(file: string, html: string, attributes = '') {
  const target = path.join(out, file)
  let page = await readFile(target, 'utf8')

  if (!page.includes(marker)) throw new Error(`${file} has no ${marker} to fill.`)

  const link = stylesheet.exec(page)
  if (!link) throw new Error(`${file} links no built stylesheet to inline.`)
  const css = await readFile(path.join(out, 'assets', link[1]), 'utf8')
  const preload = headlineFont
    ? `<link rel="preload" href="/assets/${headlineFont}" as="font" type="font/woff2" crossorigin>`
    : ''
  // A function, so a "$" in the stylesheet is never read as a replacement pattern.
  page = page.replace(link[0], () => `${preload}<style>${css}</style>`)

  const filled = page.replace(marker, () => html).replace('<div id="root">', `<div id="root"${attributes}>`)
  await writeFile(target, filled)
  console.log(`prerendered ${file}`)
}

const policy = await access(policyFile).then(
  () => readFile(policyFile, 'utf8'),
  () => null,
)

await fill('index.html', server.renderLanding(policy !== null), policy !== null ? ' data-privacy="yes"' : '')
await fill('404.html', server.renderNotFound())

if (policy !== null) {
  await fill('privacy.html', server.renderPrivacy(await marked.parse(policy)))
} else {
  await rm(path.join(out, 'privacy.html'), { force: true })
  console.log('no PRIVACY_POLICY.md at the repository root: /privacy is left out')
}
