import assert from 'node:assert/strict'
import { test } from 'node:test'
import {
  barRows,
  breakAtBands,
  carriedKey,
  daysBetween,
  lineRows,
  missingRuns,
  timeBands,
} from '../src/components/charts/coverage.ts'
import { memberCountRows } from '../src/pages/analytics/memberCountSeries.ts'
import { activityChartRows } from '../src/pages/analytics/instanceActivitySeries.ts'

const DAY = 86_400_000
const ms = (iso: string) => Date.parse(iso)

test('days run from the first to the last, both included, across a month end', () => {
  assert.deepEqual(daysBetween('2026-06-29', '2026-07-02'), ['2026-06-29', '2026-06-30', '2026-07-01', '2026-07-02'])
  assert.deepEqual(daysBetween('2026-06-29', '2026-06-29'), ['2026-06-29'])
})

test('missing days come in runs, one band per run', () => {
  const days = daysBetween('2026-06-01', '2026-06-07')
  const missing = new Set(['2026-06-01', '2026-06-02', '2026-06-05'])
  assert.deepEqual(missingRuns(days, missing), [
    { first: 0, last: 1 },
    { first: 4, last: 4 },
  ])
})

test('a column day is missing, zero or today, and a recorded value is never hidden behind a band', () => {
  const { rows, runs } = barRows(
    '2026-06-01',
    '2026-06-05',
    [
      { key: 'joined', points: [{ day: '2026-06-02', value: 3 }, { day: '2026-06-05', value: 1 }] },
      { key: 'left', points: [] },
    ],
    // Jun 2 is listed as missing but has a join on record: the join wins.
    { missing: ['2026-06-01', '2026-06-02'], today: '2026-06-05' },
  )

  assert.deepEqual(
    rows.map((r) => [r.day, r.mark, r.joined, r.left]),
    [
      ['2026-06-01', 'missing', 0, 0],
      ['2026-06-02', undefined, 3, 0],
      ['2026-06-03', undefined, 0, 0],
      ['2026-06-04', undefined, 0, 0],
      ['2026-06-05', 'today', 1, 0],
    ],
  )
  assert.deepEqual(runs, [{ first: 0, last: 0 }])
})

test('without marks, a column chart draws every day as it always did', () => {
  const { rows, runs } = barRows('2026-06-01', '2026-06-02', [{ key: 'a', points: [{ day: '2026-06-02', value: 2 }] }])
  assert.deepEqual(rows.map((r) => [r.mark, r.a]), [[undefined, 0], [undefined, 2]])
  assert.deepEqual(runs, [])
})

test('a counted line breaks on a missing day instead of dipping to nought, and fills a quiet day with nought', () => {
  const { rows, runs } = lineRows(
    '2026-06-01',
    '2026-06-04',
    [{ key: 'v', points: [{ day: '2026-06-01', value: 5 }, { day: '2026-06-04', value: 2 }] }],
    'zero',
    { missing: ['2026-06-02'] },
  )

  assert.deepEqual(rows.map((r) => [r.i, r.v, r.mark]), [
    [0, 5, undefined],
    [1, null, 'missing'],
    [2, 0, undefined],
    [3, 2, undefined],
  ])
  assert.deepEqual(runs, [{ first: 1, last: 1 }])
})

test('a level carried across days with no reading goes under the carried key, joined to the readings either side', () => {
  const { rows } = lineRows(
    '2026-06-01',
    '2026-06-06',
    [{ key: 'm', points: [{ day: '2026-06-02', value: 100 }, { day: '2026-06-05', value: 104 }] }],
    'carry',
    { missing: ['2026-06-01'], today: '2026-06-06' },
  )

  const c = carriedKey('m')
  assert.deepEqual(rows.map((r) => [r.day, r.m, r[c], r.mark]), [
    // Before the first reading: nothing, and the day is listed missing.
    ['2026-06-01', null, null, 'missing'],
    // The reading that starts the carried stretch is on both keys, so the dashes join it.
    ['2026-06-02', 100, 100, undefined],
    ['2026-06-03', null, 100, undefined],
    ['2026-06-04', null, 100, undefined],
    ['2026-06-05', 104, 104, undefined],
    // Today has no row yet: carried, and marked as not over.
    ['2026-06-06', null, 104, 'today'],
  ])
})

