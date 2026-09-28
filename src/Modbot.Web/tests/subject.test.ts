import assert from 'node:assert/strict'
import { test } from 'node:test'
import { decodeSubject, encodeSubject, isPerson, openingTab, sameSubject } from '../src/lib/subject.ts'

/**
 * The address vocabulary: which kinds exist, how a link writes one, and what a link written
 * before a kind existed still means.
 *
 * Worth pinning because these values are in links people have already pasted into Discord, into
 * emails and into each other's messages. A change here is a change to addresses Modbot does not
 * hold and cannot fix.
 */

test('a person is written bare, so every link already pasted somewhere still opens them', () => {
  assert.equal(encodeSubject({ kind: 'person', id: 'usr_abc' }), 'usr_abc')
  assert.deepEqual(decodeSubject('usr_abc'), { kind: 'person', id: 'usr_abc' })
})

test('anything with no prefix is a person, whatever shape the id is', () => {
  // A legacy VRChat id follows no structure, so nothing may read one to decide what it is.
  assert.deepEqual(decodeSubject('8JoV9XEdpo'), { kind: 'person', id: '8JoV9XEdpo' })
})

test('every other kind carries its own prefix', () => {
  for (const kind of ['world', 'instance', 'discord-person', 'account', 'case'] as const) {
    const subject = { kind, id: 'x' }
    assert.equal(encodeSubject(subject), `${kind}:x`)
    assert.deepEqual(decodeSubject(`${kind}:x`), subject)
  }
})

test('a Modbot account round-trips as its own kind rather than as a person', () => {
  const id = '0195f2c1-6f3e-7a10-9b2c-4d5e6f708192'

  assert.equal(encodeSubject({ kind: 'account', id }), `account:${id}`)
  assert.deepEqual(decodeSubject(`account:${id}`), { kind: 'account', id })
})

test('a prefix inside an id is not read as a kind', () => {
  // Only the start of the value is a prefix. An id that happens to contain "account:" is an id.
  assert.deepEqual(decodeSubject('usr_account:7'), { kind: 'person', id: 'usr_account:7' })
})

test('a prefix with nothing after it is not a kind', () => {
  assert.deepEqual(decodeSubject('account:'), { kind: 'person', id: 'account:' })
})

test('the three ways of naming a human being are one view; places are not', () => {
  assert.equal(isPerson({ kind: 'person', id: 'usr_a' }), true)
  assert.equal(isPerson({ kind: 'discord-person', id: 'd_1' }), true)
  assert.equal(isPerson({ kind: 'account', id: 'a_1' }), true)

  assert.equal(isPerson({ kind: 'world', id: 'wrld_a' }), false)
  assert.equal(isPerson({ kind: 'instance', id: 'i_1' }), false)
})

test('the same id under two kinds is two different subjects', () => {
  // They open the same popup, but the address has to say which id it holds: a Discord id and a
  // VRChat id are different things and their shapes say nothing.
  assert.equal(sameSubject({ kind: 'person', id: 'x' }, { kind: 'discord-person', id: 'x' }), false)
  assert.equal(sameSubject({ kind: 'account', id: 'x' }, { kind: 'account', id: 'x' }), true)
})

test('nothing decodes from an empty value', () => {
  assert.equal(decodeSubject(''), null)
})

test('a case file is on the stack but is not a person', () => {
  // Opened from a person's Cases tab or from the ban dialog, it sits on top of the person and
  // closing it goes back to them. It names a case file, not a human being.
  assert.deepEqual(decodeSubject('case:9b1d'), { kind: 'case', id: '9b1d' })
  assert.equal(isPerson({ kind: 'case', id: '9b1d' }), false)
})

test('a tab that was taken out still opens where its content went', () => {
  const tabs = ['overview', 'logs', 'json'] as const
  const moved = { metrics: 'overview' } as const

  // A person link pasted before Metrics folded into Overview.
  assert.equal(openingTab('metrics', 'logs', tabs, moved), 'overview')
  // Raw data is still a tab, only no longer in the row, so its old link opens it as it is.
  assert.equal(openingTab('json', 'overview', tabs, moved), 'json')
  assert.equal(openingTab('logs', 'overview', tabs, moved), 'logs')
})

test('a tab nobody knows opens on the fallback, not on whatever the link said', () => {
  const tabs = ['overview', 'logs'] as const

  assert.equal(openingTab(null, 'overview', tabs), 'overview')
  assert.equal(openingTab('nonsense', 'overview', tabs, { metrics: 'overview' }), 'overview')
  // Not one of the object's own keys, so not a moved tab.
  assert.equal(openingTab('toString', 'logs', tabs, { metrics: 'overview' }), 'logs')
})
