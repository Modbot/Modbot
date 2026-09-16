import test from 'node:test'
import assert from 'node:assert/strict'
import { actionsFor, confirmTitle, reasonRequired, resultText } from '../src/lib/moderationActions.ts'
import type { CurrentUser } from '../src/lib/api.ts'

/** A signed-in person holding exactly these permissions. */
const who = (...permissionNames: string[]) => ({ permissionNames }) as unknown as CurrentUser

const moderator = who('Kick', 'Ban', 'Unban')

test('nobody to act on means nothing is offered', () => {
  assert.deepEqual(actionsFor(moderator, { userId: null }), [])
  assert.deepEqual(actionsFor(moderator, { userId: '' }), [])
})

test('a member is offered kick and ban', () => {
  const offered = actionsFor(moderator, { userId: 'usr_1', isMember: true }).map((o) => o.action)
  assert.deepEqual(offered, ['kick', 'ban'])
})

test('someone already banned is offered unban instead of ban', () => {
  const offered = actionsFor(moderator, { userId: 'usr_1', banned: true }).map((o) => o.action)
  assert.deepEqual(offered, ['unban'])
})

test('somebody who is not a member is not offered a kick', () => {
  const offered = actionsFor(moderator, { userId: 'usr_1', isMember: false }).map((o) => o.action)
  assert.deepEqual(offered, ['ban'])
})

test('membership Modbot has not read still offers a kick', () => {
  const offered = actionsFor(moderator, { userId: 'usr_1' }).map((o) => o.action)
  assert.deepEqual(offered, ['kick', 'ban'])
})

test('each action needs its own permission', () => {
  assert.deepEqual(actionsFor(who('Kick'), { userId: 'usr_1' }).map((o) => o.action), ['kick'])
  assert.deepEqual(actionsFor(who('Ban'), { userId: 'usr_1' }).map((o) => o.action), ['ban'])
  assert.deepEqual(actionsFor(who('Ban'), { userId: 'usr_1', banned: true }).map((o) => o.action), [])
  assert.deepEqual(actionsFor(who('Unban'), { userId: 'usr_1', banned: true }).map((o) => o.action), ['unban'])
  assert.deepEqual(actionsFor(who('ViewMembers'), { userId: 'usr_1' }), [])
})

test('an administrator is offered everything', () => {
  const offered = actionsFor(who('Administrator'), { userId: 'usr_1' }).map((o) => o.action)
  assert.deepEqual(offered, ['kick', 'ban'])
})

test('a ban always needs a reason; the others only when the group asks', () => {
  assert.equal(reasonRequired('ban', false), true)
  assert.equal(reasonRequired('kick', false), false)
  assert.equal(reasonRequired('unban', false), false)
  assert.equal(reasonRequired('kick', true), true)
  assert.equal(reasonRequired('unban', true), true)
})

test('the confirmation names the person and the action', () => {
  assert.equal(confirmTitle('ban', 'Gunner24'), 'Ban Gunner24 from the group?')
  assert.equal(confirmTitle('kick', 'Gunner24'), 'Kick Gunner24 from the group?')
  assert.equal(confirmTitle('unban', 'Gunner24'), 'Unban Gunner24?')
})

test('a refusal reads as what VRChat said, never as a success', () => {
  const refused = resultText('ban', {
    done: false,
    error: 'You do not have permission to ban members of this group.',
    rateLimited: false,
    repeat: false,
  })

  assert.equal(refused, 'You do not have permission to ban members of this group.')
})

test('a rate limit says nothing happened', () => {
  const stopped = resultText('ban', { done: false, error: null, rateLimited: true, repeat: false })

  assert.match(stopped, /Nothing happened/)
})

test('a refusal with no message still says something', () => {
  const quiet = resultText('kick', { done: false, error: null, rateLimited: false, repeat: false })

  assert.equal(quiet, 'VRChat refused it and did not say why.')
})

test('a repeat of one confirmation reads as already sent, not as a second action', () => {
  const again = resultText('ban', { done: true, error: null, rateLimited: false, repeat: true })

  assert.equal(again, 'Banned. Already sent.')
  assert.equal(resultText('ban', { done: true, error: null, rateLimited: false, repeat: false }), 'Banned.')
})
