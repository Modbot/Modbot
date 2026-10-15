import assert from 'node:assert/strict'
import { test } from 'node:test'
import { FROM_LIST, NO_WORLD, OTHER_WORLD, worldChoice } from '../src/lib/calendarWorld.ts'

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

test('a whole list or one world picked from a list both keep the menu on Pick from a list', () => {
  assert.equal(worldChoice({ fromList: true, typedWorld: false, worldId: null }), FROM_LIST)
  assert.equal(worldChoice({ fromList: true, typedWorld: false, worldId: 'wrld_prop' }), FROM_LIST)
})
