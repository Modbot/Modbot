import assert from 'node:assert/strict'
import { test } from 'node:test'
import {
  latest,
  memoryValue,
  percent,
  perSecond,
  processorTop,
  readable,
  toRows,
} from '../src/components/settings/machineUsage.ts'
import type { MachineUsagePoint } from '../src/lib/api.ts'

const at = (minute: number) => `2026-09-18T14:${String(minute).padStart(2, '0')}:00Z`

const point = (minute: number, over: Partial<MachineUsagePoint> = {}): MachineUsagePoint => ({
  at: at(minute),
  processorPercent: 4,
  memoryBytes: 300 * 1024 * 1024,
  diskReadBytesPerSecond: 0,
  diskWrittenBytesPerSecond: 2048,
  ...over,
})

test('rows carry the reading time as a number, in time order, and drop anything unreadable', () => {
  const rows = toRows([point(3), { ...point(1), at: 'not a time' }, point(2)])

  assert.deepEqual(
    rows.map((r) => r.at),
    [Date.parse(at(2)), Date.parse(at(3))],
  )
})

test('a figure no reading carries is not readable, and one some readings carry is', () => {
  const rows = toRows([
    point(1, { diskReadBytesPerSecond: null, diskWrittenBytesPerSecond: null }),
    point(2, { diskReadBytesPerSecond: null, diskWrittenBytesPerSecond: 1024 }),
  ])

  assert.equal(readable(rows, 'diskRead'), false)
  assert.equal(readable(rows, 'diskWrite'), true)
  assert.equal(readable([], 'processor'), false)
})

test('the current value is the newest reading that carries one', () => {
  const rows = toRows([point(1, { processorPercent: 12 }), point(2, { processorPercent: null })])

  assert.equal(latest(rows, 'processor'), 12)
  assert.equal(latest(toRows([]), 'processor'), null)
})

test('the processor axis never squeezes below ten percent, and never climbs past a hundred', () => {
  assert.equal(processorTop(0), 10)
  assert.equal(processorTop(1.4), 10)
  assert.equal(processorTop(11), 20)
  assert.equal(processorTop(64), 70)
  assert.equal(processorTop(100), 100)
  assert.equal(processorTop(Number.NaN), 10)
})

test('values are written the way a person reads them', () => {
  assert.equal(percent(4.4), '4%')
  assert.equal(perSecond(2048), '2.0 KB/s')
  assert.equal(memoryValue(300 * 1024 * 1024, 1024 * 1024 * 1024), '300 MB of 1.0 GB')
  assert.equal(memoryValue(300 * 1024 * 1024, null), '300 MB')
})
