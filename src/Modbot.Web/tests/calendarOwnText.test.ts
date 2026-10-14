import assert from 'node:assert/strict'
import { test } from 'node:test'
import { around, countOf, OWN_KEYS, OWN_TEXT, ownKeysWithText, postAround, textShown, type OwnKey } from '../src/lib/calendarOwnText.ts'

/**
 * A service's own words, edited in place on its card (calendar design §2.1, step 2b): which fields a
 * card edits, what its boxes hold, the counters, and the lines the server draws around the words.
 */

const event = {
  title: 'Movie night',
  description: 'Bring snacks',
  vrChatTitle: '',
  vrChatDescription: '',
  discordTitle: '',
  discordDescription: '',
  googleTitle: '',
  googleDescription: '',
  blueskyTitle: '',
  blueskyDescription: '',
}

const none: ReadonlySet<OwnKey> = new Set()

test('the Discord event and the Discord post edit the same words', () => {
  assert.deepEqual(OWN_TEXT.discordEvent, OWN_TEXT.channelPost)
  assert.equal(OWN_KEYS.length, 8)
})

test('a box with no words of its own shows the event\'s', () => {
  assert.equal(textShown(event, 'vrChatTitle', none), 'Movie night')
  assert.equal(textShown(event, 'blueskyDescription', none), 'Bring snacks')
})

test('a box with words of its own shows them, and follows nothing', () => {
  const own = { ...event, googleTitle: 'Movie night (Google)' }
  assert.equal(textShown(own, 'googleTitle', none), 'Movie night (Google)')
  assert.equal(textShown(own, 'googleDescription', none), 'Bring snacks')
})

test('a box emptied to be retyped stays empty', () => {
  assert.equal(textShown(event, 'vrChatTitle', new Set<OwnKey>(['vrChatTitle'])), '')
})

test('the form starts with the own fields that hold words', () => {
  const started = ownKeysWithText({ ...event, discordTitle: 'Hello', blueskyDescription: '  ' })
  assert.deepEqual([...started], ['discordTitle'])
})

test('a counter is in the service\'s own limit, and red only for the service\'s own words', () => {
  assert.deepEqual(countOf('vrChatTitle', 'x'.repeat(101), true), { label: '101 / 100', over: true })
  assert.deepEqual(countOf('vrChatTitle', 'x'.repeat(101), false), { label: '101 / 100', over: false })
  assert.deepEqual(countOf('vrChatDescription', 'x'.repeat(1000), true), { label: '1000 / 1000', over: false })
  assert.deepEqual(countOf('blueskyDescription', 'x'.repeat(301), true), { label: '301 / 300', over: true })
})

test('Bluesky counts what a person sees as one character', () => {
  assert.deepEqual(countOf('blueskyDescription', '👨‍👩‍👧'.repeat(300), true), { label: '300 / 300', over: false })
})

test('the lines drawn around the words are the stretches before and after them', () => {
  assert.deepEqual(around('Category: Social\n\nBring snacks', 'Bring snacks'), { before: 'Category: Social', after: '' })
  assert.deepEqual(around('Bring snacks\n\nWorld: Home', 'Bring snacks'), { before: '', after: 'World: Home' })
})

test('nothing is drawn around the words when there is nothing extra, or they are not found whole', () => {
  assert.equal(around('Bring snacks', 'Bring snacks'), null)
  assert.equal(around('Bring snacks and more', 'Bring tea'), null)
  assert.equal(around(null, 'Bring snacks'), null)
  assert.equal(around('Bring snacks', ''), null)
})

test('a Bluesky post: the title is a box of its own, the time before and the link after the description', () => {
  const text = 'Movie night\nFri 8 PM\nHome\nBring snacks\nhttps://example.com/e'
  assert.deepEqual(postAround(text, 'Movie night', 'Bring snacks'), { before: 'Fri 8 PM\nHome', after: 'https://example.com/e' })
})

test('a Bluesky post with no description: everything after the title is before', () => {
  assert.deepEqual(postAround('Movie night\nFri 8 PM\nhttps://example.com/e', 'Movie night', ''), {
    before: 'Fri 8 PM\nhttps://example.com/e',
    after: '',
  })
})

test('a Bluesky post whose description was cut draws nothing around it', () => {
  assert.deepEqual(postAround('Movie night\nFri 8 PM\nBring sna…', 'Movie night', 'Bring snacks and a very long list'), {
    before: '',
    after: '',
  })
})
