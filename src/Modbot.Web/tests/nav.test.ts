import assert from 'node:assert/strict'
import { test } from 'node:test'
import {
  CREDITS_PATH,
  GO_TO_KEYS,
  IAM_PATH,
  MOVED,
  NAV,
  goesByName,
  mayOpen,
  oldMembersAddress,
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

test('People is its own page and asks for See profiles, not See members', () => {
  assert.equal(mayOpen(person('ViewProfile'), 'people'), true)
  assert.equal(mayOpen(person('ViewMembers'), 'people'), false)
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
  assert.equal(sidebarEntry('members'), 'members')
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

test('an old member list address with its filters goes on to /members, keeping them', () => {
  assert.equal(oldMembersAddress('/', '?f=status:is:current'), '/members?f=status%3Ais%3Acurrent')
  assert.equal(oldMembersAddress('/', '?page=3&subject=usr_1'), '/members?page=3&subject=usr_1')
})

test('a plain /, a person link on /, and every other address stay where they are', () => {
  assert.equal(oldMembersAddress('/', ''), null)
  assert.equal(oldMembersAddress('/', '?subject=usr_1'), null)
  assert.equal(oldMembersAddress('/members', '?f=status:is:current'), null)
  assert.equal(oldMembersAddress('/audit', '?f=type:is:x'), null)
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

test('Worlds stays in the sidebar under VRChat', () => {
  const worlds = NAV.find((n) => n.id === 'analytics-worlds')

  assert.ok(worlds && !('hidden' in worlds) && 'indent' in worlds && worlds.indent)
})

test("the VRChat page's Posts and Settings tabs light VRChat and ask for their own permissions", () => {
  assert.equal(sidebarEntry('group-posts'), 'analytics-group')
  assert.equal(sidebarEntry('group-settings'), 'analytics-group')

  assert.equal(mayOpen(person('ViewAnalytics'), 'group-posts'), true)
  assert.equal(mayOpen(person('ViewMembers'), 'group-posts'), false)

  assert.equal(mayOpen(person('ViewAnalytics'), 'group-settings'), false)
  assert.equal(mayOpen(person('EditGroupProfile'), 'group-settings'), true)
})
