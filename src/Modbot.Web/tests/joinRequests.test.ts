import assert from 'node:assert/strict'
import { test } from 'node:test'
import {
  confirmTitle,
  historyNote,
  mayAnswer,
  resultText,
  rowIsAnswered,
} from '../src/lib/joinRequests.ts'
import type { CurrentUser, JoinRequestRow } from '../src/lib/api.ts'

function person(...permissionNames: string[]): CurrentUser {
  return { permissionNames } as CurrentUser
}

function row(over: Partial<JoinRequestRow> = {}): JoinRequestRow {
  return {
    userId: 'usr_1',
    displayName: 'Someone',
    plainName: null,
    avatarThumbnailUrl: null,
    trustRank: null,
    eighteenPlus: false,
    askedAt: null,
    banned: false,
    bannedBefore: false,
    wasMember: false,
    leftAt: null,
    known: false,
    ...over,
  }
}

const refused = { done: false, error: 'VRChat said no.', rateLimited: false, repeat: false, gone: false }

test('seeing the queue is not permission to answer it', () => {
  assert.equal(mayAnswer(person('ViewJoinRequests')), false)
  assert.equal(mayAnswer(person('AnswerJoinRequests')), true)
  assert.equal(mayAnswer(person('Administrator')), true)
  assert.equal(mayAnswer(null), false)
})

test('kicking and banning do not carry answering a join request', () => {
  assert.equal(mayAnswer(person('Kick', 'Ban', 'Unban')), false)
})

test('the confirmation names the person and which answer it is', () => {
  assert.match(confirmTitle('approve', 'Nova'), /Nova/)
  assert.notEqual(confirmTitle('approve', 'Nova'), confirmTitle('reject', 'Nova'))
})

test('a request that is gone is its own outcome, not a failure', () => {
  const gone = { ...refused, gone: true, error: 'That request is no longer waiting.' }

  assert.equal(resultText('approve', gone), 'That request is no longer waiting.')
  assert.equal(rowIsAnswered(gone), true)
})

test('a refusal keeps the row, because nothing happened to it', () => {
  assert.equal(resultText('reject', refused), 'VRChat said no.')
  assert.equal(rowIsAnswered(refused), false)
})

test('a rate limit says nothing happened and never suggests trying again', () => {
  const limited = { ...refused, rateLimited: true, error: 'Waiting it out.' }

  assert.match(resultText('approve', limited), /Nothing happened/)
  assert.doesNotMatch(resultText('approve', limited), /again/i)
  assert.equal(rowIsAnswered(limited), false)
})

test('a done answer takes the row out of the list, and a repeat says so', () => {
  const done = { done: true, error: null, rateLimited: false, repeat: false, gone: false }

  assert.equal(resultText('approve', done), 'Let in.')
  assert.equal(resultText('reject', done), 'Turned down.')
  assert.match(resultText('approve', { ...done, repeat: true }), /Already sent/)
  assert.equal(rowIsAnswered(done), true)
})

test('a standing ban outranks every other thing the row could say', () => {
  assert.equal(historyNote(row({ banned: true, bannedBefore: true, wasMember: true })), 'Banned')
  assert.equal(historyNote(row({ bannedBefore: true, wasMember: true })), 'Banned before')
  assert.equal(historyNote(row({ wasMember: true })), 'Was a member')
  assert.equal(historyNote(row()), null)
})
