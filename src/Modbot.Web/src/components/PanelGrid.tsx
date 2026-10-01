import { useState } from 'react'
import { Button } from '@/components/ui/button'
import { useTryAgain } from '@/lib/tryAgain'
import { cn } from '@/lib/utils'

/**
 * Panels that sit side by side, sharing one hairline instead of standing apart with gaps.
 *
 * The columns are the caller's (`grid-cols-2 xl:grid-cols-4`, `lg:grid-cols-2`, `grid-cols-12`);
 * the lines are index.css's, which strips each child's own border and gives it an outline that
 * merges with its neighbour's. Any child works: a Card, a Stat, a SettingsCard, a chart panel.
 */
export function PanelGrid({
  id,
  as: Tag = 'div',
  className,
  phonePairs = false,
  children,
}: {
  /** An anchor to scroll to, such as Health's `#ai`. */
  id?: string
  /** `ul` or `ol` when the panels are the items of a list. */
  as?: 'div' | 'ul' | 'ol'
  className?: string
  /** Two across on a phone rather than three, for a strip of tiles (StatStrip). */
  phonePairs?: boolean
  children: React.ReactNode
}) {
  return (
    <Tag id={id} data-slot="panel-grid" data-phone-pairs={phonePairs || undefined} className={className}>
      {children}
    </Tag>
  )
}

/**
 * What a panel shows when it has nothing to show: one line, where the first row would have been.
 *
 * Left-aligned and a row high, so an empty list still reads as a list that happens to be empty
 * rather than as a message in the middle of a box. The hollow square is the console's "no signal"
 * indicator, the unfilled twin of the filled squares that mark a status. `danger` is for a list
 * that could not be read at all: the square fills in the destructive colour and the words, as
 * after every filled square, are plain foreground text.
 *
 * The same row covers the other two states a read passes through (console look §10), so every
 * list, panel and popup section draws them alike:
 *
 * - `loading` draws grey bars in the place of the words, pulsing unless reduced motion is asked
 *   for, so a list on its way does not read as a list that is empty. The words "Loading…" are
 *   there for a screen reader only.
 * - `danger` ends in a "Try again" button. `onTryAgain` is the read that failed; left out, the
 *   button falls back on the page or popup the row sits in (`lib/tryAgain.ts`), so no failed read
 *   is ever a dead end. `null` leaves the button out, for a row that states a fact rather than a
 *   read that failed (no permission to see evidence). When `onTryAgain` returns a promise, the row
 *   shows the loading bars until it settles, so a press always shows something happening.
 */
export function EmptyRow({
  children,
  className,
  tone = 'neutral',
  minHeight = 'calc(var(--row-h) * 1.5)',
  onTryAgain,
  lines = 2,
}: {
  children?: React.ReactNode
  className?: string
  /**
   * `danger` when the row stands in for a load that failed, not for a list that is empty;
   * `loading` while the read is on its way (the words are not shown).
   */
  tone?: RowTone
  /** Kept for a chart panel whose height should not jump when data arrives. */
  minHeight?: number | string
  /** What "Try again" runs on a `danger` row. `null` for no button. */
  onTryAgain?: (() => unknown) | null
  /** How many grey bars a `loading` row draws. */
  lines?: number
}) {
  const fallback = useTryAgain()
  const [trying, setTrying] = useState(false)

  if (tone === 'loading' || trying) {
    return <LoadingBars className={className} minHeight={minHeight} lines={lines} />
  }

  const tryAgain = tone !== 'danger' ? null : onTryAgain === undefined ? fallback : onTryAgain

  const press = () => {
    const result = tryAgain?.()
    if (isPromise(result)) {
      setTrying(true)
      const done = () => setTrying(false)
      result.then(done, done)
    }
  }

  return (
    <div
      data-slot="empty-row"
      className={cn(
        'flex items-center gap-2 px-(--panel-pad)',
        tone === 'danger' ? 'text-foreground' : 'text-muted-foreground',
        className,
      )}
      style={{ minHeight, fontSize: 'var(--text-small)' }}
    >
      <span
        aria-hidden
        className={cn(
          'size-2 shrink-0',
          tone === 'danger' ? 'bg-destructive' : 'border border-current opacity-70',
        )}
      />
      <span className="min-w-0">{children}</span>
      {tryAgain && <TryAgainButton className="ml-auto" onClick={press} />}
    </div>
  )
}

/**
 * The "Try again" of a failed row, for the few failures said inside a line of their own rather
 * than as a row (the Standing line's "Could not load flags.", the notes beside a ban).
 */
export function TryAgainButton({ onClick, className }: { onClick: () => void; className?: string }) {
  return (
    <Button type="button" variant="outline" size="xs" className={className} onClick={onClick}>
      Try again
    </Button>
  )
}

/** The three states `EmptyRow` draws, and every page message built on it. */
export type RowTone = 'neutral' | 'danger' | 'loading'

/** What a one-line state built on `EmptyRow` takes (a page message, a popup section's state). */
export type RowState = {
  tone?: RowTone
  children?: React.ReactNode
  onTryAgain?: (() => unknown) | null
}

// Different lengths, so the bars read as lines of text on their way rather than as a box.
const BAR_WIDTHS = ['16rem', '11rem', '20rem', '13rem']

/** A `loading` row: the empty row's place and height, with grey bars where the words would be. */
function LoadingBars({
  className,
  minHeight,
  lines,
}: {
  className?: string
  minHeight: number | string
  lines: number
}) {
  return (
    <div
      data-slot="empty-row"
      data-loading=""
      role="status"
      className={cn('flex flex-col justify-center gap-2 px-(--panel-pad)', className)}
      style={{ minHeight, fontSize: 'var(--text-small)' }}
    >
      <span className="sr-only">Loading…</span>
      {Array.from({ length: Math.max(1, lines) }, (_, i) => (
        <span
          key={i}
          aria-hidden
          className="block h-[0.75em] max-w-full animate-pulse rounded-sm bg-muted motion-reduce:animate-none"
          style={{ width: BAR_WIDTHS[i % BAR_WIDTHS.length] }}
        />
      ))}
    </div>
  )
}

function isPromise(value: unknown): value is PromiseLike<unknown> {
  return typeof value === 'object' && value !== null && typeof (value as PromiseLike<unknown>).then === 'function'
}
