import assert from 'node:assert/strict'
import { test } from 'node:test'
import { iconLayout, platformsFromWorld, platformsKey, removedAfterPick, usesWorldPicture, worldPictureLinks, worldPictureRemoved } from '../src/lib/eventPlatforms.ts'

const OFFERED = ['standalonewindows', 'android', 'ios']

test('a world with PC and Android builds ticks those two, in the form order', () => {
  assert.deepEqual(platformsFromWorld(['android', 'standalonewindows'], OFFERED), ['standalonewindows', 'android'])
})

test('a world with all three builds ticks all three', () => {
  assert.deepEqual(platformsFromWorld(['standalonewindows', 'android', 'ios'], OFFERED), OFFERED)
})

test('a world whose builds are not known leaves the boxes as they are', () => {
  assert.equal(platformsFromWorld(null, OFFERED), null)
  assert.equal(platformsFromWorld(undefined, OFFERED), null)
  assert.equal(platformsFromWorld([], OFFERED), null)
})

test('builds the form does not offer tick nothing', () => {
  assert.equal(platformsFromWorld(['standalonelinux'], OFFERED), null)
  assert.deepEqual(platformsFromWorld(['standalonelinux', 'ios'], OFFERED), ['ios'])
})

test('the same ticks make the same key in any order', () => {
  assert.equal(platformsKey(['ios', 'android']), platformsKey(['android', 'ios']))
  assert.notEqual(platformsKey(['ios']), platformsKey(['ios', 'android']))
  assert.equal(platformsKey([]), '')
})

const ready = {
  vrChatImageId: null,
  cropWaiting: false,
  pictureLink: null,
  worldPictureUrl: 'https://api.vrchat.cloud/api/1/image/file_1/1/256',
  worldId: 'wrld_1',
  removedFor: null,
  uploads: true,
  plus: true,
  publishToVRChat: true,
  draft: false,
  template: false,
}

test('no picture and a world with one uses the world picture', () => {
  assert.equal(usesWorldPicture(ready), true)
})

test('VRChat+ not known yet still uses it; no VRChat+ does not', () => {
  assert.equal(usesWorldPicture({ ...ready, plus: null }), true)
  assert.equal(usesWorldPicture({ ...ready, plus: false }), false)
})

test('a picture the event has is never replaced', () => {
  assert.equal(usesWorldPicture({ ...ready, vrChatImageId: 'file_9' }), false)
})

test('a crop waiting, or a picture link, is a picture chosen', () => {
  assert.equal(usesWorldPicture({ ...ready, cropWaiting: true }), false)
  assert.equal(usesWorldPicture({ ...ready, pictureLink: 'https://example.com/a.png' }), false)
})

test('no world picture, uploads off, not on VRChat, a draft or a template uses nothing', () => {
  assert.equal(usesWorldPicture({ ...ready, worldPictureUrl: null }), false)
  assert.equal(usesWorldPicture({ ...ready, worldPictureUrl: undefined }), false)
  assert.equal(usesWorldPicture({ ...ready, uploads: false }), false)
  assert.equal(usesWorldPicture({ ...ready, publishToVRChat: false }), false)
  assert.equal(usesWorldPicture({ ...ready, draft: true }), false)
  assert.equal(usesWorldPicture({ ...ready, template: true }), false)
})

test('Remove on the picture stops the world picture for that world only', () => {
  assert.equal(usesWorldPicture({ ...ready, removedFor: 'wrld_1' }), false)
  assert.equal(usesWorldPicture({ ...ready, removedFor: 'wrld_1', worldId: 'wrld_2' }), true)
  assert.equal(usesWorldPicture({ ...ready, removedFor: null }), true)
  assert.equal(worldPictureRemoved('wrld_1', 'wrld_1'), true)
  assert.equal(worldPictureRemoved('wrld_1', 'wrld_2'), false)
  assert.equal(worldPictureRemoved(null, null), false)
  // Remove on A, pick B, pick A again: the flag was cleared at B, so A's picture is back on.
  assert.equal(removedAfterPick('wrld_1', 'wrld_1'), 'wrld_1')
  assert.equal(removedAfterPick('wrld_1', 'wrld_2'), null)
  assert.equal(removedAfterPick(removedAfterPick('wrld_1', 'wrld_2'), 'wrld_1'), null)
})

test('the world picture is the full-size one, with the small one to fall back on', () => {
  assert.deepEqual(worldPictureLinks({ imageUrl: 'https://a/full', thumbnailUrl: 'https://a/small' }), {
    link: 'https://a/full',
    fallback: 'https://a/small',
  })
  assert.deepEqual(worldPictureLinks({ imageUrl: 'https://a/full', thumbnailUrl: null }), { link: 'https://a/full', fallback: null })
  assert.deepEqual(worldPictureLinks({ imageUrl: 'https://a/same', thumbnailUrl: 'https://a/same' }), {
    link: 'https://a/same',
    fallback: null,
  })
})

test('a world with only a small picture uses that; with none, there is no world picture', () => {
  assert.deepEqual(worldPictureLinks({ imageUrl: null, thumbnailUrl: 'https://a/small' }), { link: 'https://a/small', fallback: null })
  assert.equal(worldPictureLinks({ imageUrl: null, thumbnailUrl: null }), null)
  assert.equal(worldPictureLinks(undefined), null)
})

test('the icons sit in the top right corner with the last one at the margin', () => {
  const one = iconLayout(1000, 1)
  assert.equal(one.diameter, 70)
  assert.equal(one.margin, 20)
  assert.equal(one.y, 20)
  assert.equal(one.x + one.diameter, 1000 - one.margin)

  const three = iconLayout(1000, 3)
  assert.equal(three.x + 3 * three.diameter + 2 * three.gap, 1000 - three.margin)
})

test('the icons never get smaller than 24 pixels', () => {
  assert.equal(iconLayout(200, 1).diameter, 24)
})
