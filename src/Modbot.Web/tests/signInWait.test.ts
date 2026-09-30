import assert from 'node:assert/strict'
import { test } from 'node:test'
import { signInWaitText, timeLeftText } from '../src/lib/signInWait.ts'

test('minutes and seconds', () => {
  assert.equal(timeLeftText(3572), '59m 32s')
  assert.equal(timeLeftText(3600), '60m')
  assert.equal(timeLeftText(120), '2m')
})

test('one of each reads the same as many', () => {
  assert.equal(timeLeftText(61), '1m 1s')
  assert.equal(timeLeftText(60), '1m')
  assert.equal(timeLeftText(62), '1m 2s')
  assert.equal(timeLeftText(121), '2m 1s')
  assert.equal(timeLeftText(1), '1s')
})

test('under a minute is seconds only', () => {
  assert.equal(timeLeftText(45), '45s')
  assert.equal(timeLeftText(59), '59s')
})

test('zero and below is now', () => {
  assert.equal(timeLeftText(0), 'now')
  assert.equal(timeLeftText(-5), 'now')
})

test('a part second rounds up, so the count never shows zero early', () => {
  assert.equal(timeLeftText(0.2), '1s')
})

test('the banner text', () => {
  assert.equal(
    signInWaitText(3572),
    'Service account authentication issue - ratelimited - retrying in 59m 32s',
  )
  assert.equal(signInWaitText(45), 'Service account authentication issue - ratelimited - retrying in 45s')
  assert.equal(signInWaitText(0), 'Service account authentication issue - ratelimited - retrying now')
})
