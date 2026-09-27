import assert from 'node:assert/strict'
import { test } from 'node:test'
import { shortensAny } from '../src/components/settings/retention.ts'

test('going from forever to a number of days shortens a window', () => {
  assert.equal(shortensAny([0, 0, 0], [30, 0, 0]), true)
})

test('fewer days than before shortens a window', () => {
  assert.equal(shortensAny([90, 0, 0], [30, 0, 0]), true)
})

test('more days, the same days, or back to forever deletes nothing', () => {
  assert.equal(shortensAny([30, 0, 180], [60, 0, 180]), false)
  assert.equal(shortensAny([30, 0, 180], [30, 0, 180]), false)
  assert.equal(shortensAny([30, 10, 180], [0, 0, 0]), false)
})

test('any one window shortening is enough', () => {
  assert.equal(shortensAny([0, 0, 180], [0, 0, 179]), true)
})
