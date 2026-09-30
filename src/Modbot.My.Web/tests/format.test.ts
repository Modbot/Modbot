import assert from 'node:assert/strict'
import { test } from 'node:test'
import { ago } from '../src/lib/format.ts'

// The same steps as the moderator app's `ago` (src/Modbot.Web/tests/format.test.ts) and the
// server's TimeWords.Age, so the numbers here are theirs.
const NOW = Date.parse('2026-09-30T12:00:00Z')
const before = (seconds: number) => new Date(NOW - seconds * 1000).toISOString()

test('an age is one unit against its number, largest first, and never in weeks', () => {
  assert.equal(ago(before(0), NOW), '0s ago')
  assert.equal(ago(before(45), NOW), '45s ago')
  assert.equal(ago(before(90), NOW), '2m ago')
  assert.equal(ago(before(5 * 60), NOW), '5m ago')
  assert.equal(ago(before(3 * 3600), NOW), '3h ago')
  assert.equal(ago(before(10 * 86_400), NOW), '10d ago')
  assert.equal(ago(before(44 * 86_400), NOW), '44d ago')
  assert.equal(ago(before(60 * 86_400), NOW), '1mth ago')
  assert.equal(ago(before(180 * 86_400), NOW), '5mth ago')
  assert.equal(ago(before(730 * 86_400), NOW), '2y ago')
})

test('an age from a clock that runs ahead is "just now", and a missing date is nothing', () => {
  assert.equal(ago(before(-30), NOW), 'just now')
  assert.equal(ago('not a date', NOW), '')
})
