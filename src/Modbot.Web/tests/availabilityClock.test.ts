import assert from 'node:assert/strict'
import { beforeEach, test } from 'node:test'
import { firstClock, recallClock, rememberClock } from '../src/lib/availabilityClock.ts'
import { browserClock } from '../src/lib/availabilityZones.ts'

// A browser's store, kept in a Map, put in place of whatever Node has before each test.
let saved: Map<string, string>
let blocked: boolean

beforeEach(() => {
  saved = new Map()
  blocked = false
  Object.defineProperty(globalThis, 'localStorage', {
    configurable: true,
    writable: true,
    value: {
      getItem: (key: string) => {
        if (blocked) throw new Error('blocked')
        return saved.get(key) ?? null
      },
      setItem: (key: string, value: string) => {
        if (blocked) throw new Error('blocked')
        saved.set(key, value)
      },
    },
  })
})

test('until a clock is picked the grid opens on the browser\'s own', () => {
  assert.equal(recallClock(), null)
  assert.equal(firstClock(), browserClock())
})

test('a picked clock is what the grid opens on next, whatever the browser uses', () => {
  rememberClock('12h')
  assert.equal(recallClock(), '12h')
  assert.equal(firstClock(), '12h')

  rememberClock('24h')
  assert.equal(firstClock(), '24h')
})

test('something else in the store is not a clock', () => {
  saved.set('modbot.availability.clock', 'noon')
  assert.equal(recallClock(), null)
})

test('a blocked store forgets, and the grid still opens', () => {
  blocked = true
  rememberClock('12h')
  assert.equal(recallClock(), null)
  assert.equal(firstClock(), browserClock())
})
