import assert from 'node:assert/strict'
import { test } from 'node:test'
import { CREDITS_PATH, GO_TO_KEYS, IAM_PATH, MOVED, NAV, mayOpen } from '../src/lib/nav.ts'
import type { CurrentUser } from '../src/lib/api.ts'

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

test('sync health is off the page list and its permission is unchanged', () => {
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

test('every page in the sidebar has a go-to chord', () => {
  for (const item of NAV) {
    if ('hidden' in item && item.hidden) continue
    assert.notEqual(GO_TO_KEYS[item.id], '', `${item.id} has no letter`)
  }
})
