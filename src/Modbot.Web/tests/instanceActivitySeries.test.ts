import assert from 'node:assert/strict'
import { test } from 'node:test'
import type { InstanceActivitySeries } from '../src/lib/api.ts'
import { toActivityRows } from '../src/pages/analytics/instanceActivitySeries.ts'

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
