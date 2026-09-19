// Writes the rendered pages into the built HTML, so the words are in the file a search engine or a
// link preview fetches, not only in what the script draws after it runs. The two pages that carry
// React then hydrate the same markup rather than drawing it again; the rest ship as they are.
//
// The licence page is rendered from LICENSE at the repository root, which is always there. The
// privacy page is rendered from PRIVACY_POLICY.md, which is not: while that file does not exist,
// privacy.html is removed from the build, the server answers /privacy with its 404, and the footer
// leaves the link out.

import { access, readFile, readdir, rm, writeFile } from 'node:fs/promises'
import path from 'node:path'
import { pathToFileURL } from 'node:url'
import { marked } from 'marked'

const here = import.meta.dirname
const out = path.resolve(here, '../../wwwroot')
const root = path.resolve(here, '../../../..')
const entry = pathToFileURL(path.resolve(here, '../node_modules/.prerender/entry-server.js')).href

// Headings get an id, so the policy's own "see the section below" links work on the page as well as
// they do on GitHub. The rule is GitHub's: lower case, punctuation dropped, spaces to dashes.
marked.use({
  renderer: {
    heading({ tokens, depth }) {
      const text = this.parser.parseInline(tokens)
      const id = text
        .replace(/<[^>]*>/g, '')
        .toLowerCase()
        .trim()
        .replace(/[^\w\s-]/g, '')
        .replace(/\s+/g, '-')

      return `<h${depth} id="${id}">${text}</h${depth}>\n`
    },
  },
})

const server = (await import(entry)) as {
  renderHome: (privacy: boolean) => string
  renderFeatures: (privacy: boolean) => string
  renderSelfHost: (privacy: boolean) => string
  renderAbout: (privacy: boolean, contributors: { name: string; url: string }[]) => string
  renderLicense: (privacy: boolean, text: string) => string
  renderInstances: (privacy: boolean) => string
  renderNoDiscord: (privacy: boolean) => string
  renderPrivacy: (html: string) => string
  renderNotFound: () => string
}

// Who has committed to the repository, asked once at build time so the About page does not have to
// be edited every time somebody new lands a change. People already named in about.json are left out,
// so the founder is not printed twice. A build with no network, or one GitHub rate-limits, simply
// ships the hand-written list: this is worth having, never worth failing a deploy over.
const REPOSITORY = 'Modbot/Modbot'

async function contributors(): Promise<{ name: string; url: string }[]> {
  const people = JSON.parse(await readFile(path.resolve(here, '../src/data/about.json'), 'utf8')) as {
    team: { name: string; url?: string }[]
    contributors: { name: string; url?: string }[]
  }

  // By name and by address, because a GitHub login and the name somebody goes by are often not the
  // same word: the founder is "bin" in about.json and "binn" on GitHub, and he belongs in one
  // section, not two.
  const named = [...people.team, ...people.contributors]
  const known = new Set([
    ...named.map((person) => person.name.toLowerCase()),
    ...named.flatMap((person) => (person.url ? [person.url.toLowerCase().replace(/\/$/, '')] : [])),
  ])

  try {
    const answer = await fetch(`https://api.github.com/repos/${REPOSITORY}/contributors?per_page=100`, {
      headers: { accept: 'application/vnd.github+json', 'user-agent': 'modbot-landing-build' },
      signal: AbortSignal.timeout(8000),
    })

    if (!answer.ok) throw new Error(`GitHub answered ${answer.status}`)

    const found = ((await answer.json()) as { login: string; html_url: string; type: string }[])
      .filter(
        (person) =>
          person.type !== 'Bot' &&
          !known.has(person.login.toLowerCase()) &&
          !known.has(person.html_url.toLowerCase().replace(/\/$/, '')),
      )
      .map((person) => ({ name: person.login, url: person.html_url }))

    console.log(`${found.length} contributors from ${REPOSITORY} beyond the ${known.size} about.json names`)
    return found
  } catch (reason) {
    console.log(`could not read ${REPOSITORY}'s contributors (${reason}): the about page keeps its own list`)
    return []
  }
}

const marker = '<!--app-->'

// The stylesheet goes inline: on a phone connection the round trip for it was most of the time to
// first paint, and at about 12 KB compressed it costs less than the request did. The headline's font
// is preloaded so the fallback face is swapped out as early as it can be.
const assets = await readdir(path.join(out, 'assets'))
const headlineFont = assets.find((f) => /^bricolage-grotesque-latin-wght-normal-.*\.woff2$/.test(f))
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

const policy = await access(path.join(root, 'PRIVACY_POLICY.md')).then(
  () => readFile(path.join(root, 'PRIVACY_POLICY.md'), 'utf8'),
  () => null,
)

const built = policy !== null
const mark = built ? ' data-privacy="yes"' : ''

await fill('index.html', server.renderHome(built), mark)
await fill('features.html', server.renderFeatures(built), mark)
await fill('self-host.html', server.renderSelfHost(built), mark)
await fill('about.html', server.renderAbout(built, await contributors()), mark)
await fill('license.html', server.renderLicense(built, await readFile(path.join(root, 'LICENSE'), 'utf8')), mark)
await fill('instances.html', server.renderInstances(built), mark)
await fill('discord.html', server.renderNoDiscord(built), mark)
await fill('404.html', server.renderNotFound())

if (policy !== null) {
  await fill('privacy.html', server.renderPrivacy(await marked.parse(policy)), mark)
} else {
  await rm(path.join(out, 'privacy.html'), { force: true })
  console.log('no PRIVACY_POLICY.md at the repository root: /privacy is left out')
}
