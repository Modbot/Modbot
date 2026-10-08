import assert from 'node:assert/strict'
import { test } from 'node:test'
import { cellsBetween, PAINT_HOLD_MS, strokeAction } from '../src/components/availability/strokes.ts'

const keys = (cells: { day: number; hour: number }[]) => cells.map((cell) => `${cell.day}:${cell.hour}`)

test('a move to the neighbouring cell gives just the two cells', () => {
  assert.deepEqual(keys(cellsBetween({ day: 2, hour: 9 }, { day: 2, hour: 10 }, false)), ['2:9', '2:10'])
})

test('a fast move along a row fills in the cells it skipped, in order', () => {
  // Not flipped: days are the rows, hours run along them.
  assert.deepEqual(keys(cellsBetween({ day: 1, hour: 8 }, { day: 1, hour: 12 }, false)), ['1:8', '1:9', '1:10', '1:11', '1:12'])
})

test('a fast move the other way along a row comes back in the order it was crossed', () => {
  assert.deepEqual(keys(cellsBetween({ day: 1, hour: 12 }, { day: 1, hour: 9 }, false)), ['1:12', '1:11', '1:10', '1:9'])
})

test('on a phone, hours run down and days across, and a move down a column fills in the hours', () => {
  assert.deepEqual(keys(cellsBetween({ day: 3, hour: 5 }, { day: 3, hour: 9 }, true)), ['3:5', '3:6', '3:7', '3:8', '3:9'])
})

test('on a phone, a move sideways fills in the days between', () => {
  assert.deepEqual(keys(cellsBetween({ day: 0, hour: 14 }, { day: 3, hour: 14 }, true)), ['0:14', '1:14', '2:14', '3:14'])
})

test('a move across both ways has no gap, every step is one cell on at most', () => {
  const cells = cellsBetween({ day: 0, hour: 0 }, { day: 4, hour: 8 }, false)
  assert.equal(cells.length, 9)
  assert.deepEqual(cells[0], { day: 0, hour: 0 })
  assert.deepEqual(cells[8], { day: 4, hour: 8 })
  for (let i = 1; i < cells.length; i++) {
    assert.ok(Math.abs(cells[i].day - cells[i - 1].day) <= 1)
    assert.ok(Math.abs(cells[i].hour - cells[i - 1].hour) <= 1)
  }
})

test('the same cell twice is one cell', () => {
  assert.deepEqual(keys(cellsBetween({ day: 6, hour: 23 }, { day: 6, hour: 23 }, true)), ['6:23'])
})

// The grid draws Sunday first, so Sunday (6) sits next to Monday (0) and Saturday (5) is last.
const SUNDAY_FIRST = [6, 0, 1, 2, 3, 4, 5]

test('with Sunday drawn first, a move from Sunday to Monday is two cells and does not fill in the week', () => {
  assert.deepEqual(keys(cellsBetween({ day: 6, hour: 9 }, { day: 0, hour: 9 }, false, SUNDAY_FIRST)), ['6:9', '0:9'])
  assert.deepEqual(keys(cellsBetween({ day: 0, hour: 9 }, { day: 6, hour: 9 }, true, SUNDAY_FIRST)), ['0:9', '6:9'])
})

test('with Sunday drawn first, a move from Friday to Saturday is two cells', () => {
  assert.deepEqual(keys(cellsBetween({ day: 4, hour: 20 }, { day: 5, hour: 20 }, false, SUNDAY_FIRST)), ['4:20', '5:20'])
})

test('with Sunday drawn first, a fast move from Sunday to Wednesday passes Monday and Tuesday in drawn order', () => {
  assert.deepEqual(keys(cellsBetween({ day: 6, hour: 12 }, { day: 2, hour: 12 }, true, SUNDAY_FIRST)), ['6:12', '0:12', '1:12', '2:12'])
})

test('with Sunday drawn first, a move down a column on a phone is the same as before', () => {
  assert.deepEqual(keys(cellsBetween({ day: 6, hour: 5 }, { day: 6, hour: 8 }, true, SUNDAY_FIRST)), ['6:5', '6:6', '6:7', '6:8'])
})

test('pressing an empty cell paints the chosen state', () => {
  assert.equal(strokeAction('free', null), 'free')
  assert.equal(strokeAction('ifNeeded', null), 'ifNeeded')
})

test('pressing a cell that holds another state paints over it', () => {
  assert.equal(strokeAction('free', 'ifNeeded'), 'free')
  assert.equal(strokeAction('ifNeeded', 'free'), 'ifNeeded')
})

test('pressing a cell that already holds the chosen state clears it, and so does the rest of the stroke', () => {
  // The action is worked out once, on the first cell, and then applied to every cell the stroke passes.
  const action = strokeAction('free', 'free')
  assert.equal(action, null)
  const passed = cellsBetween({ day: 0, hour: 8 }, { day: 0, hour: 11 }, false)
  assert.equal(passed.length, 4)
  assert.ok(passed.every(() => action === null))
})

test('Erase always clears, whatever the first cell holds', () => {
  assert.equal(strokeAction('erase', null), null)
  assert.equal(strokeAction('erase', 'free'), null)
  assert.equal(strokeAction('erase', 'ifNeeded'), null)
})

test('a hold in Scroll waits about 300 ms', () => {
  assert.equal(PAINT_HOLD_MS, 300)
})
