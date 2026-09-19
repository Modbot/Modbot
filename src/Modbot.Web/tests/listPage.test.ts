import assert from 'node:assert/strict'
import { test } from 'node:test'
import { GAP, PARAM, pageHref, pageNumbers, readPage, writePage } from '../src/lib/listPage.ts'

test('a list with nothing said about its page is on the first one', () => {
  assert.equal(readPage(new URLSearchParams('')), 1)
  assert.equal(readPage(new URLSearchParams('f=status:is:current')), 1)
})

test('a page number survives the trip through the address', () => {
  const params = new URLSearchParams()
  writePage(params, 7)

  assert.equal(params.get(PARAM), '7')
  assert.equal(readPage(new URLSearchParams(params.toString())), 7)
})

test('anything that is not a whole page number reads as the first page', () => {
  for (const said of ['', '0', '-3', '2.5', 'three', 'NaN', 'Infinity', '1e3', '+4', ' 4 ', '99999999999999999999']) {
    assert.equal(readPage(new URLSearchParams(`${PARAM}=${encodeURIComponent(said)}`)), 1, said)
  }
})

test('the first page is written as nothing, so a plain list has a plain address', () => {
  const params = new URLSearchParams(`f=status:is:current&${PARAM}=4`)
  writePage(params, 1)

  assert.equal(params.has(PARAM), false)
  assert.deepEqual(params.getAll('f'), ['status:is:current'])
})

test('turning a page keeps the filters and the rest of the address', () => {
  const href = pageHref('?f=status:is:left&subject=usr_a', '/members', 3)
  const params = new URLSearchParams(href.slice(href.indexOf('?')))

  assert.ok(href.startsWith('/members?'))
  assert.deepEqual(params.getAll('f'), ['status:is:left'])
  assert.equal(params.get('subject'), 'usr_a')
  assert.equal(readPage(params), 3)
})

test('going back to the first page leaves no page number behind', () => {
  assert.equal(pageHref(`?${PARAM}=9`, '/bans', 1), '/bans')
  assert.equal(pageHref(`?f=&${PARAM}=9`, '/bans', 1), '/bans?f=')
})

test('a short list shows every page number', () => {
  assert.deepEqual(pageNumbers(1, 1), [1])
  assert.deepEqual(pageNumbers(1, 5), [1, 2, 3, 4, 5])
  assert.deepEqual(pageNumbers(4, 7), [1, 2, 3, 4, 5, 6, 7])
})

test('a long list shows both ends and where the reader is', () => {
  assert.deepEqual(pageNumbers(1, 200), [1, 2, 3, GAP, 200])
  assert.deepEqual(pageNumbers(9, 200), [1, GAP, 7, 8, 9, 10, 11, GAP, 200])
  assert.deepEqual(pageNumbers(200, 200), [1, GAP, 198, 199, 200])
})

test('a gap standing for one page is that page instead', () => {
  // 1 … 3 4 5 6 7 … 200 would hide only page 2 behind the first gap.
  assert.deepEqual(pageNumbers(5, 200), [1, 2, 3, 4, 5, 6, 7, GAP, 200])
  assert.deepEqual(pageNumbers(196, 200), [1, GAP, 194, 195, 196, 197, 198, 199, 200])
})

test('a page past the end of the list draws the end of it', () => {
  assert.deepEqual(pageNumbers(40, 3), [1, 2, 3])
  assert.deepEqual(pageNumbers(0, 3), [1, 2, 3])
})
