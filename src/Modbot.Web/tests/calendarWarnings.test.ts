import assert from 'node:assert/strict'
import { test } from 'node:test'
import { saveWarnings, startWords, type SaveWarningInput } from '../src/lib/calendarWarnings.ts'

const fine: SaveWarningInput = {
  title: 'Movie night',
  publishToVRChat: true,
  languages: ['eng'],
  tags: ['movies'],
}

test('nothing is wrong, nothing is said', () => {
  assert.deepEqual(saveWarnings(fine, [], 'Europe/London'), [])
})

test('empty tags and an empty language are each warned about while the event goes to VRChat', () => {
  assert.deepEqual(saveWarnings({ ...fine, tags: [], languages: [] }, [], 'UTC'), [
    'No VRChat tags.',
    'No VRChat language.',
  ])
})

test('tags and language are not warned about when the event stays off VRChat', () => {
  assert.deepEqual(saveWarnings({ ...fine, publishToVRChat: false, tags: [], languages: [] }, [], 'UTC'), [])
})

test('a title that is not the template\'s is warned about, ignoring spaces at the ends', () => {
  assert.deepEqual(saveWarnings({ ...fine, title: ' Movie night ', templateTitle: 'Movie night' }, [], 'UTC'), [])

  assert.deepEqual(saveWarnings({ ...fine, title: 'Movie night 2', templateTitle: 'Movie night' }, [], 'UTC'), [
    'The title differs from the template\'s: "Movie night".',
  ])
})

test('an event made without a template has no template title to differ from', () => {
  assert.deepEqual(saveWarnings({ ...fine, title: 'Anything' }, [], 'UTC'), [])
})

test('an event at the same time is named with its start, and where it is when that is VRChat', () => {
  const lines = saveWarnings(
    fine,
    [
      { id: 'a', title: 'Movie night', startsAt: '2026-11-14T20:00:00+00:00', onVRChat: false },
      { id: 'b', title: 'Movie Night!', startsAt: '2026-11-14T20:10:00+00:00', onVRChat: true },
    ],
    'UTC',
  )

  assert.deepEqual(lines, [
    '"Movie night" already starts Sat 14 Nov, 20:00.',
    '"Movie Night!" already starts Sat 14 Nov, 20:10 on VRChat.',
  ])
})

test('a start is read in the event\'s own zone', () => {
  assert.equal(startWords('2026-11-14T20:00:00+00:00', 'Asia/Tokyo'), 'Sun 15 Nov, 05:00')
})

test('a start that cannot be read is shown as it came', () => {
  assert.equal(startWords('soon', 'UTC'), 'soon')
})
