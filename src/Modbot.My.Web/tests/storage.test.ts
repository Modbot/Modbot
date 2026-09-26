import assert from 'node:assert/strict'
import { beforeEach, test } from 'node:test'
import {
  OLD_SAVED_KEY,
  OLD_SEEN_KEY,
  OUTBOX_KEY,
  SAVED_KEY,
  SEEN_KEY,
  loadOutbox,
  loadSaved,
  loadSeenServers,
  saveOutbox,
  saveSeenServers,
  saveServer,
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

test('the seen list comes back as it was saved, and is empty until it has been', () => {
  assert.deepEqual(loadSeenServers(), [])

  const items = [
    { serverUrl: 'https://a.example', firstSeenAt: '2026-09-01T00:00:00Z', lastSeenAt: '2026-09-16T00:00:00Z', visits: 3 },
  ]
  saveSeenServers(items)

  assert.deepEqual(loadSeenServers(), items)
  assert.equal(typeof JSON.parse(store.get(SEEN_KEY)!).savedAt, 'string')
})

test('a seen list that is not one is read as empty, and an entry missing what matters is skipped', () => {
  store.set(SEEN_KEY, '[1,2,3]')
  assert.deepEqual(loadSeenServers(), [])

  store.set(
    SEEN_KEY,
    JSON.stringify({
      items: [{ serverUrl: 'https://a.example' }, { serverUrl: 'https://b.example', lastSeenAt: '2026-09-16T00:00:00Z' }, 4],
    }),
  )
  assert.deepEqual(loadSeenServers(), [
    { serverUrl: 'https://b.example', firstSeenAt: '2026-09-16T00:00:00Z', lastSeenAt: '2026-09-16T00:00:00Z', visits: 0 },
  ])
})

// Compatibility, added 2026-09-26, when "instance" became "server". The tests below go with the old
// keys and the old field name, and are removed with them.

test('a saved list under the old key is moved to the new one', () => {
  const old = [{ url: 'https://a.example', name: 'Main', addedAt: '2026-09-01T00:00:00Z' }]
  store.set(OLD_SAVED_KEY, JSON.stringify(old))

  assert.deepEqual(
    loadSaved().map((s) => [s.url, s.name]),
    [['https://a.example', 'Main']],
  )
  assert.equal(store.has(OLD_SAVED_KEY), false)
  assert.deepEqual(JSON.parse(store.get(SAVED_KEY)!), old)
})

test('saving a server keeps the list that was under the old key', () => {
  store.set(OLD_SAVED_KEY, JSON.stringify([{ url: 'https://a.example', name: null, addedAt: '2026-09-01T00:00:00Z' }]))

  saveServer('https://b.example')

  assert.deepEqual(
    loadSaved().map((s) => s.url),
    ['https://a.example', 'https://b.example'],
  )
  assert.equal(store.has(OLD_SAVED_KEY), false)
})

test('the old saved key never overwrites a list already under the new one', () => {
  store.set(SAVED_KEY, JSON.stringify([{ url: 'https://new.example', name: null, addedAt: '2026-09-20T00:00:00Z' }]))
  store.set(OLD_SAVED_KEY, JSON.stringify([{ url: 'https://old.example', name: null, addedAt: '2026-09-01T00:00:00Z' }]))

  assert.deepEqual(
    loadSaved().map((s) => s.url),
    ['https://new.example'],
  )
})

test('a seen list under the old key is moved to the new one, and its instanceUrl entries still read', () => {
  const old = {
    items: [
      { instanceUrl: 'https://a.example', firstSeenAt: '2026-09-01T00:00:00Z', lastSeenAt: '2026-09-16T00:00:00Z', visits: 3 },
    ],
    savedAt: '2026-09-16T00:00:00Z',
  }
  store.set(OLD_SEEN_KEY, JSON.stringify(old))

  assert.deepEqual(loadSeenServers(), [
    { serverUrl: 'https://a.example', firstSeenAt: '2026-09-01T00:00:00Z', lastSeenAt: '2026-09-16T00:00:00Z', visits: 3 },
  ])
  assert.equal(store.has(OLD_SEEN_KEY), false)
  assert.deepEqual(JSON.parse(store.get(SEEN_KEY)!), old)
})

test('a seen entry with both names reads serverUrl', () => {
  store.set(
    SEEN_KEY,
    JSON.stringify({
      items: [
        { serverUrl: 'https://new.example', instanceUrl: 'https://old.example', lastSeenAt: '2026-09-16T00:00:00Z' },
      ],
    }),
  )

  assert.deepEqual(
    loadSeenServers().map((s) => s.serverUrl),
    ['https://new.example'],
  )
})

test('a store that refuses every read and write is empty, and moving the old keys does not throw', () => {
  const refuse = () => {
    throw new Error('blocked')
  }
  Object.defineProperty(globalThis, 'localStorage', {
    configurable: true,
    value: { getItem: refuse, setItem: refuse, removeItem: refuse, clear: refuse },
  })

  assert.deepEqual(loadSaved(), [])
  assert.deepEqual(loadSeenServers(), [])
})
