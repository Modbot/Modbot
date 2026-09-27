import assert from 'node:assert/strict'
import { test } from 'node:test'
import type { InstanceActivitySeries } from '../src/lib/api.ts'
import { highest, toActivityRows, wholeTicks } from '../src/pages/analytics/instanceActivitySeries.ts'

const series = (points: InstanceActivitySeries['points'], to: string): InstanceActivitySeries => ({
  range: 'day',
  from: '2026-09-19T00:00:00Z',
  to,
  stepSeconds: 173,
  points,
  generatedAt: to,
})

test('rows carry the moment as a number, in time order, and drop anything unreadable', () => {
  const rows = toActivityRows(
    series(
      [
        { at: '2026-09-19T12:00:00Z', people: 4, instances: 1 },
        { at: 'not a time', people: 99, instances: 9 },
        { at: '2026-09-19T10:00:00Z', people: 2, instances: 1 },
      ],
      '2026-09-19T10:00:00Z',
    ),
  )

  assert.deepEqual(
    rows.map((r) => r.people),
    [2, 4],
  )
  assert.equal(rows[0].at, Date.parse('2026-09-19T10:00:00Z'))
})

test('the last value is carried to the end of the range, so a quiet evening still draws', () => {
  const rows = toActivityRows(
    series([{ at: '2026-09-19T12:00:00Z', people: 7, instances: 2 }], '2026-09-19T18:00:00Z'),
  )

  assert.equal(rows.length, 2)
  assert.deepEqual(rows[1], { at: Date.parse('2026-09-19T18:00:00Z'), people: 7, instances: 2 })
})

test('a reading at the very end of the range is not repeated', () => {
  const rows = toActivityRows(
    series([{ at: '2026-09-19T18:00:00Z', people: 7, instances: 2 }], '2026-09-19T18:00:00Z'),
  )

  assert.equal(rows.length, 1)
})

test('nothing recorded draws nothing, rather than a line at zero', () => {
  assert.deepEqual(toActivityRows(series([], '2026-09-19T18:00:00Z')), [])
})

test('a scale counts whole things and ends on the tick just above the highest value', () => {
  // One instance at most is drawn on 0 to 1, not stretched onto 0 to 4.
  assert.deepEqual(wholeTicks(1), [0, 1])
  assert.deepEqual(wholeTicks(4), [0, 1, 2, 3, 4])
  // Six people: no half people, and never more than five ticks.
  assert.deepEqual(wholeTicks(6), [0, 2, 4, 6])
  assert.deepEqual(wholeTicks(7), [0, 2, 4, 6, 8])
  assert.deepEqual(wholeTicks(48), [0, 20, 40, 60])
  assert.deepEqual(wholeTicks(230), [0, 100, 200, 300])
})

test('a scale with nothing on it still runs from 0 to 1', () => {
  assert.deepEqual(wholeTicks(0), [0, 1])
  assert.deepEqual(wholeTicks(Number.NaN), [0, 1])
})

test('every scale is whole numbers, at most five ticks, and reaches its value', () => {
  for (let max = 1; max <= 2000; max++) {
    const ticks = wholeTicks(max)
    assert.ok(ticks.length <= 5, `${max}: ${ticks.join(',')}`)
    assert.ok(ticks.every(Number.isInteger), `${max}: ${ticks.join(',')}`)
    assert.ok(ticks[ticks.length - 1] >= max, `${max}: ${ticks.join(',')}`)
    assert.equal(ticks[0], 0)
  }
})

test('the highest value skips the breaks in the line', () => {
  const rows = [
    { at: 1, people: 4, instances: 1 },
    { at: 2, people: null, instances: null },
    { at: 3, people: 6, instances: 2 },
  ]
  assert.equal(highest(rows, 'people'), 6)
  assert.equal(highest(rows, 'instances'), 2)
  assert.equal(highest([], 'people'), 0)
})