test('a carried day with a row on record is not missing even when the list says so', () => {
  const { rows } = lineRows('2026-06-01', '2026-06-02', [{ key: 'm', points: [{ day: '2026-06-01', value: 0 }] }], 'carry', {
    missing: ['2026-06-01'],
  })
  assert.equal(rows[0].mark, undefined)
  assert.equal(rows[0].m, 0)
})

test('bands on a time axis merge consecutive days and are cut to the window', () => {
  const from = ms('2026-06-01T12:00:00Z')
  const to = ms('2026-06-05T08:00:00Z')

  assert.deepEqual(timeBands(['2026-06-02', '2026-06-01', '2026-06-05', '2026-06-02'], from, to), [
    { x1: from, x2: ms('2026-06-03T00:00:00Z') },
    { x1: ms('2026-06-05T00:00:00Z'), x2: to },
  ])

  // A day wholly outside the window draws nothing.
  assert.deepEqual(timeBands(['2026-05-20'], from, to), [])
})

test('a line of readings breaks at each band and drops anything inside one', () => {
  const bands = [{ x1: 10, x2: 20 }]
  const rows = [
    { at: 5, v: 1 as number | null },
    { at: 15, v: 2 as number | null },
    { at: 25, v: 3 as number | null },
  ]

  assert.deepEqual(breakAtBands(rows, bands, { v: null }), [
    { at: 5, v: 1 },
    { at: 10, v: null },
    { at: 25, v: 3 },
  ])
})

test('member count: facts go dashed, readings solid, the first reading closes the dashes, and a day with neither breaks the line', () => {
  const chart = memberCountRows({
    from: '2026-06-01T00:00:00Z',
    to: '2026-06-06T00:00:00Z',
    points: [
      { at: '2026-06-01T10:00:00Z', members: 100, online: 5, carried: true },
      { at: '2026-06-02T10:00:00Z', members: 102, online: 6, carried: true },
      { at: '2026-06-03T10:00:00Z', members: 103, online: 7, carried: false },
      { at: '2026-06-05T10:00:00Z', members: 105, online: 9, carried: false },
    ],
    daysWithoutReadings: ['2026-06-04'],
  })

  assert.equal(chart.readings, 2)
  assert.deepEqual(chart.bands, [{ x1: ms('2026-06-04T00:00:00Z'), x2: ms('2026-06-04T00:00:00Z') + DAY }])
  assert.deepEqual(chart.rows, [
    { at: ms('2026-06-01T10:00:00Z'), members: null, online: null, membersCarried: 100, onlineCarried: 5 },
    { at: ms('2026-06-02T10:00:00Z'), members: null, online: null, membersCarried: 102, onlineCarried: 6 },
    { at: ms('2026-06-03T10:00:00Z'), members: 103, online: 7, membersCarried: 103, onlineCarried: 7 },
    { at: ms('2026-06-04T00:00:00Z'), members: null, online: null, membersCarried: null, onlineCarried: null },
    { at: ms('2026-06-05T10:00:00Z'), members: 105, online: 9, membersCarried: null, onlineCarried: null },
  ])
})

test('instance activity breaks across a day an instance was open and never counted', () => {
  const chart = activityChartRows({
    range: 'week',
    from: '2026-06-01T00:00:00Z',
    to: '2026-06-03T12:00:00Z',
    stepSeconds: 1210,
    generatedAt: '2026-06-03T12:00:00Z',
    points: [
      { at: '2026-06-01T20:00:00Z', people: 12, instances: 2 },
      { at: '2026-06-03T09:00:00Z', people: 4, instances: 1 },
    ],
    daysWithoutHeadCounts: ['2026-06-02'],
  })

  assert.deepEqual(
    chart.rows.map((r) => [new Date(r.at).toISOString(), r.people]),
    [
      ['2026-06-01T20:00:00.000Z', 12],
      ['2026-06-02T00:00:00.000Z', null],
      ['2026-06-03T09:00:00.000Z', 4],
      // The last count carried to the end of the window, as before.
      ['2026-06-03T12:00:00.000Z', 4],
    ],
  )
})
