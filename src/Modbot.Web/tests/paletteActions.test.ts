import test from 'node:test'
import assert from 'node:assert/strict'
import { mayDo, splitVerb, verbLabel } from '../src/lib/paletteActions.ts'
import type { CurrentUser } from '../src/lib/api.ts'

/** A signed-in person holding exactly these permissions. */
const who = (...permissionNames: string[]) => ({ permissionNames }) as unknown as CurrentUser

test('a verb and a name split apart', () => {
  assert.deepEqual(splitVerb('ban fenya'), { verb: 'ban', name: 'fenya' })
  assert.deepEqual(splitVerb('  Kick   Fenya Kraut '), { verb: 'kick', name: 'Fenya Kraut' })
  assert.deepEqual(splitVerb('unban usr_da80ded9'), { verb: 'unban', name: 'usr_da80ded9' })
  assert.deepEqual(splitVerb('note fenya'), { verb: 'note', name: 'fenya' })
})

test('a verb alone is not an action, so "ban" still finds the Bans page', () => {
  assert.equal(splitVerb('ban'), null)
  assert.equal(splitVerb('ban '), null)
})

test('a name shorter than the server searches on is not an action', () => {
  assert.equal(splitVerb('ban f'), null)
})

test('a word that is not a verb is not an action', () => {
  assert.equal(splitVerb('banned fenya'), null)
  assert.equal(splitVerb('fenya'), null)
  assert.equal(splitVerb('warn fenya'), null)
})

test('the palette offers what the popup offers, no more', () => {
  const moderator = who('Kick', 'Ban', 'Unban')
  const member = { userId: 'usr_1', isMember: true, banned: false }
  const banned = { userId: 'usr_1', isMember: false, banned: true }
  const left = { userId: 'usr_1', isMember: false, banned: false }

  assert.equal(mayDo(moderator, 'ban', member), true)
  assert.equal(mayDo(moderator, 'kick', member), true)
  assert.equal(mayDo(moderator, 'unban', member), false)

  // Already banned: unban, not a second ban.
  assert.equal(mayDo(moderator, 'ban', banned), false)
  assert.equal(mayDo(moderator, 'unban', banned), true)

  // Nothing to kick them from.
  assert.equal(mayDo(moderator, 'kick', left), false)
  assert.equal(mayDo(moderator, 'ban', left), true)
})

test('an action is only offered to somebody holding its permission', () => {
  const member = { userId: 'usr_1', isMember: true }
  assert.equal(mayDo(who('Kick'), 'ban', member), false)
  assert.equal(mayDo(who('Ban'), 'kick', member), false)
  assert.equal(mayDo(who(), 'note', member), false)
  assert.equal(mayDo(who('WriteNotes'), 'note', member), false)
  assert.equal(mayDo(who('WriteNotes', 'ViewAuditLog'), 'note', member), true)
})

test('nobody to act on means nothing is offered', () => {
  assert.equal(mayDo(who('Ban', 'WriteNotes', 'ViewAuditLog'), 'ban', { userId: null }), false)
  assert.equal(mayDo(who('Ban', 'WriteNotes', 'ViewAuditLog'), 'note', { userId: '' }), false)
})

test('each verb names the person', () => {
  assert.equal(verbLabel('ban', 'Fenya'), 'Ban Fenya…')
  assert.equal(verbLabel('kick', 'Fenya'), 'Kick Fenya…')
  assert.equal(verbLabel('unban', 'Fenya'), 'Unban Fenya…')
  assert.equal(verbLabel('note', 'Fenya'), 'Add a note to Fenya…')
})
