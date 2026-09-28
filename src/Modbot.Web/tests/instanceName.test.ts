import assert from 'node:assert/strict'
import { test } from 'node:test'
import { instanceCardText, instanceEnd, instanceName, instanceNumber } from '../src/lib/instanceName.ts'

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

test('an instance is closed only when a moderator closed it, and otherwise ended', () => {
  assert.equal(instanceEnd({ closedAt: null, closedByModerator: false }), 'open now')
  assert.equal(instanceEnd({ closedAt: '2026-09-25T03:22:00Z', closedByModerator: true }), 'closed')
  assert.equal(instanceEnd({ closedAt: '2026-09-25T03:22:00Z', closedByModerator: false }), 'ended')

  // An older server that does not say is read as the common case, not as a moderator's close.
  assert.equal(instanceEnd({ closedAt: '2026-09-25T03:22:00Z' }), 'ended')
})

const neonYard = {
  worldName: 'Neon Yard',
  number: '19453',
  people: 7,
  capacity: 60,
  groupAccessType: 'members',
  region: 'eu',
}

test('the tile and the phone header say the same head count and access type', () => {
  const text = instanceCardText(neonYard)
  assert.equal(text.count, '7/60')
  assert.equal(text.access, 'Group')
  assert.equal(text.name, 'Neon Yard')
})

test('the phone header always shows the number; the tile leaves it to the popup', () => {
  const text = instanceCardText(neonYard)
  assert.equal(text.tag, '#19453')
  assert.equal(text.named, null)
  assert.equal(text.tileLabel, 'Neon Yard, 7/60, Group, EU')
  assert.equal(text.headerLabel, 'Neon Yard #19453, 7/60, Group, EU')
})

test('an instance with a name of its own shows the name in quotes on both', () => {
  const text = instanceCardText({ ...neonYard, instanceName: '6 killed 7' })
  assert.equal(text.tag, '“6 killed 7”')
  assert.equal(text.named, '“6 killed 7”')
  assert.equal(text.tileLabel, 'Neon Yard “6 killed 7”, 7/60, Group, EU')
})

test('an unsure count carries its question mark, and missing facts are left out', () => {
  const text = instanceCardText({
    worldName: null,
    people: 80,
    peopleUnsure: true,
    capacity: null,
    groupAccessType: null,
    region: null,
  })
  assert.equal(text.count, '80?')
  assert.equal(text.unsure, true)
  assert.equal(text.tag, null)
  assert.equal(text.headerLabel, 'Unknown world, 80?')
})

test('with no head count at all the instance reads as empty, never as unsure', () => {
  const text = instanceCardText({ ...neonYard, people: null, peopleUnsure: true })
  assert.equal(text.here, 0)
  assert.equal(text.unsure, false)
  assert.equal(text.count, '0/60')
})
