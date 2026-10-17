import assert from 'node:assert/strict'
import { test } from 'node:test'
import { shownPictures, withPictureChanged, withPictures, withoutPicture } from '../src/lib/blueskyPictures.ts'

const pic = (id: string, alt = '') => ({ pictureId: id, alt })

test('no list shows the cover alone, or nothing without a cover', () => {
  assert.deepEqual(shownPictures(null, 'cover'), [pic('cover')])
  assert.deepEqual(shownPictures(undefined, null), [])
})

test('a saved list wins over the cover, even an empty one', () => {
  assert.deepEqual(shownPictures([pic('a', 'x')], 'cover'), [pic('a', 'x')])
  assert.deepEqual(shownPictures([], 'cover'), [])
})

test('adding stops at four pictures', () => {
  const four = withPictures([pic('a'), pic('b')], [pic('c'), pic('d'), pic('e')])
  assert.deepEqual(four.map((p) => p.pictureId), ['a', 'b', 'c', 'd'])
})

test('changing one picture keeps the others and the order', () => {
  const list = [pic('a'), pic('b', 'old')]
  assert.deepEqual(withPictureChanged(list, 1, { alt: 'new' }), [pic('a'), pic('b', 'new')])
  assert.deepEqual(withPictureChanged(list, 5, { alt: 'new' }), list)
})

test('removing one picture keeps the order of the rest', () => {
  assert.deepEqual(withoutPicture([pic('a'), pic('b'), pic('c')], 0).map((p) => p.pictureId), ['b', 'c'])
})
