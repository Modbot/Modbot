import assert from 'node:assert/strict'
import { test } from 'node:test'
import type { LogLine } from '../src/lib/api.ts'
import { wholeEntry } from '../src/lib/logEntry.ts'

function line(over: Partial<LogLine> = {}): LogLine {
  return {
    id: 41,
    at: '2026-09-18T09:30:00.000Z',
    level: 'Information',
    message: 'Synced 412 members',
    template: 'Synced {Count} members',
    source: 'Modbot.VRChat.Sync.MemberSync',
    area: 'Sync',
    exception: null,
    properties: '{"Count":412}',
    ...over,
  }
}

test('the record holds everything the line carries, in reading order', () => {
  const entry = wholeEntry(line())

  assert.deepEqual(Object.keys(entry), [
    'time',
    'level',
    'message',
    'source',
    'area',
    'properties',
  ])
  assert.equal(entry.time, '2026-09-18T09:30:00.000Z')
  assert.equal(entry.level, 'Information')
  assert.equal(entry.message, 'Synced 412 members')
  assert.equal(entry.source, 'Modbot.VRChat.Sync.MemberSync')
  assert.equal(entry.area, 'Sync')
  assert.deepEqual(entry.properties, { Count: 412 })
})

test('the properties are a record, not the string they were stored as', () => {
  const entry = wholeEntry(line({ properties: '{"Count":412,"Group":{"Id":"grp_1"}}' }))

  assert.deepEqual(entry.properties, { Count: 412, Group: { Id: 'grp_1' } })
  // And so the whole thing is still JSON when it reaches the clipboard.
  assert.deepEqual(JSON.parse(JSON.stringify(entry)), entry)
})

test('a line with nothing but a message and a level is three keys', () => {
  const entry = wholeEntry(
    line({ template: null, source: null, area: null, exception: null, properties: '{}' }),
  )

  assert.deepEqual(Object.keys(entry), ['time', 'level', 'message'])
})

test('an empty property document is left out however it arrived', () => {
  for (const properties of ['', '{}', '{ }', '\n{\n}\n', 'null']) {
    assert.equal(wholeEntry(line({ properties })).properties, undefined, properties)
  }
})

test('a property document that will not parse is kept as the text it came as', () => {
  const entry = wholeEntry(line({ properties: '{"Count":41' }))

  assert.equal(entry.properties, '{"Count":41')
})

test('the template is never in the record, however it differs from the message', () => {
  const same = wholeEntry(line({ message: 'Sync finished', template: 'Sync finished' }))
  assert.equal(same.template, undefined)

  const different = wholeEntry(line({ message: 'Sync finished', template: 'Sync {State}' }))
  assert.equal(different.template, undefined)
})

test('an exception is in the record, so the copied line carries it', () => {
  const entry = wholeEntry(
    line({ level: 'Error', exception: 'System.Exception: no\n   at Modbot.Sync.Run()' }),
  )

  assert.equal(Object.keys(entry).at(-1), 'exception')
  assert.equal(entry.exception, 'System.Exception: no\n   at Modbot.Sync.Run()')
})

test('the row id is not in the record', () => {
  assert.equal(wholeEntry(line()).id, undefined)
})
