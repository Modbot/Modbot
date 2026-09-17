import assert from 'node:assert/strict'
import { test } from 'node:test'
import { keepTrying, retryDelay } from '../src/lib/retry.ts'
import { fakeTimers, settle } from './support.ts'

test('the wait doubles from five seconds and stops at five minutes', () => {
  assert.deepEqual([1, 2, 3, 4, 5, 6, 7, 8, 20].map(retryDelay), [
    5_000, 10_000, 20_000, 40_000, 80_000, 160_000, 300_000, 300_000, 300_000,
  ])
})

test('a try that fails is tried again after each wait until it succeeds', async () => {
  const clock = fakeTimers()
  const outcomes = [false, false, false, true]
  let tries = 0

  keepTrying(async () => outcomes[tries++], clock.timers).now()
  await settle()

  assert.equal(tries, 1)
  assert.deepEqual(clock.pending(), [5_000])

  await clock.advance(5_000)
  assert.equal(tries, 2)
  assert.deepEqual(clock.pending(), [10_000])

  await clock.advance(10_000)
  assert.equal(tries, 3)
  assert.deepEqual(clock.pending(), [20_000])

  await clock.advance(20_000)
  assert.equal(tries, 4)
  assert.deepEqual(clock.pending(), [])

  await clock.advance(600_000)
  assert.equal(tries, 4)
})

test('a throw counts as a failure', async () => {
  const clock = fakeTimers()
  let tries = 0

  keepTrying(async () => {
    tries++
    throw new Error('no network')
  }, clock.timers).now()
  await settle()

  assert.equal(tries, 1)
  assert.deepEqual(clock.pending(), [5_000])
})

test('a fresh reason to try starts the backoff over', async () => {
  const clock = fakeTimers()
  let tries = 0
  const retrying = keepTrying(async () => (tries++, false), clock.timers)

  retrying.now()
  await clock.advance(5_000)
  await clock.advance(10_000)
  assert.equal(tries, 3)
  assert.deepEqual(clock.pending(), [20_000])

  retrying.now()
  await settle()
  assert.equal(tries, 4)
  assert.deepEqual(clock.pending(), [5_000])
})

test('a reason that arrives during a try is answered by one more try, not two', async () => {
  const clock = fakeTimers()
  let tries = 0
  let finish: (done: boolean) => void = () => {}
  const retrying = keepTrying(
    () =>
      new Promise<boolean>((resolve) => {
        tries++
        finish = resolve
      }),
    clock.timers,
  )

  retrying.now()
  await settle()
  retrying.now()
  retrying.now()
  await settle()
  assert.equal(tries, 1)

  finish(true)
  await settle()
  assert.equal(tries, 2)

  finish(true)
  await settle()
  assert.equal(tries, 2)
  assert.deepEqual(clock.pending(), [])
})

test('after stop, nothing more is tried', async () => {
  const clock = fakeTimers()
  let tries = 0
  const retrying = keepTrying(async () => (tries++, false), clock.timers)

  retrying.now()
  await settle()
  retrying.stop()

  assert.deepEqual(clock.pending(), [])
  await clock.advance(600_000)
  retrying.now()
  await settle()
  assert.equal(tries, 1)
})
