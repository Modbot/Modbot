import { Fragment, useLayoutEffect, useRef } from 'react'
import { ChevronDown } from 'lucide-react'
import { keptFacts } from '@/lib/rowFacts'
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
 *
 * `narrow` is the same list drawn as two-line rows (`NarrowRows`), for a table too wide for a
 * phone. Given one, the table draws both and shows one by width (index.css, "A list with two
 * forms"): the rows on a phone and in a headset, the table everywhere else, unchanged.
 */
export function Table({
  head,
  pinFirst = false,
  nameColumn = 0,
  narrow,
  children,
}: {
  head: React.ReactNode
  pinFirst?: boolean
  nameColumn?: number
  narrow?: React.ReactNode
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
    <>
      {narrow}
      <div
        data-pin-first={pinFirst || undefined}
        data-layout={narrow ? 'wide' : undefined}
        className="relative overflow-x-auto"
      >
        <table ref={table} data-slot="table" className="w-full" style={{ fontSize: 'var(--text-small)' }}>
          <thead className="bg-strip text-left text-muted-foreground">
            <tr>{head}</tr>
          </thead>
          <tbody>{children}</tbody>
        </table>
      </div>
    </>
  )
}

/** A table's phone form: one `NarrowRow` for each of the table's rows, in the same order. */
export function NarrowRows({ children }: { children: React.ReactNode }) {
  return <ul data-layout="narrow">{children}</ul>
}

/**
 * One row of a list on a phone. Line 1 is the thing the row is about -- a name, the audit log's
 * sentence -- with `side` at its right end for a badge or the row's own buttons. Line 2 is the
 * table's other columns, muted, one line joined with " · ", in column order. When line 2 is too
 * long, the first fact gives way (a plain name, say), so the last ones -- usually when -- stay. The
 * others never shrink: when even they are too long, the line ends at the row's edge rather than
 * running past it.
 *
 * The whole row is the tap target, at least a row high. It is a button, unless something inside it
 * is a link or a button of its own (`hasLinks`): a button cannot hold another, so such a row is
 * clicked as a whole the way a table row is, and a tap on one of its own links is left to that
 * link. `open` is for a row that opens its details below it, in `children`. `children` also holds a
 * third line of buttons, for a row whose buttons do not fit beside its name: below the tap target
 * rather than in it, so a thumb that misses one does not open the row instead. `current` marks the
 * row a list is shown around, for a screen reader.
 */
export function NarrowRow({
  picture,
  main,
  side,
  facts = [],
  onOpen,
  open,
  hasLinks = false,
  current = false,
  className,
  ref,
  children,
}: {
  picture?: React.ReactNode
  main: React.ReactNode
  side?: React.ReactNode
  facts?: readonly (React.ReactNode | null | undefined | false | '')[]
  onOpen?: () => void
  open?: boolean
  hasLinks?: boolean
  current?: boolean
  className?: string
  ref?: React.Ref<HTMLLIElement>
  children?: React.ReactNode
}) {
  const shown = keptFacts(facts)

  const body = (
    <>
      {picture}
      <span className="block min-w-0 flex-1">
        <span className="flex items-center gap-2">
          <span className="block min-w-0 flex-1">{main}</span>
          {side && <span className="flex shrink-0 items-center gap-1">{side}</span>}
        </span>
        {shown.length > 0 && (
          <span
            className="flex min-w-0 items-center gap-1.5 overflow-hidden text-muted-foreground"
            style={{ fontSize: 'var(--text-small)' }}
          >
            {shown.map((fact, i) => (
              <Fragment key={i}>
                {i > 0 && <span className="shrink-0">·</span>}
                <span className={i === 0 ? 'min-w-0 truncate' : 'shrink-0 whitespace-nowrap'}>{fact}</span>
              </Fragment>
            ))}
          </span>
        )}
      </span>
    </>
  )

  const look = cn(
    'flex w-full items-center gap-2 px-(--panel-pad) py-2 text-left',
    onOpen && 'cursor-pointer hover:bg-muted/40',
    className,
  )

  return (
    <li ref={ref} aria-current={current || undefined} className="border-t border-(length:--hairline) first:border-t-0">
      {!onOpen ? (
        <div className={look} style={{ minHeight: 'var(--row-h)' }}>
          {body}
        </div>
      ) : hasLinks ? (
        <div
          tabIndex={0}
          aria-expanded={open}
          onClick={(e) => {
            // A dialog a row's button opened is drawn elsewhere in the page, but React still hands
            // its clicks up through the row; a tap inside an Unban confirmation is not a tap on the row.
            if (!e.currentTarget.contains(e.target as Node)) return
            if ((e.target as HTMLElement).closest('a, button, summary')) return
            onOpen()
          }}
          onKeyDown={(e) => {
            if (e.target !== e.currentTarget || (e.key !== 'Enter' && e.key !== ' ')) return
            e.preventDefault()
            onOpen()
          }}
          className={look}
          style={{ minHeight: 'var(--row-h)' }}
        >
          {body}
        </div>
      ) : (
        <button type="button" aria-expanded={open} onClick={onOpen} className={look} style={{ minHeight: 'var(--row-h)' }}>
          {body}
        </button>
      )}
      {children}
    </li>
  )
}

/**
 * What a `NarrowRow` opens under itself when its table has more columns than two lines can hold:
 * the rest of the row as a list, each column's name on the left and its value on the right. Team's
 * twelve counts are the case it was made for -- a phone keeps the name, the total and when, and a
 * tap reads the other ten in the table's order.
 */
export function NarrowDetails({ items }: { items: readonly { label: string; value: React.ReactNode }[] }) {
  return (
    <dl
      className="grid grid-cols-[1fr_auto] gap-x-4 gap-y-1 px-(--panel-pad) pb-2"
      style={{ fontSize: 'var(--text-small)' }}
    >
      {items.map((item) => (
        <Fragment key={item.label}>
          <dt className="text-muted-foreground">{item.label}</dt>
          <dd className="text-right">{item.value}</dd>
        </Fragment>
      ))}
    </dl>
  )
}

/** The mark at the end of a `NarrowRow` that opens: down when shut, up when open. */
export function NarrowChevron({ open }: { open: boolean }) {
  return (
    <ChevronDown
      className={cn('size-4 shrink-0 text-muted-foreground transition-transform', open && 'rotate-180')}
      aria-hidden
    />
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
