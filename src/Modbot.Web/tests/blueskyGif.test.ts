import assert from 'node:assert/strict'
import { test } from 'node:test'
import { gifFromKlipy, gifWithAlt, hasGif, NO_GIF } from '../src/lib/blueskyGif.ts'

test('a GIF is an address; the empty choice is none', () => {
  assert.equal(hasGif(null), false)
  assert.equal(hasGif(undefined), false)
  assert.equal(hasGif(NO_GIF), false)
  assert.equal(hasGif({ url: 'https://static.klipy.com/a.gif', alt: '' }), true)
})

test('a picked GIF keeps its address and takes its title as the alt text', () => {
  assert.deepEqual(gifFromKlipy({ url: 'https://static.klipy.com/a.gif?hh=1&ww=2', title: 'Cat' }), {
    url: 'https://static.klipy.com/a.gif?hh=1&ww=2',
    alt: 'Cat',
  })
})

test('the alt text changes and nothing else does', () => {
  assert.deepEqual(gifWithAlt({ url: 'https://static.klipy.com/a.gif', alt: 'Cat' }, 'A cat'), {
    url: 'https://static.klipy.com/a.gif',
    alt: 'A cat',
  })
})
