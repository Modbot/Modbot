import assert from 'node:assert/strict'
import { test } from 'node:test'
import { vrchatMedia } from '../src/lib/vrchatMedia.ts'

// VRChat's image hosts refuse a hotlinking browser, so anything on them goes through the server.
test('a picture on a VRChat host is fetched through the server', () => {
  const url = 'https://api.vrchat.cloud/api/1/image/file_abc/1/256'
  assert.equal(vrchatMedia(url), '/api/files/vrchat?url=' + encodeURIComponent(url))

  assert.equal(
    vrchatMedia('https://assets.vrchat.cloud/avatars/pic.png'),
    '/api/files/vrchat?url=' + encodeURIComponent('https://assets.vrchat.cloud/avatars/pic.png'),
  )
  assert.equal(
    vrchatMedia('https://files.vrchat.cloud/thumbnails/1.jpg?x=1&y=2'),
    '/api/files/vrchat?url=' + encodeURIComponent('https://files.vrchat.cloud/thumbnails/1.jpg?x=1&y=2'),
  )
  assert.equal(
    vrchatMedia('https://d348imysud55la.vrchat.cloud/banner.png'),
    '/api/files/vrchat?url=' + encodeURIComponent('https://d348imysud55la.vrchat.cloud/banner.png'),
  )
  assert.equal(
    vrchatMedia('HTTPS://API.VRCHAT.CLOUD/x'),
    '/api/files/vrchat?url=' + encodeURIComponent('HTTPS://API.VRCHAT.CLOUD/x'),
  )
})

test('any other picture is loaded as it is', () => {
  assert.equal(vrchatMedia('https://cdn.discordapp.com/avatars/1/2.png'), 'https://cdn.discordapp.com/avatars/1/2.png')
  assert.equal(vrchatMedia('https://vrchat.com/home'), 'https://vrchat.com/home')
  assert.equal(vrchatMedia('https://notvrchat.cloud/x.png'), 'https://notvrchat.cloud/x.png')
  assert.equal(vrchatMedia('https://vrchat.cloud.example.com/x.png'), 'https://vrchat.cloud.example.com/x.png')
  assert.equal(vrchatMedia('/icon-512.png'), '/icon-512.png')
  assert.equal(vrchatMedia('blob:https://modbot.local/abc'), 'blob:https://modbot.local/abc')
  assert.equal(vrchatMedia('not a url'), 'not a url')
})

test('nothing in gives nothing out', () => {
  assert.equal(vrchatMedia(null), null)
  assert.equal(vrchatMedia(undefined), null)
  assert.equal(vrchatMedia(''), null)
})
