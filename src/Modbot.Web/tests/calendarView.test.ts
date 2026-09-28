import assert from 'node:assert/strict'
import { beforeEach, test } from 'node:test'
import { firstView, recallView, rememberView } from '../src/lib/calendarView.ts'

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

test('a phone opens on Schedule and a desk on Week until a view is picked', () => {
  assert.equal(firstView(true), 'schedule')
  assert.equal(firstView(false), 'week')
})

test('a picked view is what the page opens on next, on a phone and at a desk', () => {
  rememberView('month')
  assert.equal(recallView(), 'month')
  assert.equal(firstView(true), 'month')
  assert.equal(firstView(false), 'month')

  rememberView('day')
  assert.equal(firstView(true), 'day')
})

test('a stored value that is not a view is ignored', () => {
  saved.set('modbot.calendar.view', 'agenda')
  assert.equal(recallView(), null)
  assert.equal(firstView(true), 'schedule')
})

test('a blocked store forgets without throwing', () => {
  blocked = true
  assert.doesNotThrow(() => rememberView('month'))
  assert.equal(recallView(), null)
  assert.equal(firstView(false), 'week')
})
