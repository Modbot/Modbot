import assert from 'node:assert/strict'
import { test } from 'node:test'
import { boostGoal, boostShare, discordPicture, serverInitials, serverTabs } from '../src/lib/serverOverview.ts'
import type { CurrentUser } from '../src/lib/api.ts'

function person(...permissionNames: string[]): CurrentUser {
  return { permissionNames } as CurrentUser
}

test('the links are named the way Discord names them, and lead to the matching pages', () => {
  assert.deepEqual(
    serverTabs(person('Administrator')).map((t) => [t.label, t.id]),
    [
      ['Overview', 'analytics-server'],
      ['Members', 'discord-members'],
      ['Voice now', 'live'],
      ['Events', 'calendar'],
      ['Bans', 'bans'],
    ],
  )
})

test('a link the person may not open is not offered, the same as in the sidebar', () => {
  assert.deepEqual(serverTabs(person('ViewAnalytics')).map((t) => t.label), ['Overview'])
  assert.deepEqual(
    serverTabs(person('ViewAnalytics', 'ViewMembers', 'ViewAuditLog')).map((t) => t.label),
    ['Overview', 'Members', 'Bans'],
  )
})

test('the boost goal is the next level, from Discord’s level', () => {
  assert.deepEqual(boostGoal(0, 0), { boosts: 0, level: 0, goal: 2 })
  assert.deepEqual(boostGoal(3, 1), { boosts: 3, level: 1, goal: 7 })
  assert.deepEqual(boostGoal(9, 2), { boosts: 9, level: 2, goal: 14 })
  assert.deepEqual(boostGoal(20, 3), { boosts: 20, level: 3, goal: null })
})

test('a level kept after boosts ran out is Discord’s, not worked out from the count', () => {
  assert.deepEqual(boostGoal(5, 2), { boosts: 5, level: 2, goal: 14 })
})

test('with no level, the level is worked out from the count', () => {
  assert.deepEqual(boostGoal(1, null), { boosts: 1, level: 0, goal: 2 })
  assert.deepEqual(boostGoal(7, null), { boosts: 7, level: 2, goal: 14 })
  assert.deepEqual(boostGoal(14, null), { boosts: 14, level: 3, goal: null })
  assert.deepEqual(boostGoal(4, 9), { boosts: 4, level: 1, goal: 7 })
})

test('no count, no bar', () => {
  assert.equal(boostGoal(null, 2), null)
  assert.equal(boostGoal(-1, 0), null)
  assert.equal(boostGoal(Number.NaN, 0), null)
})

test('the bar fills towards the goal, and is full at level 3', () => {
  assert.equal(boostShare({ boosts: 3, level: 1, goal: 7 }), 3 / 7)
  assert.equal(boostShare({ boosts: 20, level: 3, goal: null }), 1)
  assert.equal(boostShare({ boosts: 9, level: 1, goal: 7 }), 1)
})

test('with no icon, the server is drawn with the first letter of each word', () => {
  assert.equal(serverInitials('The Black Cat'), 'TBC')
  assert.equal(serverInitials('  Thy   Kingdom '), 'TK')
  assert.equal(serverInitials('a very long server name'), 'avl')
  assert.equal(serverInitials('[VR] Club'), 'VC')
  assert.equal(serverInitials('日本 サーバー'), '日サ')
  assert.equal(serverInitials('!!! ???'), null)
  assert.equal(serverInitials(''), null)
  assert.equal(serverInitials(null), null)
})

test('a Discord picture is asked for at a size; anything else is left alone, and only https is kept', () => {
  assert.equal(
    discordPicture('https://cdn.discordapp.com/icons/1/abc.png', 256),
    'https://cdn.discordapp.com/icons/1/abc.png?size=256',
  )
  assert.equal(
    discordPicture('https://cdn.discordapp.com/icons/1/abc.png?size=64', 256),
    'https://cdn.discordapp.com/icons/1/abc.png?size=256',
  )
  assert.equal(discordPicture('https://example.com/a.png', 256), 'https://example.com/a.png')
  assert.equal(discordPicture('http://cdn.discordapp.com/icons/1/abc.png', 256), null)
  assert.equal(discordPicture('javascript:alert(1)', 256), null)
  assert.equal(discordPicture(null, 256), null)
})
