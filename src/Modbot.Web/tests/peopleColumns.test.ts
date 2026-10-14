import assert from 'node:assert/strict'
import { beforeEach, test } from 'node:test'
import { loadHiddenColumns, peopleColumns, saveHiddenColumns, withColumn } from '../src/lib/peopleColumns.ts'

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

test('nothing stored shows every column', () => {
  assert.deepEqual(loadHiddenColumns('people'), [])
})

test('a saved choice comes back', () => {
  saveHiddenColumns('people', ['firstSeen'])
  assert.deepEqual(loadHiddenColumns('people'), ['firstSeen'])
})

test('each view keeps its own choice', () => {
  saveHiddenColumns('people', ['standing'])
  saveHiddenColumns('instance', ['visits'])
  assert.deepEqual(loadHiddenColumns('people'), ['standing'])
  assert.deepEqual(loadHiddenColumns('instance'), ['visits'])
  assert.deepEqual(loadHiddenColumns('members'), [])
})

test('a column added later shows, since only the hidden ones are stored', () => {
  saveHiddenColumns('instance', ['visits'])
  const shown = peopleColumns('instance').filter((c) => !loadHiddenColumns('instance').includes(c.id))
  assert.deepEqual(shown.map((c) => c.id), ['standing', 'arrived', 'leftAt'])
})

test('a garbled or blocked store gives the defaults', () => {
  saved.set('modbot.people.columns.people', '{not json')
  assert.deepEqual(loadHiddenColumns('people'), [])
  saved.set('modbot.people.columns.people', '"standing"')
  assert.deepEqual(loadHiddenColumns('people'), [])
  saved.set('modbot.people.columns.people', '["standing", 4]')
  assert.deepEqual(loadHiddenColumns('people'), ['standing'])

  blocked = true
  assert.deepEqual(loadHiddenColumns('people'), [])
  assert.doesNotThrow(() => saveHiddenColumns('people', ['standing']))
})

test('turning a column off and on again', () => {
  assert.deepEqual(withColumn([], 'joined', false), ['joined'])
  assert.deepEqual(withColumn(['joined'], 'joined', false), ['joined'])
  assert.deepEqual(withColumn(['joined', 'roles'], 'joined', true), ['roles'])
})

test('Reset is saving nothing hidden', () => {
  saveHiddenColumns('members', ['roles'])
  saveHiddenColumns('members', [])
  assert.deepEqual(loadHiddenColumns('members'), [])
})

test('the member list has the columns its view draws', () => {
  const ids = (view: 'member' | 'left', profiles: boolean) => peopleColumns('members', { view, profiles }).map((c) => c.id)
  assert.deepEqual(ids('member', true), ['discord', 'roles', 'joined', 'lastSeen', 'firstSeen'])
  assert.deepEqual(ids('left', false), ['roles', 'joined', 'left', 'lastSeen'])
})
