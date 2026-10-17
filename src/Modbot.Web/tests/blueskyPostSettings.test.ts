import assert from 'node:assert/strict'
import { test } from 'node:test'
import {
  BLUESKY_LANGUAGES,
  interactLabel,
  languageName,
  languagesLabel,
  replyKind,
  withLanguageToggled,
  withReplyGroupToggled,
} from '../src/lib/blueskyPostSettings.ts'

test('every language has its own code and an English name', () => {
  const codes = BLUESKY_LANGUAGES.map((l) => l.code)
  assert.equal(new Set(codes).size, codes.length)
  assert.ok(BLUESKY_LANGUAGES.every((l) => /^[a-z]{2}$/.test(l.code) && l.name.length > 0))
  assert.equal(languageName('en'), 'English')
  assert.equal(languageName('xx'), 'xx')
})

test('languages are added up to three and taken out again', () => {
  const three = ['en', 'de', 'fr']
  assert.deepEqual(withLanguageToggled(['en'], 'de'), ['en', 'de'])
  assert.deepEqual(withLanguageToggled(three, 'es'), three)
  assert.deepEqual(withLanguageToggled(three, 'de'), ['en', 'fr'])
})

test('the language button shows the names, or Language with none', () => {
  assert.equal(languagesLabel([]), 'Language')
  assert.equal(languagesLabel(['en', 'ja']), 'English, Japanese')
})

test('ticking groups builds a mix in the box order, and the last one off is anyone again', () => {
  assert.deepEqual(withReplyGroupToggled(['anyone'], 'mentioned'), ['mentioned'])
  assert.deepEqual(withReplyGroupToggled(['mentioned'], 'followers'), ['followers', 'mentioned'])
  assert.deepEqual(withReplyGroupToggled(['nobody'], 'following'), ['following'])
  assert.deepEqual(withReplyGroupToggled(['followers', 'mentioned'], 'followers'), ['mentioned'])
  assert.deepEqual(withReplyGroupToggled(['mentioned'], 'mentioned'), ['anyone'])
})

test('the kind of a choice', () => {
  assert.equal(replyKind(['anyone']), 'anyone')
  assert.equal(replyKind(['nobody']), 'nobody')
  assert.equal(replyKind(['followers']), 'some')
})

test('the chip says what is picked', () => {
  assert.equal(interactLabel(['anyone'], false), 'Anyone can interact')
  assert.equal(interactLabel(['anyone'], true), 'Anyone can reply, no quote posts')
  assert.equal(interactLabel(['nobody'], false), 'Nobody can reply')
  assert.equal(interactLabel(['followers', 'mentioned'], true), 'Your followers and people you mention can reply, no quote posts')
  assert.equal(
    interactLabel(['followers', 'following', 'mentioned'], false),
    'Your followers, people you follow and people you mention can reply',
  )
})
