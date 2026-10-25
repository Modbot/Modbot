import test from 'node:test'
import assert from 'node:assert/strict'
import { conditionChipText, conditionName, conditionNumbers, givenLabel } from '../src/lib/automaticRoleStanding.ts'
import type { RuleCondition } from '../src/lib/automaticRoles.ts'

const condition = (fields: Partial<RuleCondition>): RuleCondition => ({
  label: 'Overturned bans',
  met: true,
  reason: null,
  count: null,
  max: null,
  days: null,
  ...fields,
})

test('a counted condition shows its count, the most allowed and the days', () => {
  assert.equal(conditionNumbers(condition({ count: 1, max: 0, days: 30 })), '1 of at most 0, 30 days')
  assert.equal(conditionNumbers(condition({ count: 2, max: 3, days: 1 })), '2 of at most 3, 1 day')
})

test('the daily ban cap has no days', () => {
  assert.equal(conditionNumbers(condition({ label: 'Daily ban cap', count: 4, max: 5 })), '4 of at most 5')
})

test('a yes-or-no condition and a missing number show no numbers', () => {
  assert.equal(conditionNumbers(condition({ label: 'Clips ready' })), null)
  assert.equal(conditionNumbers(condition({ met: false, max: 3, days: 7 })), null)
})

test('missed clips go by the name the settings page uses', () => {
  assert.equal(conditionName(condition({ label: 'Missed clips' })), 'Missing clips')
  assert.equal(conditionName(condition({ label: 'Clips ready' })), 'Clips ready')
})

test('a holders chip is the name, with count and most allowed for a counted condition', () => {
  assert.equal(conditionChipText(condition({ count: 1, max: 2, days: 30 })), 'Overturned bans 1/2')
  assert.equal(conditionChipText(condition({ label: 'Missed clips', count: 0, max: 3, days: 7 })), 'Missing clips 0/3')
  assert.equal(conditionChipText(condition({ label: 'Clips ready' })), 'Clips ready')
})

test('who gave a role is spelled out', () => {
  assert.equal(givenLabel('ByModbot'), 'By Modbot')
  assert.equal(givenLabel('ByHand'), 'By hand')
  assert.equal(givenLabel('NotHeld'), 'Not held')
})
