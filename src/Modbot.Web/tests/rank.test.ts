import assert from 'node:assert/strict'
import { test } from 'node:test'
import type { CurrentUser } from '../src/lib/api.ts'
import { isBelowMe } from '../src/lib/permissions.ts'

function person(rank: number | null, ...permissionNames: string[]): CurrentUser {
  return { permissionNames, rank } as CurrentUser
}

test('a role or account below you has a bigger position than yours', () => {
  const lead = person(3, 'ManageUsers')
  assert.equal(isBelowMe(lead, 4), true)
  assert.equal(isBelowMe(lead, 9), true)
})

test('the same rank, or a higher one, is not below you', () => {
  const lead = person(3, 'ManageUsers')
  assert.equal(isBelowMe(lead, 3), false)
  assert.equal(isBelowMe(lead, 2), false)
  assert.equal(isBelowMe(lead, 0), false)
})

test('an account with no role is below everybody who holds one', () => {
  assert.equal(isBelowMe(person(3, 'ManageUsers'), null), true)
})

test('nothing is below a person who holds no role', () => {
  assert.equal(isBelowMe(person(null, 'ManageUsers'), null), false)
  assert.equal(isBelowMe(person(null, 'ManageUsers'), 5), false)
})

test('an administrator is above every rank, the first included', () => {
  const admin = person(0, 'Administrator')
  assert.equal(isBelowMe(admin, 0), true)
  assert.equal(isBelowMe(admin, null), true)
})
