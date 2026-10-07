import type { AvailabilityState } from '../../lib/availabilityZones.ts'
import type { GridCell } from './cells.ts'

/** What the Paint switch has picked: a state to paint, or erasing. */
export type Tool = AvailabilityState | 'erase'

/** How long a finger is held in Scroll before it starts painting. */
export const PAINT_HOLD_MS = 300

/**
 * What a stroke does to every cell it passes, fixed by the cell it starts on: paint the chosen
 * state, or clear when that first cell already holds it, and the rest of the stroke clears too.
 */
export function strokeAction(tool: Tool, firstState: AvailabilityState | null): AvailabilityState | null {
  const painting = tool === 'erase' ? null : tool
  return painting !== null && firstState === painting ? null : painting
}

/**
 * The cells from one pointer sample to the next, both included, in the order the finger crossed
 * them. A fast move jumps over cells between two samples; this fills them in, down a column or
 * along a row, and in a straight line when the move went across.
 */
export function cellsBetween(from: GridCell, to: GridCell, flipped: boolean): GridCell[] {
  // The grid's own two directions: rows are hours when flipped, days when not.
  const rowOf = (cell: GridCell) => (flipped ? cell.hour : cell.day)
  const columnOf = (cell: GridCell) => (flipped ? cell.day : cell.hour)

  const fromRow = rowOf(from)
  const fromColumn = columnOf(from)
  const rowSpan = rowOf(to) - fromRow
  const columnSpan = columnOf(to) - fromColumn
  const steps = Math.max(Math.abs(rowSpan), Math.abs(columnSpan))

  const cells: GridCell[] = []
  for (let step = 0; step <= steps; step++) {
    const row = steps === 0 ? fromRow : Math.round(fromRow + (rowSpan * step) / steps)
    const column = steps === 0 ? fromColumn : Math.round(fromColumn + (columnSpan * step) / steps)
    cells.push(flipped ? { day: column, hour: row } : { day: row, hour: column })
  }
  return cells
}
