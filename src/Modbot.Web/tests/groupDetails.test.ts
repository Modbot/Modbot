import assert from 'node:assert/strict'
import { test } from 'node:test'
import { countReadings, groupFieldLabel, groupFieldName, withPlainNames } from '../src/lib/groupDetails.ts'

test('a count that only ticked is said as a reading, the online count on its own', () => {
  assert.deepEqual(countReadings([['OnlineMemberCount', { old: 260, new: 263 }]]), [
    'Members online went from 260 to 263.',
  ])
})

test('the member count is said with its thousands separated', () => {
  const [sentence] = countReadings([['MemberCount', { old: 4790, new: 4791 }]])!

  assert.equal(sentence, `Members went from ${(4790).toLocaleString()} to ${(4791).toLocaleString()}.`)
})

test('both counts moving give a sentence each, the members first', () => {
  assert.deepEqual(
    countReadings([
      ['OnlineMemberCount', { old: 260, new: 263 }],
      ['MemberCount', { old: 400, new: 401 }],
    ]),
    ['Members went from 400 to 401.', 'Members online went from 260 to 263.'],
  )
})

test('any other field makes it an edit, not a reading', () => {
  assert.equal(
    countReadings([
      ['OnlineMemberCount', { old: 260, new: 263 }],
      ['Rules', { old: 'Be kind', new: 'Be kind. No crashers.' }],
    ]),
    null,
  )
})

test('nothing changed, or a count with no number on a side, is not a reading', () => {
  assert.equal(countReadings([]), null)
  assert.equal(countReadings([['MemberCount', { old: null, new: 12 }]]), null)
})

test('the poll and the audit log name the same field in different cases, and both read plainly', () => {
  assert.equal(groupFieldName('OnlineMemberCount'), 'members online')
  assert.equal(groupFieldName('JoinState'), 'join state')
  assert.equal(groupFieldName('joinState'), 'join state')
  assert.equal(groupFieldName('Rules'), 'rules')
})

test('a label starts with a capital, and a field it does not know has none', () => {
  assert.equal(groupFieldLabel('OnlineMemberCount'), 'Members online')
  assert.equal(groupFieldLabel('MemberCount'), 'Members')
  assert.equal(groupFieldLabel('Privacy'), 'Privacy')
  assert.equal(groupFieldLabel('bannerId'), null)
})

test('a count beside another change is written with its thousands separated', () => {
  assert.deepEqual(withPlainNames([['MemberCount', { old: 4790, new: 4791 }]]), [
    ['members', { old: (4790).toLocaleString(), new: (4791).toLocaleString() }],
  ])
})

test('plain names go into the changes and unknown fields keep theirs', () => {
  const pair = { old: 'a', new: 'b' }

  assert.deepEqual(withPlainNames([['IsVerified', pair], ['bannerId', pair]]), [
    ['verified', pair],
    ['bannerId', pair],
  ])
})
