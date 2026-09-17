import assert from 'node:assert/strict'
import { beforeEach, test } from 'node:test'
import {
  OUTBOX_KEY,
  SERVER_LIST_KEY,
  loadOutbox,
  loadServerList,
  saveOutbox,
  saveServerList,
} from '../src/lib/storage.ts'
import { fakeLocalStorage } from './support.ts'

let store: Map<string, string>

beforeEach(() => {
  store = fakeLocalStorage()
})

test('the outbox comes back as it was saved', () => {
  saveOutbox([{ url: 'https://a.example', addedAt: '2026-09-16T10:00:00Z', tries: 2 }])

  assert.deepEqual(loadOutbox(), [{ url: 'https://a.example', addedAt: '2026-09-16T10:00:00Z', tries: 2 }])
})

test('an outbox somebody else wrote is read carefully', () => {
  store.set(
    OUTBOX_KEY,
    JSON.stringify([
      { url: 'https://a.example/settings', tries: '7' },
      { url: 'https://a.example', tries: 1 },
      { url: 'http://plain.example', tries: 1 },
      'https://b.example',
      null,
      { url: 'https://b.example', addedAt: '2026-09-16T10:00:00Z', tries: -3 },
    ]),
  )

  assert.deepEqual(
    loadOutbox().map((e) => [e.url, e.tries]),
    [
      ['https://a.example', 0],
      ['https://b.example', 0],
    ],
  )
})

test('a broken or missing outbox is empty', () => {
  assert.deepEqual(loadOutbox(), [])

  store.set(OUTBOX_KEY, '{not json')
  assert.deepEqual(loadOutbox(), [])

  store.set(OUTBOX_KEY, '{"url":"https://a.example"}')
  assert.deepEqual(loadOutbox(), [])
})

test('the outbox keeps the newest fifty', () => {
  saveOutbox(
    Array.from({ length: 60 }, (_, i) => ({ url: `https://m${i}.example`, addedAt: '2026-09-16T10:00:00Z', tries: 0 })),
  )

  const kept = loadOutbox()
  assert.equal(kept.length, 50)
  assert.equal(kept[0].url, 'https://m10.example')
  assert.equal(kept[49].url, 'https://m59.example')
})

test("the server's list comes back as it was saved, and is empty until it has been", () => {
  assert.deepEqual(loadServerList(), [])

  const items = [
    { instanceUrl: 'https://a.example', firstSeenAt: '2026-09-01T00:00:00Z', lastSeenAt: '2026-09-16T00:00:00Z', visits: 3 },
  ]
  saveServerList(items)

  assert.deepEqual(loadServerList(), items)
  assert.equal(typeof JSON.parse(store.get(SERVER_LIST_KEY)!).savedAt, 'string')
})

test("a server list that is not one is read as empty, and an entry missing what matters is skipped", () => {
  store.set(SERVER_LIST_KEY, '[1,2,3]')
  assert.deepEqual(loadServerList(), [])

  store.set(
    SERVER_LIST_KEY,
    JSON.stringify({
      items: [{ instanceUrl: 'https://a.example' }, { instanceUrl: 'https://b.example', lastSeenAt: '2026-09-16T00:00:00Z' }, 4],
    }),
  )
  assert.deepEqual(loadServerList(), [
    { instanceUrl: 'https://b.example', firstSeenAt: '2026-09-16T00:00:00Z', lastSeenAt: '2026-09-16T00:00:00Z', visits: 0 },
  ])
})
