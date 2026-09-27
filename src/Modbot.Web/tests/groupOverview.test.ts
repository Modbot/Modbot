import assert from 'node:assert/strict'
import { test } from 'node:test'
import {
  groupCode,
  groupTabFrom,
  groupTabHref,
  groupTabs,
  isWebLink,
  languageName,
  linkLabel,
  nextEvent,
  settingsTab,
} from '../src/lib/groupOverview.ts'
import { membersAddress } from '../src/lib/nav.ts'
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
      ['Gallery', 'group-gallery'],
      ['Members', 'members'],
      ['Invites', 'group-invites'],
      ['Settings', 'group-settings'],
      ['Banned Users', 'bans'],
    ],
  )
})

test('a tab the person may not open is not offered, the same as in the sidebar', () => {
  assert.deepEqual(
    groupTabs(person('ViewAnalytics')).map((t) => t.label),
    ['Overview', 'Posts', 'Instances', 'Gallery'],
  )
  assert.deepEqual(
    groupTabs(person('ViewAnalytics', 'ViewCalendar', 'EditGroupProfile')).map((t) => t.label),
    ['Overview', 'Posts', 'Events', 'Instances', 'Gallery', 'Settings'],
  )
})

test('Invites is only offered to somebody who may manage invites', () => {
  assert.ok(!groupTabs(person('ViewAnalytics')).some((t) => t.id === 'group-invites'))
  assert.ok(groupTabs(person('ViewAnalytics', 'ManageGroupInvites')).some((t) => t.id === 'group-invites'))
})

test('Settings opens at Roles for somebody who may manage roles but not the profile', () => {
  const tabs = groupTabs(person('ViewAnalytics', 'ManageGroupRoles'))
  const settings = tabs.find((t) => t.label === 'Settings')

  assert.equal(settings?.id, 'group-roles')
  assert.equal(settingsTab(person('ViewAnalytics', 'ManageGroupRoles')), 'group-roles')
  assert.equal(settingsTab(person('ViewAnalytics', 'EditGroupProfile', 'ManageGroupRoles')), 'group-settings')
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

test("a tab that leads to a page of its own says it came from the group; the VRChat page's own tabs do not", () => {
  assert.equal(groupTabHref('calendar', '/calendar'), '/calendar?from=group')
  assert.equal(groupTabHref('members', '/members'), '/members?from=group')
  assert.equal(groupTabHref('bans', '/bans'), '/bans?from=group')
  assert.equal(groupTabHref('audit', '/audit'), '/audit?from=group')
  assert.equal(groupTabHref('audit', '/audit?type=ban'), '/audit?type=ban&from=group')
  assert.equal(groupTabHref('group-posts', '/analytics/group/posts'), '/analytics/group/posts')
  assert.equal(groupTabHref('analytics-group', '/analytics/group'), '/analytics/group')
})

test('a page opened from the tab row marks its tab; opened any other way it marks nothing', () => {
  const admin = person('Administrator')
  const from = new URLSearchParams('from=group')

  assert.equal(groupTabFrom('calendar', from, admin), 'calendar')
  assert.equal(groupTabFrom('bans', from, admin), 'bans')
  assert.equal(groupTabFrom('people', from, admin), 'members')
  assert.equal(groupTabFrom('audit', from, admin), 'group-settings')
  assert.equal(groupTabFrom('audit', from, person('ViewAuditLog', 'ManageGroupRoles')), 'group-roles')

  assert.equal(groupTabFrom('bans', new URLSearchParams(''), admin), null)
  assert.equal(groupTabFrom('bans', new URLSearchParams('from=elsewhere'), admin), null)
  assert.equal(groupTabFrom('live', from, admin), null)
})

test('the Members tab still says where it came from after /members moves on to People', () => {
  const moved = membersAddress('/members', '?from=group')
  assert.ok(moved?.startsWith('/people?'))
  assert.equal(new URLSearchParams(moved!.slice('/people'.length)).get('from'), 'group')
})
