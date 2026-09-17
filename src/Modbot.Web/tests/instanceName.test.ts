import assert from 'node:assert/strict'
import { test } from 'node:test'
import { instanceName, instanceNumber } from '../src/lib/instanceName.ts'

test('an instance is named the way VRChat shows it: the world, then the number', () => {
  assert.equal(instanceName('The Black Cat', 'wrld_4b34', '19453'), 'The Black Cat #19453')
})

test('a world Modbot has not read yet is named by its id, not by a made-up word', () => {
  assert.equal(instanceName(null, 'wrld_4b34', '19453'), 'wrld_4b34 #19453')
  assert.equal(instanceName('', 'wrld_4b34', '19453'), 'wrld_4b34 #19453')
})

test('with no number the world stands alone, and with nothing at all it is just an instance', () => {
  assert.equal(instanceName('The Black Cat', 'wrld_4b34', null), 'The Black Cat')
  assert.equal(instanceName(null, null, '19453'), 'instance #19453')
  assert.equal(instanceName(null, null, null), 'an instance')
})

test('the number on its own is written with a hash, where the world is already named beside it', () => {
  assert.equal(instanceNumber('19453'), '#19453')
  assert.equal(instanceNumber(null), 'this instance')
})
