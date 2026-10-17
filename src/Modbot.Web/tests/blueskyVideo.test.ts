import assert from 'node:assert/strict'
import { test } from 'node:test'
import {
  BLUESKY_VIDEO_MAX_BYTES,
  hasVideo,
  NO_VIDEO,
  videoFileProblem,
  videoFromFile,
  videoFromLink,
  videoHost,
  videoLinkProblem,
  videoWithAlt,
} from '../src/lib/blueskyVideo.ts'

test('a video is a kept file or a link; the empty choice is none', () => {
  assert.equal(hasVideo(null), false)
  assert.equal(hasVideo(undefined), false)
  assert.equal(hasVideo(NO_VIDEO), false)
  assert.equal(hasVideo(videoFromFile('a')), true)
  assert.equal(hasVideo(videoFromLink('https://example.com/a.mp4')), true)
})

test('a file video has no link and a link video has no file', () => {
  assert.deepEqual(videoFromFile('a'), { videoId: 'a', url: null, alt: '' })
  assert.deepEqual(videoFromLink('  https://example.com/a.mp4 '), { videoId: null, url: 'https://example.com/a.mp4', alt: '' })
})

test('the alt text changes and nothing else does', () => {
  assert.deepEqual(videoWithAlt(videoFromFile('a'), 'a cat'), { videoId: 'a', url: null, alt: 'a cat' })
})

test('a file over 50 MB or of another type is refused', () => {
  assert.equal(videoFileProblem({ size: 1000, type: 'video/mp4' }), null)
  assert.equal(videoFileProblem({ size: 1000, type: 'video/quicktime' }), null)
  assert.equal(videoFileProblem({ size: BLUESKY_VIDEO_MAX_BYTES, type: 'video/webm' }), null)
  assert.equal(videoFileProblem({ size: BLUESKY_VIDEO_MAX_BYTES + 1, type: 'video/mp4' }), 'The video is larger than 50 MB.')
  assert.equal(videoFileProblem({ size: 1000, type: '' }), null)
  assert.equal(videoFileProblem({ size: 1000, type: 'image/png' }), 'The video must be an MP4, MPEG, WebM or QuickTime file.')
})

test('a link must be https with no user name or password', () => {
  assert.equal(videoLinkProblem('https://example.com/a.mp4'), null)
  assert.equal(videoLinkProblem('  https://example.com/a.mp4  '), null)
  assert.equal(videoLinkProblem('http://example.com/a.mp4'), 'The video link must start with https://.')
  assert.equal(videoLinkProblem('example.com/a.mp4'), 'The video link must start with https://.')
  assert.equal(videoLinkProblem('https://me:pw@example.com/a.mp4'), 'The video link must start with https://.')
  assert.equal(videoLinkProblem(''), 'The video link must start with https://.')
})

test('a link shows as its host', () => {
  assert.equal(videoHost('https://cdn.example.com/a/b.mp4'), 'cdn.example.com')
  assert.equal(videoHost('not a link'), 'not a link')
})
