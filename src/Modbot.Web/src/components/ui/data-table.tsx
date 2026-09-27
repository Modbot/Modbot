import { useLayoutEffect, useRef } from 'react'
import { cn } from '@/lib/utils'

/**
 * A table run to its panel's edges: the column names on the strip, one hairline between rows.
 * The column names are one line with 0.5rem above and below it and a row is `--row-h`, taller
 * only when a cell holds more than that: the same two heights as every other table in the app.
 * `pinFirst` keeps the first column in place while a phone scrolls the rest sideways, for tables
 * whose first column names the row.
 *
 * In a headset the table does not scroll sideways at all: each row is drawn as a small block, the
 * cell that names the row on top and every other cell under it beside its column's name (index.css,
 * "Tables in a headset"). `nameColumn` says which column names the row, where it is not the first.
 * The column names are copied onto the cells here, because a stacked cell is far from its header
 * and CSS cannot read one element's text into another.
 */
export function Table({
  head,
  pinFirst = false,
  nameColumn = 0,
  children,
}: {
  head: React.ReactNode
  pinFirst?: boolean
  nameColumn?: number
  children: React.ReactNode
}) {
  const table = useRef<HTMLTableElement>(null)

  useLayoutEffect(() => {
    const el = table.current
    if (!el) return

    const label = () => {
      const names: string[] = []
      for (const th of el.tHead?.rows[0]?.cells ?? []) {
        // A name only a screen reader hears ("Actions") is not written beside the cell either.
        const unseen = [...th.querySelectorAll('.sr-only')].map((e) => e.textContent ?? '').join('')
        const all = th.textContent?.trim() ?? ''
        const name = all === unseen.trim() ? '' : all
        for (let i = 0; i < th.colSpan; i++) names.push(name)
      }

      for (const body of el.tBodies) {
        for (const row of body.rows) {
          let column = 0
          for (const cell of row.cells) {
            if (cell.colSpan > 1) {
              cell.dataset.wide = ''
            } else {
              delete cell.dataset.wide
              if (column === nameColumn) cell.dataset.name = ''
              else delete cell.dataset.name
            }
            const name = cell.colSpan > 1 || column === nameColumn ? '' : (names[column] ?? '')
            if (name) cell.dataset.label = name
            else delete cell.dataset.label
            column += cell.colSpan
          }
        }
      }
    }

    label()
    // Rows come and go as a page loads and filters, and a header can change its words. Setting the
    // attributes is not a change the observer listens for, so it never wakes itself.
    const observer = new MutationObserver(label)
    observer.observe(el, { childList: true, subtree: true, characterData: true })
    return () => observer.disconnect()
  }, [nameColumn])

  return (
    <div data-pin-first={pinFirst || undefined} className="relative overflow-x-auto">
      <table ref={table} data-slot="table" className="w-full" style={{ fontSize: 'var(--text-small)' }}>
        <thead className="bg-strip text-left text-muted-foreground">
          <tr>{head}</tr>
        </thead>
        <tbody>{children}</tbody>
      </table>
    </div>
  )
}

export function Th({ className, ...props }: React.ComponentProps<'th'>) {
  return <th className={cn('px-(--panel-pad) py-2 font-normal whitespace-nowrap', className)} {...props} />
}

export function Tr({ className, ...props }: React.ComponentProps<'tr'>) {
  return <tr className={cn('h-(--row-h) border-t border-t-(length:--hairline)', className)} {...props} />
}

export function Td({ className, ...props }: React.ComponentProps<'td'>) {
  return <td className={cn('px-(--panel-pad) whitespace-nowrap', className)} {...props} />
}
