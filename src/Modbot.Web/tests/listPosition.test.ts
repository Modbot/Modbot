import assert from 'node:assert/strict'
import { test } from 'node:test'
import { PARAM, positionHref, readPosition, writePosition } from '../src/lib/listPosition.ts'

test('a list with nothing said about its position is on the first page', () => {
  assert.equal(readPosition(new URLSearchParams('')), null)
  assert.equal(readPosition(new URLSearchParams('f=status:is:current')), null)
})

test('a cursor survives the trip through the address, whatever is in it', () => {
  const cursor = 'next!joined!=2026-03-10T12%3A00%3A00.0000000%2B00%3A00!usr_abc'

  const params = new URLSearchParams()
  writePosition(params, cursor)

  assert.equal(readPosition(new URLSearchParams(params.toString())), cursor)
})

test('an empty cursor reads as the first page, not as an empty position', () => {
  assert.equal(readPosition(new URLSearchParams(`${PARAM}=`)), null)
})

test('clearing the position leaves the filters where they were', () => {
  const params = new URLSearchParams('f=status:is:current&f=role:is:grol_mod&cursor=next!joined!-!usr_a')
  writePosition(params, null)

  assert.equal(readPosition(params), null)
  assert.deepEqual(params.getAll('f'), ['status:is:current', 'role:is:grol_mod'])
})

test('turning a page keeps the filters and the rest of the address', () => {
  const href = positionHref('?f=status:is:left&subject=usr_a', '/members', 'next!name!=Alice!usr_a')
  const params = new URLSearchParams(href.slice(href.indexOf('?')))

  assert.ok(href.startsWith('/members?'))
  assert.deepEqual(params.getAll('f'), ['status:is:left'])
  assert.equal(params.get('subject'), 'usr_a')
  assert.equal(readPosition(params), 'next!name!=Alice!usr_a')
})

test('going back to the first page leaves no cursor behind in the address', () => {
  assert.equal(positionHref('?cursor=next!joined!-!usr_a', '/bans', null), '/bans')
  assert.equal(positionHref('?f=&cursor=next!joined!-!usr_a', '/bans', null), '/bans?f=')
})
