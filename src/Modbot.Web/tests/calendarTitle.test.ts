import assert from 'node:assert/strict'
import { test } from 'node:test'
import { titleOf } from '../src/lib/calendar.ts'

test('an event with no title reads Untitled event', () => {
  assert.equal(titleOf(''), 'Untitled event')
  assert.equal(titleOf('   '), 'Untitled event')
  assert.equal(titleOf(null), 'Untitled event')
  assert.equal(titleOf(undefined), 'Untitled event')
})

test('a title is shown as typed, without the spaces around it', () => {
  assert.equal(titleOf('Game night'), 'Game night')
  assert.equal(titleOf('  Game night '), 'Game night')
})
