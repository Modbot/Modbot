import { useState } from 'react'
import { ChevronLeft, ChevronRight } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { addMonths, monthDays, sameDay, startOfMonth } from '@/lib/calendarGrid'
import { cn } from '@/lib/utils'

const monthName = new Intl.DateTimeFormat(undefined, { month: 'long', year: 'numeric' })
const LETTERS = Array.from({ length: 7 }, (_, i) =>
  new Intl.DateTimeFormat(undefined, { weekday: 'narrow' }).format(new Date(2026, 8, 21 + i)),
)

/**
 * The small month in the side column, for jumping to a date. The days on screen are shaded, today
 * is filled. Its own arrows page through months without moving the calendar.
 */
export function MiniMonth({
  anchor,
  shown,
  today,
  onPick,
}: {
  anchor: Date
  /** The days the calendar shows now, shaded here. */
  shown: Date[]
  today: Date
  onPick: (day: Date) => void
}) {
  // Follows the calendar when it moves to another month, and pages on its own in between.
  const [month, setMonth] = useState(() => startOfMonth(anchor))
  const [followed, setFollowed] = useState(() => startOfMonth(anchor).getTime())
  const anchorMonth = startOfMonth(anchor).getTime()
  if (anchorMonth !== followed) {
    setFollowed(anchorMonth)
    setMonth(startOfMonth(anchor))
  }

  const days = monthDays(month)

  return (
    <div className="flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
      <div className="flex items-center gap-1">
        <span className="flex-1 truncate pl-1 font-label">{monthName.format(month)}</span>
        <Button variant="ghost" size="icon-sm" aria-label="Previous month" onClick={() => setMonth(addMonths(month, -1))}>
          <ChevronLeft />
        </Button>
        <Button variant="ghost" size="icon-sm" aria-label="Next month" onClick={() => setMonth(addMonths(month, 1))}>
          <ChevronRight />
        </Button>
      </div>
      <div className="grid grid-cols-7 text-center">
        {LETTERS.map((l, i) => (
          <span key={i} className="py-1 text-muted-foreground" style={{ fontSize: 'var(--text-tiny)' }}>
            {l}
          </span>
        ))}
        {days.map((day) => {
          const inMonth = day.getMonth() === month.getMonth()
          const isShown = shown.some((d) => sameDay(d, day))
          return (
            <button
              key={day.toISOString()}
              type="button"
              onClick={() => onPick(day)}
              className={cn(
                'grid aspect-square min-h-(--control-h) place-items-center font-mono hover:bg-muted',
                isShown && 'bg-accent text-accent-foreground',
                !inMonth && 'text-muted-foreground',
              )}
            >
              <span
                className={cn(
                  'grid size-[1.9em] place-items-center rounded-full',
                  sameDay(day, today) && 'bg-primary text-primary-foreground',
                )}
              >
                {day.getDate()}
              </span>
            </button>
          )
        })}
      </div>
    </div>
  )
}
