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

test('an instance opened with a name is called by it, in quotes, in place of the number', () => {
  assert.equal(instanceName('Murder 4', 'wrld_4b34', '16354', '6 killed 7'), 'Murder 4 “6 killed 7”')
  assert.equal(instanceName(null, 'wrld_4b34', '16354', '6 killed 7'), 'wrld_4b34 “6 killed 7”')
  assert.equal(instanceName(null, null, '16354', '6 killed 7'), 'instance “6 killed 7”')
  assert.equal(instanceNumber('16354', '6 killed 7'), '“6 killed 7”')
})

test('an instance with no name, or a blank one, keeps its number', () => {
  assert.equal(instanceName('Murder 4', 'wrld_4b34', '16354', null), 'Murder 4 #16354')
  assert.equal(instanceName('Murder 4', 'wrld_4b34', '16354', ''), 'Murder 4 #16354')
  assert.equal(instanceName('Murder 4', 'wrld_4b34', '16354', '   '), 'Murder 4 #16354')
  assert.equal(instanceNumber('16354', '  '), '#16354')
})

test('a name is shown without the spaces around it', () => {
  assert.equal(instanceName('Murder 4', 'wrld_4b34', '16354', '  6 killed 7 '), 'Murder 4 “6 killed 7”')
})
