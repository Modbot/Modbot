import test from 'node:test'
import assert from 'node:assert/strict'
import { briefPieces, offersBriefs } from '../src/lib/briefs.ts'
import type { CurrentUser } from '../src/lib/api.ts'

const me = (fields: Partial<CurrentUser>): CurrentUser =>
  ({ permissionNames: [], chatOn: true, briefsOn: true, ...fields }) as CurrentUser

test('briefs are offered only with the switch on and Use AI chat held', () => {
  assert.equal(offersBriefs(me({ permissionNames: ['UseAiChat'] })), true)
  assert.equal(offersBriefs(me({ permissionNames: ['Administrator'] })), true)
  assert.equal(offersBriefs(me({ permissionNames: ['UseAiChat'], briefsOn: false })), false)
  assert.equal(offersBriefs(me({ permissionNames: ['ViewAuditLog'] })), false)
})

test('a citation becomes its ids, and only ids that were sent are links', () => {
  const pieces = briefPieces('14:02 Ada joined [#10, #12]\n14:05 Ada left [#99]', [10, 12])

  assert.deepEqual(pieces, [
    { text: '14:02 Ada joined ' },
    { ids: [{ id: 10, link: true }, { id: 12, link: true }] },
    { text: '\n14:05 Ada left ' },
    { ids: [{ id: 99, link: false }] },
  ])
})

test('a number outside square brackets is never taken for an id', () => {
  const pieces = briefPieces('The Black Cat #39047 opened at 20:00 [#7]', [7, 39047])

  assert.deepEqual(pieces, [{ text: 'The Black Cat #39047 opened at 20:00 ' }, { ids: [{ id: 7, link: true }] }])
})

test('text with no citation is one piece', () => {
  assert.deepEqual(briefPieces('Nothing cited.', [1]), [{ text: 'Nothing cited.' }])
})
