import assert from 'node:assert/strict'
import { test } from 'node:test'
import type { AuditEntry, PersonView } from '../src/lib/api.ts'
import { auditQueryFrom, PERSON_ACTIVITY_START, personChip } from '../src/lib/pageFilters.ts'
import { askedOf, concernsAny, foundUnder } from '../src/lib/personTimeline.ts'
import { hasStory, joinStory, times } from '../src/lib/joinStory.ts'

const person: Pick<PersonView, 'vrChat' | 'discord' | 'account'> = {
  vrChat: { id: 'usr_ada', name: 'Ada', foundBy: 'asked' },
  discord: { id: '1234', name: 'ada', foundBy: 'link' },
  account: null,
}

function entry(over: Partial<AuditEntry>): AuditEntry {
  return {
    id: 1,
    occurredAt: '2026-09-01T10:00:00Z',
    occurredBefore: null,
    observedAt: '2026-09-01T10:00:00Z',
    precision: 'Exact',
    type: 'vrchat.group.member.ban',
    typeRaw: null,
    category: 'Moderation',
    source: 'AuditLog',
    subjectPlatform: 'VRChat',
    subjectId: 'usr_ada',
    subjectKind: 'Person',
    subjectName: null,
    actorPlatform: null,
    actorId: null,
    actorName: null,
    subjectTrustRank: null,
    actorTrustRank: null,
    worldId: null,
    worldName: null,
    instanceId: null,
    modbotInstanceId: null,
    instanceName: null,
    description: null,
    data: null,
    ...over,
  } as AuditEntry
}

// ── The person chip and the Show chip ────────────────────────────────────────────────────────

test('a person chip asks the server for the person, with the platform it was named on', () => {
  const query = auditQueryFrom([personChip('Discord', '1234')])
  assert.equal(query.person, '1234')
  assert.equal(query.personPlatform, 'Discord')
})

test('Show narrows to one of the three, and Everything narrows nothing', () => {
  assert.equal(auditQueryFrom(PERSON_ACTIVITY_START).show, undefined)
  assert.equal(auditQueryFrom([{ property: 'show', operator: 'is', values: ['moderation'] }]).show, 'moderation')
  assert.equal(auditQueryFrom([{ property: 'show', operator: 'is', values: ['presence'] }]).show, 'presence')
  // A stale or hand-written value is everything rather than a request the server would refuse.
  assert.equal(auditQueryFrom([{ property: 'show', operator: 'is', values: ['nonsense'] }]).show, undefined)
})

test('the popup asks about the account its address named', () => {
  assert.deepEqual(askedOf({ kind: 'person', id: 'usr_ada' }), { platform: 'VRChat', id: 'usr_ada' })
  assert.deepEqual(askedOf({ kind: 'discord-person', id: '1234' }), { platform: 'Discord', id: '1234' })
  assert.deepEqual(askedOf({ kind: 'account', id: 'acc' }), { platform: 'Modbot', id: 'acc' })
})

// ── Which account a row was found under ──────────────────────────────────────────────────────

test('a row names the account it was found under, by platform as well as id', () => {
  assert.equal(foundUnder(person, entry({})), 'VRChat')
  assert.equal(foundUnder(person, entry({ subjectPlatform: 'Discord', subjectId: '1234', type: 'discord.member.ban' })), 'Discord')
  // Something Ada did to somebody else is still found under her account.
  assert.equal(foundUnder(person, entry({ subjectId: 'usr_eve', actorPlatform: 'VRChat', actorId: 'usr_ada' })), 'VRChat')
  // The same id on another platform is not her.
  assert.equal(foundUnder(person, entry({ subjectPlatform: 'Discord', subjectId: 'usr_ada' })), undefined)
})

test('a live event counts when it is about or by any of the accounts', () => {
  const about = { subject: { platform: 'Discord', id: '1234' }, actor: null }
  const by = { subject: { platform: 'VRChat', id: 'usr_eve' }, actor: { platform: 'VRChat', id: 'usr_ada', name: null } }
  const other = { subject: { platform: 'VRChat', id: 'usr_eve' }, actor: null }

  assert.equal(concernsAny(person, about as never), true)
  assert.equal(concernsAny(person, by as never), true)
  assert.equal(concernsAny(person, other as never), false)
})

// ── How they came into the group ─────────────────────────────────────────────────────────────

