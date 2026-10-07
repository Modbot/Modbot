import { useRef, useState, type CSSProperties, type KeyboardEvent, type PointerEvent as ReactPointerEvent, type ReactNode } from 'react'
import type { GridCell } from '@/components/availability/cells'
import { DAY_NAMES, hourText } from '@/lib/availabilityZones'
import { cn } from '@/lib/utils'

/**
 * A week as a grid of hours: the team's heatmap and a person's own week are both drawn with it.
 *
 * Days run down and hours across where there is room. On a phone they swap, hours down and days
 * across, so each of the seven columns is wide enough to hit with a finger and the page never scrolls
 * sideways. What each cell looks like and what a press does belong to the caller.
 *
 * One tab stop for the whole grid, the arrow keys move inside it and Enter or Space press the cell
 * the focus is on: 168 tab stops would be a week of tabbing.
 */
export function WeekGrid({
  days,
  hours,
  flipped,
  cellClass,
  cellStyle,
  cellContent,
  cellLabel,
  selected = null,
  onChoose,
  onPress,
  onKey,
  onHover,
}: {
  days: readonly number[]
  hours: readonly number[]
  flipped: boolean
  cellClass: (cell: GridCell) => string
  cellStyle?: (cell: GridCell) => CSSProperties | undefined
  cellContent?: (cell: GridCell) => ReactNode
  cellLabel: (cell: GridCell) => string
  selected?: GridCell | null
  /** A click or a tap, and Enter or Space on the focused cell when `onKey` is not given. */
  onChoose?: (cell: GridCell) => void
  /** The pointer going down on a cell, for a caller that tracks the press itself. */
  onPress?: (event: ReactPointerEvent, cell: GridCell) => void
  /** Enter or Space on the focused cell. */
  onKey?: (cell: GridCell) => void
  /** The mouse over a cell, and null when it leaves the grid. */
  onHover?: (cell: GridCell | null) => void
}) {
  const grid = useRef<HTMLDivElement>(null)
  const [focus, setFocus] = useState<GridCell>({ day: days[0] ?? 0, hour: hours[0] ?? 0 })

  // The cell the one tab stop is on, kept inside what is shown: a filter can take it away.
  const here: GridCell = {
    day: days.includes(focus.day) ? focus.day : (days[0] ?? 0),
    hour: hours.includes(focus.hour) ? focus.hour : (hours[0] ?? 0),
  }

  // The grid's own two directions: rows and columns, which are days and hours in one order or the other.
  const rows = flipped ? hours : days
  const columns = flipped ? days : hours
  const at = (row: number, column: number): GridCell =>
    flipped ? { day: columns[column], hour: rows[row] } : { day: rows[row], hour: columns[column] }

  const move = (event: KeyboardEvent, cell: GridCell) => {
    const row = rows.indexOf(flipped ? cell.hour : cell.day)
    const column = columns.indexOf(flipped ? cell.day : cell.hour)
    let nextRow = row
    let nextColumn = column

    if (event.key === 'ArrowDown') nextRow = Math.min(rows.length - 1, row + 1)
    else if (event.key === 'ArrowUp') nextRow = Math.max(0, row - 1)
    else if (event.key === 'ArrowRight') nextColumn = Math.min(columns.length - 1, column + 1)
    else if (event.key === 'ArrowLeft') nextColumn = Math.max(0, column - 1)
    else if (event.key === 'Enter' || event.key === ' ') {
      event.preventDefault()
      if (onKey) onKey(cell)
      else onChoose?.(cell)
      return
    } else return

    event.preventDefault()
    const next = at(nextRow, nextColumn)
    setFocus(next)
    grid.current
      ?.querySelector<HTMLElement>(`[data-week-cell][data-day="${next.day}"][data-hour="${next.hour}"]`)
      ?.focus()
  }

  const columnCount = columns.length

  return (
    // Its own scroll box, so a window too narrow for the hours moves the grid and not the page.
    <div className="overflow-x-auto">
      <div
        ref={grid}
        role="grid"
        aria-label="Week"
        className={cn('grid w-full gap-0.5 select-none', !flipped && 'min-w-[34rem]')}
        style={{ gridTemplateColumns: `${flipped ? '2.75rem' : '2.5rem'} repeat(${columnCount}, minmax(0, 1fr))` }}
        onMouseLeave={() => onHover?.(null)}
      >
        <div role="row" className="contents">
          <span />
          {columns.map((column) => (
            <span
              key={column}
              role="columnheader"
              className="pb-1 text-center font-mono text-muted-foreground"
              style={{ fontSize: 'var(--text-tiny)' }}
            >
              {flipped ? DAY_NAMES[column] : String(column).padStart(2, '0')}
            </span>
          ))}
        </div>

        {rows.map((row) => (
          <div key={row} role="row" className="contents">
            <span
              role="rowheader"
              className="flex items-center pr-1 font-mono text-muted-foreground"
              style={{ fontSize: 'var(--text-tiny)' }}
            >
              {flipped ? hourText(row) : DAY_NAMES[row]}
            </span>
            {columns.map((column) => {
              const cell: GridCell = flipped ? { day: column, hour: row } : { day: row, hour: column }
              const focused = cell.day === here.day && cell.hour === here.hour
              const chosen = selected?.day === cell.day && selected?.hour === cell.hour

              return (
                <div
                  key={column}
                  role="gridcell"
                  data-week-cell=""
                  data-day={cell.day}
                  data-hour={cell.hour}
                  tabIndex={focused ? 0 : -1}
                  aria-label={cellLabel(cell)}
                  aria-selected={selected ? chosen : undefined}
                  onFocus={() => setFocus(cell)}
                  onKeyDown={(event) => move(event, cell)}
                  onClick={onChoose ? () => onChoose(cell) : undefined}
                  onPointerDown={onPress ? (event) => onPress(event, cell) : undefined}
                  onMouseEnter={onHover ? () => onHover(cell) : undefined}
                  className={cn(
                    'flex items-center justify-center font-mono outline-none focus-visible:z-10 focus-visible:outline-2 focus-visible:outline-ring',
                    flipped ? 'h-8' : 'h-9',
                    chosen && 'outline-2 outline-foreground z-10',
                    cellClass(cell),
                  )}
                  style={{ fontSize: 'var(--text-tiny)', ...cellStyle?.(cell) }}
                >
                  {cellContent?.(cell)}
                </div>
              )
            })}
          </div>
        ))}
      </div>
    </div>
  )
}
