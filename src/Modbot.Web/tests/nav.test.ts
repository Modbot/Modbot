import assert from 'node:assert/strict'
import { test } from 'node:test'
import {
  CREDITS_PATH,
  GO_TO_KEYS,
  IAM_PATH,
  MOVED,
  NAV,
  goesByName,
  matchRank,
  MEMBERS_PATH,
  mayOpen,
  membersAddress,
  otherWords,
  sidebarEntry,
  titleWithCount,
  waitingTotal,
} from '../src/lib/nav.ts'
import type { CurrentUser } from '../src/lib/api.ts'

test('the waiting total adds up the counts the sidebar shows', () => {
  assert.equal(waitingTotal({}), 0)
  assert.equal(waitingTotal({ reviews: 2 }), 2)
  assert.equal(waitingTotal({ reviews: 2, flags: 5 }), 7)
})

test('a count that is not a real positive number adds nothing', () => {
  assert.equal(waitingTotal({ reviews: 0, flags: 3 }), 3)
  assert.equal(waitingTotal({ reviews: -4, flags: 3 }), 3)
  assert.equal(waitingTotal({ reviews: Number.NaN, flags: Number.POSITIVE_INFINITY }), 0)
})

test('the tab title carries the total only while something is waiting', () => {
  assert.equal(titleWithCount('Modbot', 3), '(3) Modbot')
  assert.equal(titleWithCount('Modbot', 0), 'Modbot')
})

function person(...permissionNames: string[]): CurrentUser {
  return { permissionNames } as CurrentUser
}

test('credits is open to somebody with no permissions at all', () => {
  assert.equal(mayOpen(person(), 'credits'), true)
})

test('living under /settings does not put credits behind the settings permission', () => {
  assert.ok(CREDITS_PATH.startsWith('/settings'))
  assert.equal(mayOpen(person(), 'settings'), false)
  assert.equal(mayOpen(person(), 'credits'), true)
})

test('the old credits address still leads to the page', () => {
  assert.equal(MOVED['/credits'], CREDITS_PATH)
})

test('Health is off the page list and its permission is unchanged', () => {
  const health = NAV.find((n) => n.id === 'health')

  assert.ok(health && 'hidden' in health && health.hidden)
  assert.equal(mayOpen(person(), 'health'), false)
  assert.equal(mayOpen(person('ViewOperationalLog'), 'health'), true)
})

test('Settings sits under System, the heading Setup was renamed to', () => {
  const settings = NAV.find((n) => n.id === 'settings')

  assert.ok(settings && 'group' in settings && settings.group === 'System')
  assert.ok(!NAV.some((n) => 'group' in n && n.group === 'Setup'))
})

test('Users and Roles are no longer pages of their own', () => {
  assert.ok(!NAV.some((n) => n.id === 'users'))
  assert.ok(!NAV.some((n) => n.id === 'roles'))
})

test('the old Users and Roles addresses lead to the half of the IAM tab they named', () => {
  assert.equal(MOVED['/users'], `${IAM_PATH}/users`)
  assert.equal(MOVED['/roles'], `${IAM_PATH}/roles`)
  assert.ok(IAM_PATH.startsWith('/settings'))
})

test('Settings opens for somebody who may manage only users or only roles', () => {
  assert.equal(mayOpen(person('ManageUsers'), 'settings'), true)
  assert.equal(mayOpen(person('ManageRoles'), 'settings'), true)
  assert.equal(mayOpen(person('ManageSettings'), 'settings'), true)
  assert.equal(mayOpen(person('ViewMembers'), 'settings'), false)
})

test('People opens with See profiles, and with See members for the Members view', () => {
  assert.equal(mayOpen(person('ViewProfile'), 'people'), true)
  assert.equal(mayOpen(person('ViewMembers'), 'people'), true)
  assert.equal(mayOpen(person('ViewMembers'), 'members'), true)
  assert.equal(mayOpen(person('ViewProfile'), 'members'), false)
  assert.equal(mayOpen(person('ViewAnalytics'), 'people'), false)
})

test('Members is off the page list, a view of People, which the sidebar lights', () => {
  const members = NAV.find((n) => n.id === 'members')

  assert.ok(members && 'hidden' in members && members.hidden)
  assert.equal(sidebarEntry('members'), 'people')
  assert.equal(MEMBERS_PATH, '/people?f=membership%3Ais%3Amember')
})

test('Members is still offered by name and keeps its chord', () => {
  const members = NAV.find((n) => n.id === 'members')

  assert.ok(members && goesByName(members))
  assert.equal(GO_TO_KEYS.members, 'm')
})

test('Requests asks for its own permission, not See members', () => {
  assert.equal(mayOpen(person('ViewJoinRequests'), 'requests'), true)
  assert.equal(mayOpen(person('ViewMembers'), 'requests'), false)
})

test('every page with a go-to chord has its own letter', () => {
  const letters = Object.values(GO_TO_KEYS).filter((l) => l !== '')

  assert.ok(letters.every((l) => /^[a-z]$/.test(l)))
  assert.equal(new Set(letters).size, letters.length)
})

