import assert from 'node:assert/strict'
import { test } from 'node:test'
import { ago, howLong } from '../src/lib/format.ts'

const NOW = '2026-09-18T12:00:00Z'

test('how long says the same steps as ago, without the ago', () => {
  assert.equal(howLong('2026-09-18T11:59:30Z', NOW), '30s')
  assert.equal(howLong('2026-09-18T11:30:00Z', NOW), '30m')
  assert.equal(howLong('2026-09-18T06:00:00Z', NOW), '6h')
  assert.equal(howLong('2026-06-20T12:00:00Z', NOW), '90d')

  assert.equal(ago('2026-06-20T12:00:00Z', NOW), '90d ago')
})

test('somebody Modbot has no record of has not been known for any time at all', () => {
  assert.equal(howLong(null, NOW), 'never')
})

test('a clock that ran backwards does not produce a negative age', () => {
  assert.equal(howLong('2026-09-18T12:00:30Z', NOW), '0s')
})
