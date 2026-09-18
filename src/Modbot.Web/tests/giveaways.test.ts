import assert from 'node:assert/strict'
import { test } from 'node:test'
import type { CurrentUser } from '../src/lib/api.ts'
import { mayOpen } from '../src/lib/nav.ts'
import { fromPolledData, measured, takesAmount, takesWindow } from '../src/lib/giveawayRules.ts'

function person(...permissionNames: string[]): CurrentUser {
  return { permissionNames } as CurrentUser
}

test('giveaways needs its own permission and running one is not enough to see the page', () => {
  assert.equal(mayOpen(person(), 'giveaways'), false)
  assert.equal(mayOpen(person('RunGiveaways'), 'giveaways'), false)
  assert.equal(mayOpen(person('ViewGiveaways'), 'giveaways'), true)
  assert.equal(mayOpen(person('Administrator'), 'giveaways'), true)
})

test('a figure from polled presence is shown as "about" and never to a decimal place', () => {
  assert.equal(measured(12.4, true), 'about 12')
  assert.equal(measured(12.44, false), '12.4')
})

test('the rules that read presence are the ones marked as polled', () => {
  assert.equal(fromPolledData('instanceHours'), true)
  assert.equal(fromPolledData('oneInstanceHours'), true)
  assert.equal(fromPolledData('seenWithinDays'), true)

  // Voice and messages are summed from daily totals, which are exact counts of what was recorded.
  assert.equal(fromPolledData('voiceHours'), false)
  assert.equal(fromPolledData('messages'), false)
})

test('a rule that takes no number offers no number box', () => {
  assert.equal(takesAmount('inGroup'), false)
  assert.equal(takesAmount('linkedAccounts'), false)
  assert.equal(takesAmount('instanceHours'), true)
})

test('only the counted-over-time rules offer a window', () => {
  assert.equal(takesWindow('instanceHours'), true)
  assert.equal(takesWindow('noTrouble'), true)
  assert.equal(takesWindow('groupMemberDays'), false)
})
