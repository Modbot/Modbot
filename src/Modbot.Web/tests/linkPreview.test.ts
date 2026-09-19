import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import path from 'node:path'
import { test } from 'node:test'

/**
 * The app is one file for every path, so the link preview a chat app draws when a moderator pastes
 * a page into a channel is whatever this head says, and it names the product only.
 *
 * It has to. A crawler runs no script, so a title the app set after it loaded is never read; and the
 * links moderators paste carry a person, a ban or a case file in them, which a preview must not
 * spell out in a channel the app itself keeps behind a sign-in.
 */
const html = readFileSync(path.resolve(import.meta.dirname, '..', 'index.html'), 'utf8')

function tag(name: string): string | null {
  const match = new RegExp(`<meta (?:property|name)="${name}" content="([^"]*)"`).exec(html)
  return match ? match[1] : null
}

test('the page has a title, a description, an image and a site name', () => {
  assert.equal(tag('og:type'), 'website')
  assert.equal(tag('og:site_name'), 'Modbot')
  assert.ok(tag('og:title'))
  assert.ok(tag('og:description'))
  assert.ok(tag('og:image'))

  assert.equal(tag('twitter:card'), 'summary_large_image')
  assert.ok(tag('twitter:title'))
  assert.ok(tag('twitter:description'))
  assert.ok(tag('twitter:image'))
})

test('the image address is whole, because a crawler cannot work out a relative one', () => {
  assert.match(tag('og:image') ?? '', /^https:\/\//)
  assert.match(tag('twitter:image') ?? '', /^https:\/\//)
})

test('the preview names the product, not the page', () => {
  // One deployment's address is a setting most installs leave empty, so there is no address to
  // claim as this page's own, and nothing here may name a page of the app.
  assert.equal(tag('og:url'), null)
  assert.equal(tag('og:title'), 'Modbot')
  assert.equal(tag('twitter:title'), 'Modbot')
})
