import assert from 'node:assert/strict'
import { test } from 'node:test'
import {
  fileSlug,
  gifAddress,
  KLIPY_API,
  klipyCustomerId,
  klipyLocale,
  parseKlipyPage,
  searchAddress,
  searchKlipy,
} from '../src/lib/klipy.ts'

const file = (url: string, width = 200, height = 100) => ({ url, width, height })

const item = (id: number, title: string, sizes: Record<string, Record<string, unknown>>) => ({ id, title, file: sizes })

const sizeOf = (name: string) => ({
  gif: file(`https://static.klipy.com/ii/x/${name}.gif`, 220, 120),
  mp4: file(`https://static.klipy.com/ii/x/${name}.mp4`),
  webm: file(`https://static.klipy.com/ii/x/${name}.webm`),
})

test('the page keeps Klipy order and says whether another page follows', () => {
  const page = parseKlipyPage({
    result: true,
    data: {
      data: [item(3, 'Third', { md: sizeOf('c') }), item(1, 'First', { md: sizeOf('a') }), item(2, 'Second', { md: sizeOf('b') })],
      current_page: 2,
      has_next: true,
    },
  })
  assert.deepEqual(page.gifs.map((g) => g.id), ['3', '1', '2'])
  assert.equal(page.page, 2)
  assert.equal(page.hasNext, true)
})

test('the address carries height, width and the names of the mp4 and webm', () => {
  const page = parseKlipyPage({ data: { data: [item(1, ' Cat ', { md: sizeOf('cat') })] } })
  assert.equal(page.gifs[0].title, 'Cat')
  assert.equal(page.gifs[0].url, 'https://static.klipy.com/ii/x/cat.gif?hh=120&ww=220&mp4=cat&webm=cat')
  assert.equal(page.hasNext, false)
  assert.equal(page.page, 1)
})

test('the post uses the middle size and the grid the small one when it has it', () => {
  const page = parseKlipyPage({ data: { data: [item(1, 'Cat', { hd: sizeOf('hd'), md: sizeOf('md'), sm: sizeOf('sm') })] } })
  assert.match(page.gifs[0].url, /md\.gif\?/)
  assert.equal(page.gifs[0].previewUrl, 'https://static.klipy.com/ii/x/sm.gif')
})

test('a result without a GIF address is left out and nothing else moves', () => {
  const page = parseKlipyPage({
    data: { data: [item(1, 'A', { md: { mp4: file('https://static.klipy.com/a.mp4') } }), 'junk', null, item(2, 'B', { md: sizeOf('b') })] },
  })
  assert.deepEqual(page.gifs.map((g) => g.id), ['2'])
})

test('an answer that is not a page is an empty page', () => {
  assert.deepEqual(parseKlipyPage(null).gifs, [])
  assert.deepEqual(parseKlipyPage({ data: 'x' }).gifs, [])
})

test('the address keeps an existing query and adds the video names only when there are some', () => {
  assert.equal(gifAddress(file('https://static.klipy.com/a.gif?v=1', 10, 20), undefined, undefined), 'https://static.klipy.com/a.gif?v=1&hh=20&ww=10')
  assert.equal(fileSlug('https://static.klipy.com/ii/a.b.mp4?x=1'), 'a.b')
  assert.equal(fileSlug(undefined), undefined)
})

test('the locale is the country of the browser language', () => {
  assert.equal(klipyLocale('en-US'), 'us')
  assert.equal(klipyLocale('pt_BR'), 'br')
  assert.equal(klipyLocale('en'), '')
  assert.equal(klipyLocale(undefined), '')
})

test('a search asks for high content filtering with the customer and the locale', () => {
  const url = new URL(searchAddress('my-key', 'cat dance', 2, 'cust-1', 'us'))
  assert.equal(url.origin, KLIPY_API)
  assert.equal(url.pathname, '/api/v1/my-key/gifs/search')
  assert.equal(url.searchParams.get('q'), 'cat dance')
  assert.equal(url.searchParams.get('page'), '2')
  assert.equal(url.searchParams.get('customer_id'), 'cust-1')
  assert.equal(url.searchParams.get('locale'), 'us')
  assert.equal(url.searchParams.get('content_filter'), 'high')
  assert.equal(new URL(searchAddress('k', 'q', 1, 'c', '')).searchParams.has('locale'), false)
})

test('the customer id is made once and kept', () => {
  const kept = new Map<string, string>()
  const store = { getItem: (k: string) => kept.get(k) ?? null, setItem: (k: string, v: string) => void kept.set(k, v) }
  const first = klipyCustomerId(store)
  assert.ok(first.length > 10)
  assert.equal(klipyCustomerId(store), first)
})

test('a search that Klipy refuses throws and is not tried again', async () => {
  let calls = 0
  const ask = (async () => {
    calls++
    return new Response('{}', { status: 429 })
  }) as typeof fetch
  await assert.rejects(searchKlipy('k', 'cat', 1, 'c', '', undefined, ask), /429/)
  assert.equal(calls, 1)
})
