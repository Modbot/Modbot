import assert from 'node:assert/strict'
import { test } from 'node:test'
import { groupCode, groupTabs, isWebLink, languageName, linkLabel, nextEvent } from '../src/lib/groupOverview.ts'
import type { CurrentUser } from '../src/lib/api.ts'
import type { CalendarEvent } from '../src/lib/calendar.ts'

function person(...permissionNames: string[]): CurrentUser {
  return { permissionNames } as CurrentUser
}

test('the tabs are named and ordered the way vrchat.com names them, and lead to the matching pages', () => {
  assert.deepEqual(
    groupTabs(person('Administrator')).map((t) => [t.label, t.id]),
    [
      ['Overview', 'analytics-group'],
      ['Posts', 'group-posts'],
      ['Events', 'calendar'],
      ['Instances', 'analytics-instances'],
      ['Members', 'members'],
      ['Settings', 'group-settings'],
      ['Banned Users', 'bans'],
    ],
  )
})

test('a tab the person may not open is not offered, the same as in the sidebar', () => {
  assert.deepEqual(
    groupTabs(person('ViewAnalytics')).map((t) => t.label),
    ['Overview', 'Posts', 'Instances'],
  )
  assert.deepEqual(
    groupTabs(person('ViewAnalytics', 'ViewCalendar', 'EditGroupProfile')).map((t) => t.label),
    ['Overview', 'Posts', 'Events', 'Instances', 'Settings'],
  )
})

test('Settings is only offered to somebody who may change the group', () => {
  assert.ok(!groupTabs(person('ViewAnalytics', 'ManageGroupPosts')).some((t) => t.id === 'group-settings'))
  assert.ok(groupTabs(person('ViewAnalytics', 'EditGroupProfile')).some((t) => t.id === 'group-settings'))
})

test('the group code is the short code and the digits, joined by a dot', () => {
  assert.equal(groupCode('TESTIN', '4698'), 'TESTIN.4698')
  assert.equal(groupCode('TESTIN', null), 'TESTIN')
  assert.equal(groupCode('TESTIN', ' '), 'TESTIN')
  assert.equal(groupCode(null, '4698'), null)
  assert.equal(groupCode('  ', '4698'), null)
})

test('a language code reads as the language', () => {
  assert.equal(languageName('eng', 'en'), 'English')
  assert.equal(languageName('jpn', 'en'), 'Japanese')
  assert.equal(languageName('ENG', 'en'), 'English')
  assert.equal(languageName('bfi', 'en'), 'British Sign Language')
})

test('a code nobody knows is written as it is, in capitals', () => {
  assert.equal(languageName('qqq', 'en'), 'QQQ')
  assert.equal(languageName('not a code', 'en'), 'NOT A CODE')
  assert.equal(languageName('', 'en'), '')
})

test('a link reads without its scheme or a lone slash', () => {
  assert.equal(linkLabel('https://discord.gg/example'), 'discord.gg/example')
  assert.equal(linkLabel('https://www.example.org/'), 'example.org')
  assert.equal(linkLabel('http://example.org/rules/'), 'example.org/rules')
  assert.equal(linkLabel('https://example.org/a?b=c'), 'example.org/a?b=c')
  assert.equal(linkLabel('not a link'), 'not a link')
})

test('only a web address may go in a link', () => {
  assert.equal(isWebLink('https://example.org/'), true)
  assert.equal(isWebLink('http://example.org/'), true)
  assert.equal(isWebLink('javascript:alert(1)'), false)
  assert.equal(isWebLink('not a link'), false)
})

function event(id: string, state: CalendarEvent['state'], ...occurrences: [string, string][]): CalendarEvent {
  return {
    id,
    title: id,
    state,
    occurrences: occurrences.map(([startsAt, endsAt]) => ({ startsAt, endsAt })),
  } as CalendarEvent
}

test('the next event is the earliest occurrence that has not ended', () => {
  const now = '2026-09-26T12:00:00Z'

  const next = nextEvent(
    [
      event('later', 'scheduled', ['2026-09-28T18:00:00Z', '2026-09-28T20:00:00Z']),
      event('past', 'scheduled', ['2026-09-25T18:00:00Z', '2026-09-25T20:00:00Z']),
      event('weekly', 'scheduled', ['2026-09-19T18:00:00Z', '2026-09-19T20:00:00Z'], ['2026-09-27T18:00:00Z', '2026-09-27T20:00:00Z']),
    ],
    now,
  )

  assert.equal(next?.event.id, 'weekly')
  assert.equal(next?.startsAt, '2026-09-27T18:00:00Z')
})

test('an event under way counts until it ends', () => {
  const next = nextEvent(
    [
      event('soon', 'scheduled', ['2026-09-26T18:00:00Z', '2026-09-26T20:00:00Z']),
      event('now', 'open', ['2026-09-26T11:00:00Z', '2026-09-26T13:00:00Z']),
    ],
    '2026-09-26T12:00:00Z',
  )

  assert.equal(next?.event.id, 'now')
})

test('drafts, cancelled and finished events are never the next one', () => {
  const later = ['2026-09-27T18:00:00Z', '2026-09-27T20:00:00Z'] as [string, string]

  assert.equal(
    nextEvent(
      [event('d', 'draft', later), event('c', 'cancelled', later), event('f', 'finished', later)],
      '2026-09-26T12:00:00Z',
    ),
    null,
  )
})

test('no events, no next event', () => {
  assert.equal(nextEvent([], '2026-09-26T12:00:00Z'), null)
})
