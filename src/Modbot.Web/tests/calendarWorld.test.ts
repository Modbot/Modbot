import assert from 'node:assert/strict'
import { test } from 'node:test'
import { FROM_LIST, listMissing, NO_WORLD, OTHER_WORLD, worldChoice } from '../src/lib/calendarWorld.ts'

test('an event with a world and no list shows it under World id', () => {
  assert.equal(worldChoice({ fromList: false, typedWorld: false, worldId: 'wrld_prop' }), OTHER_WORLD)
})

test('an event with no world shows None, until World id is chosen', () => {
  assert.equal(worldChoice({ fromList: false, typedWorld: false, worldId: null }), NO_WORLD)
  assert.equal(worldChoice({ fromList: false, typedWorld: true, worldId: null }), OTHER_WORLD)
})

test('picking from a list wins over a world id', () => {
  assert.equal(worldChoice({ fromList: true, typedWorld: false, worldId: null }), FROM_LIST)
  assert.equal(worldChoice({ fromList: true, typedWorld: true, worldId: 'wrld_prop' }), FROM_LIST)
})

test('Pick from a list with nothing picked is missing its list', () => {
  assert.equal(listMissing({ choice: FROM_LIST, worldListId: null, worldId: null }), true)
})

test('a whole list picked is not missing', () => {
  assert.equal(listMissing({ choice: FROM_LIST, worldListId: 'list-1', worldId: null }), false)
})

test('one world picked from a list is not missing', () => {
  assert.equal(listMissing({ choice: FROM_LIST, worldListId: null, worldId: 'wrld_prop' }), false)
})

test('other choices never miss a list', () => {
  assert.equal(listMissing({ choice: NO_WORLD, worldListId: null, worldId: null }), false)
  assert.equal(listMissing({ choice: OTHER_WORLD, worldListId: null, worldId: null }), false)
})
