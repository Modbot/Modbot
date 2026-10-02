import assert from 'node:assert/strict'
import { test } from 'node:test'
import {
  centredCrop,
  CROP_MAX_WIDTH,
  CROP_SMALLEST,
  croppedSize,
  cropSize,
  DISCORD_COVER_ASPECT,
  moveCrop,
  pictureFollowingLink,
  sizeCrop,
  VRCHAT_FILE_EXAMPLE,
  VRCHAT_FILE_NOT_FOUND,
  VRCHAT_PICTURE_ASPECT,
  vrchatFileIdIn,
  vrchatFileIdInLink,
} from '../src/lib/eventPicture.ts'

/**
 * An event's pictures in the form (calendar design §15.1–§15.3): the file id inside whatever was
 * pasted, the VRChat picture following a picture link from VRChat, and the crop box's arithmetic.
 */

const ID = 'file_6f1c2a3b-4d5e-4f60-8a71-92b3c4d5e6f7'
const LINK = `https://api.vrchat.cloud/api/1/file/${ID}/1/file`

test('the file id is taken out of any VRChat link or paste', () => {
  for (const pasted of [
    ID,
    `  ${ID}  `,
    LINK,
    `${LINK}?width=512&height=288`,
    `https://api.vrchat.cloud/api/1/file/${ID}/1`,
    `https://api.vrchat.cloud/api/1/image/${ID}/1/1024`,
    `https://files.vrchat.cloud/thumbnails/${ID}.png`,
    `https://vrchat.com/home/gallery/${ID}`,
    `${ID}_blob`,
    `${ID}/1/file`,
    `api/1/file/${ID}/1/file_blob`,
  ]) {
    assert.equal(vrchatFileIdIn(pasted), ID, pasted)
  }
})

test('an odd id is kept, never judged by its shape', () => {
  assert.equal(vrchatFileIdIn('file_legacy123'), 'file_legacy123')
  assert.equal(vrchatFileIdIn('https://api.vrchat.cloud/api/1/file/file_legacy123/4/file'), 'file_legacy123')
  assert.equal(vrchatFileIdIn('file_odd-but_real.png'), 'file_odd-but_real')
})

test('with no VRChat file id there is none', () => {
  for (const pasted of [
    null,
    undefined,
    '',
    '   ',
    'hello',
    'file_',
    '6f1c2a3b-4d5e-4f60-8a71-92b3c4d5e6f7',
    'https://api.vrchat.cloud/api/1/worlds/wrld_1',
    `https://pictures.example/${ID}.png`,
    `https://notvrchat.cloud/${ID}`,
  ]) {
    assert.equal(vrchatFileIdIn(pasted), null, String(pasted))
  }
})

test('the refusal shows an example that is itself an id', () => {
  assert.ok(VRCHAT_FILE_NOT_FOUND.includes(VRCHAT_FILE_EXAMPLE))
  assert.equal(vrchatFileIdIn(VRCHAT_FILE_EXAMPLE), VRCHAT_FILE_EXAMPLE)
})

test('only a link on VRChat has an id as a picture link', () => {
  assert.equal(vrchatFileIdInLink(LINK), ID)
  assert.equal(vrchatFileIdInLink(ID), null)
  assert.equal(vrchatFileIdInLink(`https://pictures.example/${ID}.png`), null)
  assert.equal(vrchatFileIdInLink(null), null)
})

test('a picture link from VRChat fills an empty VRChat picture', () => {
  assert.equal(pictureFollowingLink(null, LINK, null), ID)
})

test('a picture link elsewhere leaves the VRChat picture as it is', () => {
  assert.equal(pictureFollowingLink(null, 'https://pictures.example/a.png', null), null)
  assert.equal(pictureFollowingLink(null, 'https://pictures.example/a.png', 'file_mine'), 'file_mine')
})

test('a VRChat picture from the link follows the link, and empties when the link is not VRChat’s', () => {
  const other = 'file_0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d'
  const next = `https://api.vrchat.cloud/api/1/file/${other}/1/file`

  assert.equal(pictureFollowingLink(LINK, next, ID), other)
  assert.equal(pictureFollowingLink(LINK, 'https://pictures.example/a.png', ID), null)
  assert.equal(pictureFollowingLink(LINK, null, ID), null)
})

test('a VRChat picture chosen another way stays when the link changes', () => {
  assert.equal(pictureFollowingLink(LINK, null, 'file_uploaded'), 'file_uploaded')
  assert.equal(pictureFollowingLink(null, LINK, 'file_uploaded'), 'file_uploaded')
})

test('VRChat’s pictures are 16:9 and Discord’s covers 2.5:1', () => {
  assert.equal(VRCHAT_PICTURE_ASPECT, 16 / 9)
  assert.equal(DISCORD_COVER_ASPECT, 2.5)
})

test('the first crop is the largest box of the shape, in the middle', () => {
  // A tall picture: the full width, centred up and down.
  assert.deepEqual(centredCrop(1600, 1800, 16 / 9), { x: 0, y: 450, width: 1600, height: 900 })
  // A wide one: the full height, centred side to side.
  assert.deepEqual(centredCrop(4000, 900, 16 / 9), { x: 1200, y: 0, width: 1600, height: 900 })
})

test('a box moves inside the picture and no further', () => {
  const box = { x: 0, y: 450, width: 1600, height: 900 }
  assert.deepEqual(moveCrop(box, 0, -100, 1600, 1800), { ...box, y: 350 })
  assert.deepEqual(moveCrop(box, 0, -9999, 1600, 1800), { ...box, y: 0 })
  assert.deepEqual(moveCrop(box, 0, 9999, 1600, 1800), { ...box, y: 900 })
  assert.deepEqual(moveCrop(box, -50, 0, 1600, 1800), box)
})

test('a box sizes around its middle, keeps its shape, and stays inside', () => {
  const largest = centredCrop(1600, 1800, 16 / 9)
  const half = sizeCrop(largest, 0.5, 1600, 1800, 16 / 9)

  assert.equal(half.width, 800)
  assert.equal(half.height, 450)
  assert.equal(half.x + half.width / 2, 800)
  assert.equal(half.y + half.height / 2, 900)
  assert.equal(cropSize(half, 1600, 1800, 16 / 9), 0.5)

  // Never smaller than the smallest, never larger than the picture.
  assert.equal(sizeCrop(largest, 0, 1600, 1800, 16 / 9).width, 1600 * CROP_SMALLEST)
  assert.deepEqual(sizeCrop(half, 5, 1600, 1800, 16 / 9).width, 1600)

  // A small box in a corner grows back inside the picture.
  const corner = { x: 0, y: 0, width: 320, height: 180 }
  const grown = sizeCrop(corner, 1, 1600, 1800, 16 / 9)
  assert.equal(grown.x, 0)
  assert.equal(grown.y, 0)
})

test('a crop is never enlarged, and never wider than the widest picture made', () => {
  assert.deepEqual(croppedSize({ x: 0, y: 0, width: 800, height: 450 }, 16 / 9), { width: 800, height: 450 })
  assert.deepEqual(croppedSize({ x: 0, y: 0, width: 4000, height: 2250 }, 16 / 9), {
    width: CROP_MAX_WIDTH,
    height: 1152,
  })
})
