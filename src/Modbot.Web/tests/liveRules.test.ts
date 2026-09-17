import assert from 'node:assert/strict'
import { test } from 'node:test'
import {
  auditMatches,
  changesBans,
  changesCalendar,
  changesCases,
  changesDiscordMembers,
  changesFlags,
  changesLive,
  changesMembers,
  changesReviews,
  concernsInstance,
  concernsPerson,
  concernsWorld,
} from '../src/lib/liveRules.ts'
import type { LiveEvent } from '../src/lib/liveStream.ts'

/**
 * Which live events each screen redraws for. These are the rules a page asks before it reads
 * again, so a wrong one either misses a change or reads on every fact.
 */

function fact(overrides: Partial<LiveEvent> = {}): LiveEvent {
  return {
    id: '81234',
    cursor: '81234',
    kind: 'fact',
    type: 'vrchat.group.member.ban',
    typeRaw: null,
    category: 'moderation',
    label: 'Banned',
    source: 'AuditLog',
    at: '2026-09-16T14:32:07+00:00',
    occurredBefore: null,
    observedAt: '2026-09-16T14:32:11+00:00',
    subject: { platform: 'VRChat', id: 'usr_c164', kind: 'Person' },
    actor: { platform: 'VRChat', id: 'usr_4f0b', name: 'Alice' },
    instanceId: null,
    worldId: null,
    worldName: null,
    person: null,
    flagged: false,
    reason: null,
    byThisDevice: false,
    data: { actorDisplayName: 'Alice', reason: 'spam' },
    ...overrides,
  }
}

test('each screen knows which fact types change it', () => {
  assert.ok(changesLive(fact({ type: 'vrchat.instance.join' })))
  assert.ok(changesLive(fact({ type: 'vrchat.group.instance.close' })))
  assert.ok(!changesLive(fact({ type: 'vrchat.group.member.ban' })))

  assert.ok(changesMembers(fact({ type: 'vrchat.group.member.join' })))
  assert.ok(changesMembers(fact({ type: 'vrchat.group.role.assign' })))
  assert.ok(changesMembers(fact({ type: 'vrchat.user.profile.changed' })), 'a trust rank arrives as a profile change')
  assert.ok(!changesMembers(fact({ type: 'discord.member.join' })))

  assert.ok(changesDiscordMembers(fact({ type: 'discord.member.join' })))
  assert.ok(changesDiscordMembers(fact({ type: 'discord.link.create' })))
  assert.ok(!changesDiscordMembers(fact({ type: 'vrchat.group.member.join' })))

  assert.ok(changesBans(fact({ type: 'vrchat.group.member.ban' })))
  assert.ok(changesBans(fact({ type: 'vrchat.group.member.unban' })))
  assert.ok(!changesBans(fact({ type: 'vrchat.group.member.join' })))

  assert.ok(changesCases(fact({ type: 'modbot.report.created' })))
  assert.ok(changesCases(fact({ type: 'modbot.evidence.attach' })))
  assert.ok(changesFlags(fact({ type: 'modbot.ai-moderation.flag' })))
  assert.ok(changesReviews(fact({ type: 'modbot.review.opened' })))
  assert.ok(changesCalendar(fact({ type: 'modbot.calendar.event.create' })))
  assert.ok(changesCalendar(fact({ type: 'vrchat.group.calendar-event.delete' })))
  assert.ok(!changesCalendar(fact({ type: 'modbot.review.opened' })))
})

test('a person is concerned as the subject or as the one who acted, on their own platform', () => {
  const e = fact()

  assert.ok(concernsPerson(e, 'usr_c164'))
  assert.ok(concernsPerson(e, 'usr_4f0b'))
  assert.ok(!concernsPerson(e, 'usr_other'))
  assert.ok(!concernsPerson(e, 'usr_c164', 'Discord'))
  assert.ok(concernsPerson(fact({ subject: { platform: 'Discord', id: '1234', kind: 'Person' } }), '1234', 'Discord'))
})

test('a world and an instance are concerned by where the fact happened', () => {
  const e = fact({ worldId: 'wrld_4b34', instanceId: '39911' })

  assert.ok(concernsWorld(e, 'wrld_4b34'))
  assert.ok(!concernsWorld(e, 'wrld_else'))
  assert.ok(concernsInstance(e, '39911'))
  assert.ok(!concernsInstance(e, '85019'))
  assert.ok(!concernsInstance(e, null), 'an instance whose number is not known yet is not matched by anything')
})

test('the audit log counts a fact only when its filters would show it', () => {
  const e = fact()

  assert.ok(auditMatches(e, {}))
  assert.ok(auditMatches(e, { source: ['AuditLog', 'Discord'] }))
  assert.ok(!auditMatches(e, { source: ['SyncDiff'] }))
  assert.ok(auditMatches(e, { type: ['vrchat.group.member.ban'] }))
  assert.ok(!auditMatches(e, { type: ['vrchat.group.member.join'] }))
  assert.ok(auditMatches(e, { category: 'Moderation' }))
  assert.ok(!auditMatches(e, { category: 'Operational' }))
})

test('the audit log matches who, where and when', () => {
  const e = fact({ worldId: 'wrld_4b34', instanceId: '39911' })

  assert.ok(auditMatches(e, { subject: 'usr_c164' }))
  assert.ok(!auditMatches(e, { subject: 'usr_4f0b' }), 'the actor is not the subject')
  assert.ok(!auditMatches(e, { subject: 'usr_c164', subjectPlatform: 'Discord' }))

  assert.ok(auditMatches(e, { actor: 'usr_4f0b' }))
  assert.ok(auditMatches(e, { actor: 'alice' }), 'the "done by" filter takes a name too')
  assert.ok(!auditMatches(e, { actor: 'bob' }))
  assert.ok(!auditMatches(fact({ actor: null }), { actor: 'usr_4f0b' }))

  assert.ok(auditMatches(e, { world: 'wrld_4b34' }))
  assert.ok(!auditMatches(e, { world: 'wrld_else' }))
  assert.ok(auditMatches(e, { instance: '39911' }))
  assert.ok(!auditMatches(e, { instance: '85019' }))

  assert.ok(auditMatches(e, { from: '2026-09-16T00:00:00+00:00', to: '2026-09-17T00:00:00+00:00' }))
  assert.ok(!auditMatches(e, { to: '2026-09-16T00:00:00+00:00' }), 'opened at an entry: nothing after it belongs above it')
  assert.ok(!auditMatches(e, { from: '2026-09-17T00:00:00+00:00' }))
})

test('the audit log matches precision, whether somebody is named, and the text', () => {
  const exact = fact()
  const window = fact({ occurredBefore: '2026-09-16T14:40:00+00:00', actor: null })

  assert.ok(auditMatches(exact, { precision: 'Exact' }))
  assert.ok(!auditMatches(exact, { precision: 'Window' }))
  assert.ok(auditMatches(window, { precision: 'Window' }))

  assert.ok(auditMatches(exact, { hasActor: true }))
  assert.ok(!auditMatches(exact, { hasActor: false }))
  assert.ok(auditMatches(window, { hasActor: false }))

  assert.ok(auditMatches(exact, { q: 'spam' }), 'the payload is searched')
  assert.ok(auditMatches(exact, { q: 'usr_c164' }), 'so is the subject id')
  assert.ok(auditMatches(exact, { q: 'ALICE' }), 'case does not matter')
  assert.ok(!auditMatches(exact, { q: 'nothing like this' }))
})
