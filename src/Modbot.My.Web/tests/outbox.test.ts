import assert from 'node:assert/strict'
import { beforeEach, test } from 'node:test'
import { ApiError } from '../src/lib/api.ts'
import { createOutbox } from '../src/lib/outbox.ts'
import { OUTBOX_KEY } from '../src/lib/storage.ts'
import { fakeLocalStorage, fakeTimers, settle } from './support.ts'

const A = 'https://a.example'
const B = 'https://b.example'

let store: Map<string, string>

beforeEach(() => {
  store = fakeLocalStorage()
  Object.defineProperty(globalThis, 'window', { configurable: true, value: new EventTarget() })
})

const stored = () => JSON.parse(store.get(OUTBOX_KEY) ?? '[]') as { url: string; tries: number }[]

/** A server whose answers a test scripts: each entry is what the next send gets. */
function server(...answers: (true | Error)[]) {
  const sent: string[] = []
  return {
    sent,
    send: async (url: string) => {
      sent.push(url)
      const answer = answers.length > 1 ? answers.shift() : answers[0]
      if (answer instanceof Error) throw answer
    },
  }
}

const unreachable = () => new TypeError('Failed to fetch')
const cloudDown = () => new ApiError(503, 'Modbot Cloud could not be reached.')

test('an address the server takes leaves nothing behind', async () => {
  const clock = fakeTimers()
  const cloud = server(true)
  const outbox = createOutbox(cloud.send, clock.timers)

  outbox.add(A)
  await settle()

  assert.deepEqual(cloud.sent, [A])
  assert.deepEqual(outbox.entries(), [])
  assert.deepEqual(stored(), [])
  assert.deepEqual(clock.pending(), [])
})

test('an address the server cannot take waits in localStorage and is sent again until it can', async () => {
  const clock = fakeTimers()
  const cloud = server(unreachable(), cloudDown(), true)
  const outbox = createOutbox(cloud.send, clock.timers)

  outbox.add(A)
  await settle()

  assert.deepEqual(cloud.sent, [A])
  assert.equal(outbox.entries()[0].tries, 1)
  assert.deepEqual(stored().map((e) => [e.url, e.tries]), [[A, 1]])
  assert.deepEqual(clock.pending(), [5_000])

  await clock.advance(5_000)
  assert.deepEqual(cloud.sent, [A, A])
  assert.equal(stored()[0].tries, 2)
  assert.deepEqual(clock.pending(), [10_000])

  await clock.advance(10_000)
  assert.deepEqual(cloud.sent, [A, A, A])
  assert.deepEqual(outbox.entries(), [])
  assert.deepEqual(stored(), [])
  assert.deepEqual(clock.pending(), [])
})

test('the first try shows no failure, so a send that just works is never marked as waiting', async () => {
  const clock = fakeTimers()
  let finish = () => {}
  const outbox = createOutbox(() => new Promise<void>((resolve) => (finish = resolve)), clock.timers)

  outbox.add(A)
  await settle()
  assert.deepEqual(outbox.entries().map((e) => e.tries), [0])

  finish()
  await settle()
  assert.deepEqual(outbox.entries(), [])
})

test('an answer that says this will never work drops the address instead of retrying', async () => {
  const clock = fakeTimers()
  const cloud = server(new ApiError(400, 'url must be an absolute https URL.'))
  const outbox = createOutbox(cloud.send, clock.timers)

  outbox.add(A)
  await settle()

  assert.deepEqual(outbox.entries(), [])
  assert.deepEqual(clock.pending(), [])
})

test('too many requests and a server error are both tried again', async () => {
  for (const error of [new ApiError(429, 'Too many requests from this address.'), new ApiError(502, 'Bad gateway')]) {
    const clock = fakeTimers()
    const outbox = createOutbox(server(error).send, clock.timers)

    outbox.add(A)
    await settle()

    assert.equal(outbox.entries().length, 1)
    assert.deepEqual(clock.pending(), [5_000])
  }
})

test('what an earlier page load left behind is sent on the next one', async () => {
  store.set(OUTBOX_KEY, JSON.stringify([{ url: A, addedAt: '2026-09-16T10:00:00Z', tries: 3 }]))
  const clock = fakeTimers()
  const cloud = server(true)
  const outbox = createOutbox(cloud.send, clock.timers)

  assert.deepEqual(outbox.entries().map((e) => [e.url, e.tries]), [[A, 3]])
  assert.deepEqual(cloud.sent, [])

  outbox.start()
  await settle()

  assert.deepEqual(cloud.sent, [A])
  assert.deepEqual(outbox.entries(), [])
})

test('coming back online sends at once, without waiting for the next try', async () => {
  const clock = fakeTimers()
  const cloud = server(unreachable(), unreachable(), unreachable(), true)
  const outbox = createOutbox(cloud.send, clock.timers)
  outbox.start()

  outbox.add(A)
  await clock.advance(5_000)
  await clock.advance(10_000)
  assert.deepEqual(clock.pending(), [20_000])

  window.dispatchEvent(new Event('online'))
  await settle()

  assert.equal(cloud.sent.length, 4)
  assert.deepEqual(outbox.entries(), [])
})

test('addresses go in the order they were queued, and one that fails holds the rest', async () => {
  const clock = fakeTimers()
  const cloud = server(unreachable(), unreachable(), true, true)
  const outbox = createOutbox(cloud.send, clock.timers)

  outbox.add(A)
  outbox.add(B)
  outbox.add(A)
  await settle()

  // A was sent, failed, and — because the queue changed meanwhile — sent once more at once. B
  // waited behind it both times.
  assert.deepEqual(cloud.sent, [A, A])
  assert.deepEqual(outbox.entries().map((e) => [e.url, e.tries]), [[A, 2], [B, 0]])
  assert.deepEqual(clock.pending(), [5_000])

  await clock.advance(5_000)
  assert.deepEqual(cloud.sent, [A, A, A, B])
  assert.deepEqual(outbox.entries(), [])
})

test('the list is the same array until something changes, and listeners hear each change', async () => {
  const clock = fakeTimers()
  const outbox = createOutbox(server(unreachable(), true).send, clock.timers)
  let heard = 0
  outbox.subscribe(() => heard++)

  const before = outbox.entries()
  assert.equal(outbox.entries(), before)

  outbox.add(A)
  await settle()
  assert.equal(heard, 2) // queued, then marked as tried once
  const waiting = outbox.entries()
  assert.notEqual(waiting, before)
  assert.equal(outbox.entries(), waiting)

  await clock.advance(5_000)
  assert.equal(heard, 3) // sent
})
