import assert from 'node:assert/strict'
import { test } from 'node:test'
import { signInWaitText, timeLeftText } from '../src/lib/signInWait.ts'

test('minutes and seconds', () => {
  assert.equal(timeLeftText(3572), '59 minutes and 32 seconds')
  assert.equal(timeLeftText(3600), '60 minutes')
  assert.equal(timeLeftText(120), '2 minutes')
})

test('one of each is singular', () => {
  assert.equal(timeLeftText(61), '1 minute and 1 second')
  assert.equal(timeLeftText(60), '1 minute')
  assert.equal(timeLeftText(62), '1 minute and 2 seconds')
  assert.equal(timeLeftText(121), '2 minutes and 1 second')
  assert.equal(timeLeftText(1), '1 second')
})

test('under a minute is seconds only', () => {
  assert.equal(timeLeftText(45), '45 seconds')
  assert.equal(timeLeftText(59), '59 seconds')
})

test('zero and below is now', () => {
  assert.equal(timeLeftText(0), 'now')
  assert.equal(timeLeftText(-5), 'now')
})

test('a part second rounds up, so the count never shows zero early', () => {
  assert.equal(timeLeftText(0.2), '1 second')
})

test('the banner text', () => {
  assert.equal(
    signInWaitText(3572),
    'Service account authentication issue - ratelimited - retrying in 59 minutes and 32 seconds',
  )
  assert.equal(signInWaitText(45), 'Service account authentication issue - ratelimited - retrying in 45 seconds')
  assert.equal(signInWaitText(0), 'Service account authentication issue - ratelimited - retrying now')
})