test('an invite names who sent the newest one', () => {
  const story = joinStory(
    [
      entry({ id: 3, occurredAt: '2026-09-03T10:00:00Z', type: 'vrchat.group.invite.create', actorPlatform: 'VRChat', actorId: 'usr_mod2', actorName: 'Mod Two' }),
      entry({ id: 2, occurredAt: '2026-09-02T10:00:00Z', type: 'vrchat.group.invite.create', actorPlatform: 'VRChat', actorId: 'usr_mod1', actorName: 'Mod One' }),
    ],
    'usr_ada',
  )

  assert.equal(story.invitedBy?.id, 'usr_mod2')
  assert.equal(story.approvedBy, null)
})

test("Modbot's auto-invite names Modbot", () => {
  const story = joinStory([entry({ type: 'modbot.group.auto-invite', source: 'Modbot' })], 'usr_ada')
  assert.equal(story.invitedBy?.name, 'Modbot')
  assert.equal(story.invitedBy?.id, null)
})

test('requests and refusals are counted once per decision, naming who pressed the button', () => {
  // Modbot's own refusal and VRChat's record of it are one decision: the VRChat fact rides inside
  // Modbot's as a linked fact, and the moderator is named rather than the bot account.
  const vrchatsRecord = entry({ id: 11, type: 'vrchat.group.request.reject', actorPlatform: 'VRChat', actorId: 'usr_bot', actorName: 'Bot' })
  const story = joinStory(
    [
      entry({ id: 10, occurredAt: '2026-09-05T10:00:00Z', type: 'modbot.action.request.reject', source: 'Manual', actorPlatform: 'Modbot', actorId: 'acc_mira', actorName: 'mira', linked: [vrchatsRecord] }),
      entry({ id: 9, occurredAt: '2026-09-04T10:00:00Z', type: 'vrchat.group.request.create' }),
      entry({ id: 8, occurredAt: '2026-09-03T10:00:00Z', type: 'vrchat.group.request.reject', actorPlatform: 'VRChat', actorId: 'usr_mod1', actorName: 'Mod One' }),
      entry({ id: 7, occurredAt: '2026-09-02T10:00:00Z', type: 'vrchat.group.request.create' }),
      entry({ id: 6, occurredAt: '2026-09-01T10:00:00Z', type: 'vrchat.group.request.create' }),
    ],
    'usr_ada',
  )

  assert.equal(story.asked, 3)
  assert.equal(story.rejected.count, 2)
  assert.deepEqual(story.rejected.by.map((w) => w.name), ['mira', 'Mod One'])
})

test('approved by Modbot names the moderator; a VRChat join only counts after a request with no invite', () => {
  const approved = joinStory(
    [entry({ type: 'modbot.action.request.approve', source: 'Manual', actorPlatform: 'Modbot', actorId: 'acc_mira', actorName: 'mira' })],
    'usr_ada',
  )
  assert.equal(approved.approvedBy?.name, 'mira')

  const join = entry({ id: 5, occurredAt: '2026-09-05T10:00:00Z', type: 'vrchat.group.member.join', actorPlatform: 'VRChat', actorId: 'usr_mod1', actorName: 'Mod One' })
  const asked = entry({ id: 4, occurredAt: '2026-09-04T10:00:00Z', type: 'vrchat.group.request.create' })
  const invited = entry({ id: 3, occurredAt: '2026-09-03T10:00:00Z', type: 'vrchat.group.invite.create', actorPlatform: 'VRChat', actorId: 'usr_mod2', actorName: 'Mod Two' })

  assert.equal(joinStory([join, asked], 'usr_ada').approvedBy?.id, 'usr_mod1')
  // With no request, the join's actor could be anybody's invite being taken up: say nothing.
  assert.equal(joinStory([join], 'usr_ada').approvedBy, null)
  // With an invite on record, the invite is the line to read.
  assert.equal(joinStory([join, asked, invited], 'usr_ada').approvedBy, null)
  // Joining by themselves names nobody.
  assert.equal(joinStory([{ ...join, actorId: 'usr_ada' }, asked], 'usr_ada').approvedBy, null)
})

test('nothing on record is nothing to say', () => {
  assert.equal(hasStory(joinStory([], 'usr_ada')), false)
  assert.equal(hasStory(joinStory([entry({ type: 'vrchat.group.request.block' })], 'usr_ada')), true)
})

test('counts read as words', () => {
  assert.equal(times(1), 'once')
  assert.equal(times(2), 'twice')
  assert.equal(times(3), '3 times')
})
