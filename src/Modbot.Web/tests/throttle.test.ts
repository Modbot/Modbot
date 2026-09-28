import assert from 'node:assert/strict'
import { test } from 'node:test'
import { throttle, type ThrottleTimers } from '../src/lib/throttle.ts'

/** A clock and timers the test moves by hand. */
function fakeTimers() {
  let now = 0
  let next = 0
  const timers = new Map<number, { at: number; run: () => void }>()

  const api: ThrottleTimers = {
    now: () => now,
    setTimer: (run, ms) => {
      const id = ++next
      timers.set(id, { at: now + ms, run })
      return id
    },
    clearTimer: (handle) => {
      timers.delete(handle as number)
    },
  }

  const advance = (ms: number) => {
    now += ms
    for (const [id, timer] of [...timers].sort((a, b) => a[1].at - b[1].at)) {
      if (timer.at > now) continue
      timers.delete(id)
      timer.run()
    }
  }

  return { api, advance, waiting: () => timers.size }
}

test('the first change redraws at once', () => {
  const t = fakeTimers()
  let runs = 0
  const redraw = throttle(() => runs++, 2000, t.api)

  redraw()

  assert.equal(runs, 1)
  assert.equal(t.waiting(), 0)
})

test('a burst inside the gap is one more redraw, when the gap ends', () => {
  const t = fakeTimers()
  let runs = 0
  const redraw = throttle(() => runs++, 2000, t.api)

  redraw()
  t.advance(100)
  redraw()
  redraw()
  t.advance(500)
  redraw()

  assert.equal(runs, 1)
  t.advance(1399)
  assert.equal(runs, 1)
  t.advance(1)
  assert.equal(runs, 2)
})

test('changes that never stop still redraw every gap', () => {
  const t = fakeTimers()
  let runs = 0
  const redraw = throttle(() => runs++, 2000, t.api)

  // A change every 100 ms for ten seconds: a settle would wait the whole time.
  for (let ms = 0; ms < 10_000; ms += 100) {
    redraw()
    t.advance(100)
  }

  // At 0, then 2, 4, 6, 8 and 10 seconds.
  assert.equal(runs, 6)
})

test('after a quiet gap the next change redraws at once again', () => {
  const t = fakeTimers()
  let runs = 0
  const redraw = throttle(() => runs++, 2000, t.api)

  redraw()
  t.advance(5000)
  redraw()

  assert.equal(runs, 2)
})

test('cancel drops a held redraw', () => {
  const t = fakeTimers()
  let runs = 0
  const redraw = throttle(() => runs++, 2000, t.api)

  redraw()
  redraw()
  redraw.cancel()
  t.advance(5000)

  assert.equal(runs, 1)
  assert.equal(t.waiting(), 0)
})
