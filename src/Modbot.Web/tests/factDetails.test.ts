import assert from 'node:assert/strict'
import { test } from 'node:test'
import { avatarWorn, timeInInstance } from '../src/lib/factDetails.ts'

test('an avatar change names the avatar it was recorded with', () => {
  assert.equal(avatarWorn({ displayName: 'Ada', avatarName: 'Nardoragon' }), 'Nardoragon')
})

test('an avatar change with no name recorded names nothing rather than inventing one', () => {
  assert.equal(avatarWorn({ displayName: 'Ada' }), null)
  assert.equal(avatarWorn({ avatarName: '' }), null)
  assert.equal(avatarWorn(null), null)
})

test('an avatar id, if VRChat ever sent one, is not mistaken for a name', () => {
  // The log carries no avtr_ id, so a payload holding one and no name still says nothing: the
  // entry reads "changed avatar" rather than "switched to the avatar avtr_...".
  assert.equal(avatarWorn({ avatarId: 'avtr_8b1f2c7a-0000-4000-8000-000000000000' }), null)
})

test('a kick with a watched arrival says exactly how long they had been there', () => {
  assert.equal(
    timeInInstance({ inInstanceSeconds: 720, seenArriving: true }),
    'after 12 min in the instance',
  )
})

test('a kick with no watched arrival says at least how long', () => {
  assert.equal(
    timeInInstance({ inInstanceSeconds: 720, seenArriving: false }),
    'after at least 12 min in the instance',
  )
})

test('a kick with no presence data says nothing at all, not zero', () => {
  assert.equal(timeInInstance({}), null)
  assert.equal(timeInInstance(null), null)
  assert.equal(timeInInstance({ seenArriving: true }), null)
})

test('a nonsense duration is not shown', () => {
  assert.equal(timeInInstance({ inInstanceSeconds: -5, seenArriving: true }), null)
  assert.equal(timeInInstance({ inInstanceSeconds: 'twelve', seenArriving: true }), null)
})

test('somebody seen arriving and kicked in the same second is a real measurement of nothing', () => {
  assert.equal(timeInInstance({ inInstanceSeconds: 0, seenArriving: true }), 'after 0 seconds in the instance')
})

test('the steps read the way a person says them', () => {
  assert.equal(timeInInstance({ inInstanceSeconds: 45, seenArriving: true }), 'after 45 seconds in the instance')
  assert.equal(timeInInstance({ inInstanceSeconds: 5400, seenArriving: true }), 'after 1 h 30 min in the instance')
})