test('no heading in the sidebar shares a name with a page in it', () => {
  const labels = new Set<string>(NAV.map((n) => n.label))
  const groups = NAV.flatMap((n) => ('group' in n ? [n.group as string] : []))

  assert.ok(!groups.some((g) => labels.has(g)), 'a heading repeats a page name')
})

test('Reviews sits beside Flags, with no heading of its own', () => {
  const at = (id: string) => NAV.findIndex((n) => n.id === id)

  assert.equal(at('reviews'), at('flags') + 1)
  assert.ok(!('group' in NAV[at('reviews')]))
})

test("Modbot's own log is not called just Logs, which the audit log and the popup also were", () => {
  assert.equal(NAV.find((n) => n.id === 'logs')?.label, "Modbot's log")
})

test('Discord members is off the page list, shown as part of Discord, which the sidebar lights', () => {
  const discordMembers = NAV.find((n) => n.id === 'discord-members')

  assert.ok(discordMembers && 'hidden' in discordMembers && discordMembers.hidden)
  assert.equal(sidebarEntry('discord-members'), 'analytics-server')
  assert.equal(sidebarEntry('analytics-server'), 'analytics-server')
  assert.equal(mayOpen(person('ViewMembers'), 'discord-members'), true)
})

test('Discord members is still offered by name and keeps its chord', () => {
  const discordMembers = NAV.find((n) => n.id === 'discord-members')

  assert.ok(discordMembers && goesByName(discordMembers))
  assert.equal(GO_TO_KEYS['discord-members'], 'd')
})

test('a page off the list with no page over it is not offered by name', () => {
  for (const id of ['cases', 'credits', 'health', 'account']) {
    const item = NAV.find((n) => n.id === id)
    assert.ok(item && !goesByName(item), `${id} is offered by name`)
  }
})

test('every page shown as part of another points at one in the sidebar', () => {
  for (const item of NAV) {
    if (!('under' in item)) continue
    const over = NAV.find((n) => n.id === item.under)
    assert.ok(over && !('hidden' in over && over.hidden), `${item.id} is under a page not in the sidebar`)
  }
})

test('every page in the sidebar has a go-to chord', () => {
  for (const item of NAV) {
    if ('hidden' in item && item.hidden) continue
    assert.notEqual(GO_TO_KEYS[item.id], '', `${item.id} has no letter`)
  }
})

test('Now is the first page and needs no permission of its own', () => {
  assert.equal(NAV[0].id, 'now')
  assert.equal(mayOpen(person(), 'now'), true)
  assert.equal(GO_TO_KEYS.now, 'k')
})

test('/members goes to the Members view of People, keeping the rest of the address', () => {
  assert.equal(membersAddress('/members', ''), MEMBERS_PATH)
  assert.equal(membersAddress('/members', '?page=3&subject=usr_1'), '/people?page=3&subject=usr_1&f=membership%3Ais%3Amember')
})

test("the member list's Status chip becomes People's Membership chip, and its other chips carry over", () => {
  assert.equal(membersAddress('/members', '?f=status:is:current'), MEMBERS_PATH)
  assert.equal(membersAddress('/members', '?f=status:is:left&f=role:is:grol_a'), '/people?f=membership%3Ais%3Aleft&f=role%3Ais%3Agrol_a')

  // Its "everybody" is no Membership chip at all, and "no filters" stays "no filters".
  assert.equal(membersAddress('/members', '?f=status:is:all&f=hasRole:yes:'), '/people?f=hasRole%3Ayes%3A')
  assert.equal(membersAddress('/members', '?f='), '/people?f=')
})

test('an old member list address on / goes to People too, and so does an old alert', () => {
  assert.equal(membersAddress('/', '?f=status:is:current'), MEMBERS_PATH)
  assert.equal(membersAddress('/', '?page=3&subject=usr_1'), '/people?page=3&subject=usr_1&f=membership%3Ais%3Amember')
  assert.equal(
    membersAddress('/', '?joinedFrom=2026-03-10T12%3A00%3A00Z&joinedTo=2026-03-10T13%3A00%3A00Z'),
    '/people?joinedFrom=2026-03-10T12%3A00%3A00Z&joinedTo=2026-03-10T13%3A00%3A00Z&f=membership%3Ais%3Amember',
  )
})

test('a plain /, a person link on /, and every other address stay where they are', () => {
  assert.equal(membersAddress('/', ''), null)
  assert.equal(membersAddress('/', '?subject=usr_1'), null)
  assert.equal(membersAddress('/people', '?f=membership:is:member'), null)
  assert.equal(membersAddress('/audit', '?f=type:is:x'), null)
})

test("Instances is off the page list, shown as the VRChat page's Instances tab, which lights VRChat", () => {
  const instances = NAV.find((n) => n.id === 'analytics-instances')

  assert.ok(instances && 'hidden' in instances && instances.hidden)
  assert.equal(sidebarEntry('analytics-instances'), 'analytics-group')
  assert.equal(mayOpen(person('ViewAnalytics'), 'analytics-instances'), true)
})

