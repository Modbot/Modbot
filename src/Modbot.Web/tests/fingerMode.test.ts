import assert from 'node:assert/strict'
import { beforeEach, test } from 'node:test'
import { firstFingerMode, recallFingerMode, rememberFingerMode } from '../src/lib/fingerMode.ts'

// A browser's store, kept in a Map, put in place of whatever Node has before each test.
let saved: Map<string, string>
let blocked: boolean

function useStore() {
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
}

beforeEach(() => {
  saved = new Map()
  blocked = false
  useStore()
})

test('a finger paints until the other is picked', () => {
  assert.equal(recallFingerMode(), null)
  assert.equal(firstFingerMode(), 'paint')
})

test('a picked mode is what the grid opens on next', () => {
  rememberFingerMode('scroll')
  assert.equal(recallFingerMode(), 'scroll')
  assert.equal(firstFingerMode(), 'scroll')

  rememberFingerMode('paint')
  assert.equal(firstFingerMode(), 'paint')
})

test('something else in the store is not a mode', () => {
  saved.set('modbot.availability.finger', 'drag')
  assert.equal(recallFingerMode(), null)
  assert.equal(firstFingerMode(), 'paint')
})

test('a blocked store forgets, and the grid still opens on Paint', () => {
  blocked = true
  assert.doesNotThrow(() => rememberFingerMode('scroll'))
  assert.equal(recallFingerMode(), null)
  assert.equal(firstFingerMode(), 'paint')
})
