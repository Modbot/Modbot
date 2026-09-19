import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import path from 'node:path'
import { test } from 'node:test'

/**
 * Every route is its own built file, and the link preview a chat app draws comes from the head of
 * whichever one the route served. A crawler runs no script, so a tag the app sets after it loads is
 * never read: if it is not written here, there is no preview.
 */
const pages = ['index.html', 'register.html', 'go.html']

const here = import.meta.dirname

function head(page: string): string {
  return readFileSync(path.resolve(here, '..', page), 'utf8')
}

function tag(html: string, name: string): string | null {
  const match = new RegExp(`<meta (?:property|name)="${name}" content="([^"]*)"`).exec(html)
  return match ? match[1] : null
}

test('every page has a title, a description, an image and a site name', () => {
  for (const page of pages) {
    const html = head(page)

    for (const name of ['og:type', 'og:site_name', 'og:url', 'og:title', 'og:description']) {
      assert.ok(tag(html, name), `${page} has no ${name}`)
    }

    assert.equal(tag(html, 'twitter:card'), 'summary_large_image', `${page} has no twitter card`)
    assert.ok(tag(html, 'twitter:title'), `${page} has no twitter:title`)
    assert.ok(tag(html, 'twitter:description'), `${page} has no twitter:description`)
    assert.ok(tag(html, 'twitter:image'), `${page} has no twitter:image`)
  }
})

test('the image and the address are whole, because a crawler cannot work out a relative one', () => {
  for (const page of pages) {
    const html = head(page)

    for (const name of ['og:url', 'og:image', 'twitter:image']) {
      assert.match(tag(html, name) ?? '', /^https:\/\//, `${page} has a ${name} that is not whole`)
    }
  }
})

test('the pages do not all say the same thing', () => {
  const titles = new Set(pages.map((page) => tag(head(page), 'og:title')))
  const descriptions = new Set(pages.map((page) => tag(head(page), 'og:description')))

  assert.equal(titles.size, pages.length)
  assert.equal(descriptions.size, pages.length)
})

test('the title in the head is the title in the preview', () => {
  for (const page of pages) {
    const html = head(page)
    const title = /<title>([^<]*)<\/title>/.exec(html)?.[1]

    assert.equal(tag(html, 'og:title'), title, `${page} previews a different title from its own`)
  }
})
