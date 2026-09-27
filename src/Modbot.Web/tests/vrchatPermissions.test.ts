import assert from 'node:assert/strict'
import { test } from 'node:test'
import {
  missingPermissionOf,
  missingPermissionParts,
  missingPermissionText,
  vrchatPermissionLabel,
  vrchatRolesPage,
} from '../src/lib/vrchatPermissions.ts'
import { noPermissionText } from '../src/lib/moderationActions.ts'
import type { MissingGroupPermission } from '../src/lib/api.ts'

function missing(over: Partial<MissingGroupPermission> = {}): MissingGroupPermission {
  return { permission: 'group-invites-manage', groupId: 'grp_1', roles: ['Greeter'], said: null, ...over }
}

test('the permission is named in VRChat’s own words', () => {
  assert.equal(vrchatPermissionLabel('group-invites-manage'), 'Manage Group Invites')
  assert.equal(vrchatPermissionLabel('group-bans-manage'), 'Manage Group Bans')
  assert.equal(vrchatPermissionLabel('group-something-new'), 'group-something-new')
})

test('the link goes to the group’s roles page on vrchat.com', () => {
  assert.equal(vrchatRolesPage('grp_1'), 'https://vrchat.com/home/group/grp_1/settings/roles')
  assert.equal(vrchatRolesPage('grp 1/x'), 'https://vrchat.com/home/group/grp%201%2Fx/settings/roles')
})

test('the message names the permission and the account’s role', () => {
  assert.equal(
    missingPermissionText(missing()),
    'Modbot\'s VRChat account needs Manage Group Invites in this group. It has the role "Greeter".',
  )
  assert.equal(missingPermissionParts(missing()).link, 'https://vrchat.com/home/group/grp_1/settings/roles')
})

test('several roles, no roles, and roles not yet read', () => {
  assert.equal(missingPermissionParts(missing({ roles: ['A', 'B', 'C'] })).roles, 'It has the roles "A", "B" and "C".')
  assert.equal(missingPermissionParts(missing({ roles: [] })).roles, 'It has no roles.')
  assert.equal(missingPermissionParts(missing({ roles: null })).roles, null)
  assert.equal(
    missingPermissionText(missing({ roles: null })),
    "Modbot's VRChat account needs Manage Group Invites in this group.",
  )
})

test('an unknown permission is not guessed at', () => {
  assert.equal(missingPermissionParts(missing({ permission: null })).permission, null)
  assert.equal(
    missingPermissionText(missing({ permission: null, roles: null })),
    "Modbot's VRChat account needs a group permission in this group.",
  )
})

test('the list refusal’s body is read, and anything else is not', () => {
  const body = { error: 'Forbidden', missingGroupPermission: missing({ permission: null }) }
  assert.deepEqual(missingPermissionOf(body), missing({ permission: null }))

  assert.equal(missingPermissionOf({ error: 'Forbidden', missingGroupPermission: null }), null)
  assert.equal(missingPermissionOf({ error: 'Forbidden' }), null)
  assert.equal(missingPermissionOf(null), null)
  assert.equal(missingPermissionOf({ missingGroupPermission: { permission: 'x' } }), null)
})

test('Modbot’s own 403 says the person lacks the permission, not VRChat', () => {
  assert.equal(noPermissionText('kick'), "You don't have permission to kick people.")
  assert.equal(noPermissionText('ban'), "You don't have permission to ban people.")
  assert.equal(noPermissionText('unban'), "You don't have permission to unban people.")
})