test('Instances is still offered by name and keeps its chord', () => {
  const instances = NAV.find((n) => n.id === 'analytics-instances')

  assert.ok(instances && goesByName(instances))
  assert.equal(GO_TO_KEYS['analytics-instances'], 'i')
})

test('Stats takes Team’s place at the top of the Analytics heading, before VRChat and Discord', () => {
  const at = (id: string) => NAV.findIndex((n) => n.id === id)
  const stats = NAV[at('stats')]

  assert.ok('group' in stats && stats.group === 'Analytics')
  assert.ok(at('stats') < at('analytics-group') && at('analytics-group') < at('analytics-server'))
  assert.ok(!NAV.some((n) => (n.id as string) === 'analytics-team' || (n.id as string) === 'analytics-worlds'))
})

test('the Stats tabs light Stats and need what Stats needs', () => {
  for (const id of ['stats-activity', 'stats-moderation'] as const) {
    assert.equal(sidebarEntry(id), 'stats')
    assert.equal(mayOpen(person('ViewAnalytics'), id), true)
    assert.equal(mayOpen(person('ViewMembers'), id), false)
  }
})

test('Team and Worlds open the Stats tabs they became, and Stats opens on Growth', () => {
  assert.equal(MOVED['/analytics/team'], '/stats/moderation')
  assert.equal(MOVED['/analytics/worlds'], '/stats/activity')
  assert.equal(MOVED['/stats'], '/stats/growth')
})

test('the palette still finds the team and the worlds', () => {
  const find = (typed: string) => NAV.filter((n) => matchRank(typed, n.label, otherWords(n)) !== null).map((n) => n.id)

  assert.ok(find('team').includes('stats-moderation'))
  assert.ok(find('worlds').includes('stats-activity'))
})

test("the VRChat page's Posts and Settings tabs light VRChat and ask for their own permissions", () => {
  assert.equal(sidebarEntry('group-posts'), 'analytics-group')
  assert.equal(sidebarEntry('group-settings'), 'analytics-group')

  assert.equal(mayOpen(person('ViewAnalytics'), 'group-posts'), true)
  assert.equal(mayOpen(person('ViewMembers'), 'group-posts'), false)

  assert.equal(mayOpen(person('ViewAnalytics'), 'group-settings'), false)
  assert.equal(mayOpen(person('EditGroupProfile'), 'group-settings'), true)
})

test("the VRChat page's Roles, Gallery and Invites tabs light VRChat and ask for their own permissions", () => {
  for (const id of ['group-roles', 'group-gallery', 'group-invites'] as const) assert.equal(sidebarEntry(id), 'analytics-group')

  assert.equal(mayOpen(person('EditGroupProfile'), 'group-roles'), false)
  assert.equal(mayOpen(person('ManageGroupRoles'), 'group-roles'), true)

  assert.equal(mayOpen(person('ViewAnalytics'), 'group-gallery'), true)
  assert.equal(mayOpen(person('ViewMembers'), 'group-gallery'), false)

  assert.equal(mayOpen(person('ViewAnalytics'), 'group-invites'), false)
  assert.equal(mayOpen(person('ManageGroupInvites'), 'group-invites'), true)
})

/** The pages the palette offers for what was typed, best match first, as the palette orders them. */
function palette(typed: string): string[] {
  return NAV.filter(goesByName)
    .map((n) => ({ label: n.label, rank: matchRank(typed, n.label, [...otherWords(n), 'Go to']) }))
    .filter((r) => r.rank !== null)
    .sort((a, b) => (a.rank ?? 0) - (b.rank ?? 0))
    .map((r) => r.label)
}

test('the words a moderator types find the page they mean', () => {
  assert.equal(palette('join')[0], 'Requests')
  assert.equal(palette('events')[0], 'Calendar')
  assert.equal(palette('banned')[0], 'Bans')
  assert.equal(palette('kick')[0], 'Audit log')
  assert.equal(palette('voice')[0], 'Live')
})

test("the group's Members comes before Discord members", () => {
  assert.deepEqual(palette('members'), ['Members', 'Discord members'])
})

test('a page named by what was typed comes before one that only answers to it', () => {
  assert.deepEqual(palette('instances').slice(0, 2), ['Instances', 'Live'])
})

test('a match on the label ranks exact, then start, then anywhere, then other words', () => {
  assert.equal(matchRank('audit log', 'Audit log'), 0)
  assert.equal(matchRank('aud', 'Audit log'), 1)
  assert.equal(matchRank('log', 'Audit log'), 2)
  assert.equal(matchRank('history', 'Audit log', ['kick', 'history']), 3)
  assert.equal(matchRank('ban list', 'Bans', ['ban list']), 3)
  assert.equal(matchRank('voice', 'Bans', ['banned']), null)
})

test('case and extra spaces do not matter, and nothing typed matches everything', () => {
  assert.equal(matchRank('  MEMBERS ', 'Members'), 0)
  assert.equal(matchRank('', 'Bans'), 0)
})
