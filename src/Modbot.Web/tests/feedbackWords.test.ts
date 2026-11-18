import assert from 'node:assert/strict'
import { test } from 'node:test'
import {
  FEEDBACK_KINDS,
  FEEDBACK_STATUSES,
  feedbackKindLabel,
  feedbackQuery,
  feedbackStatusBadge,
  feedbackStatusLabel,
} from '../src/lib/feedbackWords.ts'

test('the three kinds and four statuses are the ones the server knows, in the server order', () => {
  assert.deepEqual(
    FEEDBACK_KINDS.map((k) => k.value),
    ['bug', 'idea', 'change'],
  )
  assert.deepEqual(
    FEEDBACK_STATUSES.map((s) => s.value),
    ['new', 'reviewed', 'done', 'dropped'],
  )
})

test('kinds and statuses are named in plain words, and an unknown one is shown as it came', () => {
  assert.equal(feedbackKindLabel('bug'), 'Bug')
  assert.equal(feedbackKindLabel('idea'), 'Idea')
  assert.equal(feedbackKindLabel('change'), 'Change')
  assert.equal(feedbackStatusLabel('reviewed'), 'Reviewed')
  assert.equal(feedbackStatusLabel('dropped'), 'Dropped')
  assert.equal(feedbackKindLabel('praise'), 'praise')
  assert.equal(feedbackStatusLabel('later'), 'later')
})

test('a new item stands out and a dropped one is quiet', () => {
  assert.equal(feedbackStatusBadge('new'), 'warn')
  assert.equal(feedbackStatusBadge('done'), 'ok')
  assert.equal(feedbackStatusBadge('dropped'), 'outline')
})

test('the list is read plain for all, and with the status for one', () => {
  assert.equal(feedbackQuery('all'), '')
  assert.equal(feedbackQuery('new'), '?status=new')
  assert.equal(feedbackQuery('dropped'), '?status=dropped')
})
