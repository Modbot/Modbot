import assert from 'node:assert/strict'
import { test } from 'node:test'
import { CREDITS_PATH, GO_TO_KEYS, MOVED, NAV, mayOpen } from '../src/lib/nav.ts'
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

test('Settings keeps the Setup heading now that Sync health is not there to carry it', () => {
  const settings = NAV.find((n) => n.id === 'settings')

  assert.ok(settings && 'group' in settings && settings.group === 'Setup')
})

test('People is its own page and asks for See profiles, not See members', () => {
  assert.equal(mayOpen(person('ViewProfile'), 'people'), true)
  assert.equal(mayOpen(person('ViewMembers'), 'people'), false)
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
