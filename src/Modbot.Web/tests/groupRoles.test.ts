import assert from 'node:assert/strict'
import { test } from 'node:test'
import type { GroupRoleRow } from '../src/lib/api.ts'
import {
  emptyRoleDraft,
  permissionChoices,
  permissionCount,
  roleBody,
  roleDraftOf,
  roleProblem,
} from '../src/lib/groupRoles.ts'

function role(change: Partial<GroupRoleRow> = {}): GroupRoleRow {
  return {
    id: 'grol_1',
    name: 'Moderator',
    description: 'Keeps the peace',
    permissions: ['group-bans-manage', 'group-audit-view'],
    order: 2,
    isDefault: false,
    isManagementRole: true,
    isSelfAssignable: false,
    requiresTwoFactor: false,
    heldByModbot: false,
    ...change,
  }
}

test('a role needs a name', () => {
  assert.equal(roleProblem(emptyRoleDraft()), 'A role needs a name.')
  assert.equal(roleProblem({ ...emptyRoleDraft(), name: '   ' }), 'A role needs a name.')
  assert.equal(roleProblem({ ...emptyRoleDraft(), name: 'Helper' }), null)
})

test('a new role sends every field, trimmed', () => {
  assert.deepEqual(roleBody({ name: ' Helper ', description: ' Helps ', permissions: ['group-audit-view'] }, null), {
    name: 'Helper',
    description: 'Helps',
    permissions: ['group-audit-view'],
  })
})

test('a change sends only what differs, and nothing when nothing does', () => {
  const r = role()

  assert.equal(roleBody(roleDraftOf(r), r), null)
  assert.deepEqual(roleBody({ ...roleDraftOf(r), name: 'Mod' }, r), { id: 'grol_1', name: 'Mod' })
  assert.deepEqual(roleBody({ ...roleDraftOf(r), description: '' }, r), { id: 'grol_1', description: '' })
})

test('permissions in a different order are not a change', () => {
  const r = role()
  assert.equal(roleBody({ ...roleDraftOf(r), permissions: ['group-audit-view', 'group-bans-manage'] }, r), null)
  assert.deepEqual(roleBody({ ...roleDraftOf(r), permissions: ['group-audit-view'] }, r), {
    id: 'grol_1',
    permissions: ['group-audit-view'],
  })
})

test("the form offers VRChat's permissions by their labels, and keeps ones this build does not know", () => {
  const choices = permissionChoices(['group-something-new'])

  assert.ok(choices.some((c) => c.id === 'group-bans-manage' && c.label === 'Manage Group Bans'))
  assert.ok(choices.some((c) => c.id === 'group-something-new' && c.label === 'group-something-new'))
  assert.ok(!choices.some((c) => c.id === '*'))
})

test('every permission is offered only on a role that has it', () => {
  assert.ok(permissionChoices(['*']).some((c) => c.id === '*' && c.label === 'Every permission'))
})

test('the count reads as words', () => {
  assert.equal(permissionCount([]), '0 permissions')
  assert.equal(permissionCount(['group-audit-view']), '1 permission')
  assert.equal(permissionCount(['*']), 'Every permission')
})
