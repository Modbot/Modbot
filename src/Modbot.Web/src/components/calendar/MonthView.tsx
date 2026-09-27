import { useState, type KeyboardEvent } from 'react'
import { Card } from '@/components/ui/card'
import { atMinutes, daysTouched, isAllDay, minutesIntoDay, movedByDays, sameDay } from '@/lib/calendarGrid'
import { timeOfDay } from '@/lib/format'
import { isModalOpen } from '@/lib/shortcuts'
import { cn } from '@/lib/utils'
import { mayChange, spotOf, toneClass, type Change, type Entry, type Spot } from './entry'
import { beginPress, dayIndexAt, type PointerPoint } from './pointer'

/** How many events a day cell lists before the rest are a "+2 more" that opens the day. */
const SHOWN_PER_DAY = 3

const WEEKDAYS = Array.from({ length: 7 }, (_, i) =>
  new Intl.DateTimeFormat(undefined, { weekday: 'short' }).format(new Date(2026, 8, 21 + i)),
)

/**
 * The Month view: six weeks, Monday first. An event is listed on the day it starts, and an event a
 * day or longer on every day it covers.
 *
 * Dragging an event to another day moves it there at the same time of day. Clicking empty space in
 * a day starts a new event on it; the date opens that day.
 */
export function MonthView({
  days,
  month,
  entries,
  now,
  canManage,
  onOpen,
  onCreate,
  onChange,
  onPickDay,
}: {
  days: Date[]
  /** Any date in the month being shown, to grey the days either side of it. */
  month: Date
  entries: Entry[]
  now: Date
  canManage: boolean
  onOpen: (entry: Entry, spot: Spot) => void
  onCreate: (range: { start: Date; end: Date }, spot: Spot) => void
  onChange: (entry: Entry, change: Change) => void
  onPickDay: (day: Date) => void
}) {
  const [drag, setDrag] = useState<{ entry: Entry; target: number } | null>(null)

  const onDay = (day: Date) =>
    entries.filter((e) => (isAllDay(e) ? daysTouched(e).some((d) => sameDay(d, day)) : sameDay(e.start, day)))

  const pressEntry = (event: React.PointerEvent<HTMLElement>, entry: Entry, from: number) => {
    event.stopPropagation()
    const element = event.currentTarget
    const open = () => onOpen(entry, spotOf(element))

    if (!mayChange(canManage, entry.event)) {
      beginPress(event, { onTap: open, onEnd: open })
      return
    }

    const target = (at: PointerPoint) => dayIndexAt(at) ?? from

    beginPress(event, {
      onMove: (at) => setDrag({ entry, target: target(at) }),
      onEnd: (at) => {
        setDrag(null)
        const to = target(at)
        if (to !== from) onChange(entry, { ...movedByDays(entry, to - from), kind: 'move' })
      },
      onTap: open,
      onCancel: () => setDrag(null),
    })
  }

  const pressEmpty = (event: React.PointerEvent<HTMLElement>, day: Date) => {
    // A press outside an open event or quick form closes it, as Google's does, and starts nothing.
    if (!canManage || event.target !== event.currentTarget || isModalOpen()) return
    const cell = event.currentTarget

    // A new event on that day, at the next whole hour of the clock: the same hour the New event
    // form starts at.
    const hour = Math.min(23, Math.floor(minutesIntoDay(now) / 60) + 1)
    const create = () => onCreate({ start: atMinutes(day, hour * 60), end: atMinutes(day, (hour + 1) * 60) }, spotOf(cell))
    beginPress(event, { onTap: create })
  }

  const openByKey = (event: KeyboardEvent<HTMLElement>, entry: Entry) => {
    if (event.key !== 'Enter' && event.key !== ' ') return
    event.preventDefault()
    onOpen(entry, spotOf(event.currentTarget))
  }

  return (
    <Card className="grid grid-cols-7 select-none [-webkit-touch-callout:none]">
      {WEEKDAYS.map((d) => (
        <div
          key={d}
          className="flex min-h-(--strip-h) items-center border-b border-b-(length:--hairline) bg-strip px-2 text-muted-foreground"
          style={{ fontSize: 'var(--text-small)' }}
        >
          {d}
        </div>
      ))}
      {days.map((day, index) => {
        const inMonth = day.getMonth() === month.getMonth()
        const listed = onDay(day)
        const shown = listed.slice(0, SHOWN_PER_DAY)
        const hidden = listed.length - shown.length

        return (
          <div
            key={day.toISOString()}
            data-day-index={index}
            onPointerDown={(e) => pressEmpty(e, day)}
            className={cn(
              'flex min-h-20 min-w-0 flex-col gap-0.5 border-r border-r-(length:--hairline) border-b border-b-(length:--hairline) p-0.5 sm:min-h-28 [&:nth-child(7n)]:border-r-0 [&:nth-last-child(-n+7)]:border-b-0',
              !inMonth && 'bg-strip/50',
              drag?.target === index && 'bg-accent',
              canManage && 'cursor-cell',
            )}
          >
            <button
              type="button"
              onClick={() => onPickDay(day)}
              className={cn(
                'grid size-(--control-h) shrink-0 place-items-center self-start rounded-full font-mono hover:bg-muted',
                inMonth ? 'text-foreground' : 'text-muted-foreground',
                sameDay(day, now) && 'bg-primary text-primary-foreground hover:bg-primary/90',
              )}
              style={{ fontSize: 'var(--text-small)' }}
            >
              {day.getDate()}
            </button>

            {shown.map((entry) => (
              <button
                key={entry.key}
                type="button"
                onPointerDown={(e) => pressEntry(e, entry, index)}
                onKeyDown={(e) => openByKey(e, entry)}
                title={entry.event.title}
                className={cn(
                  'min-h-(--control-h) truncate rounded-sm border border-(length:--hairline) border-l-[3px] px-1 text-left',
                  toneClass(entry.event),
                  entry.event.state === 'finished' && 'opacity-70',
                  mayChange(canManage, entry.event) && 'cursor-grab',
                  drag?.entry.key === entry.key && 'opacity-40',
                )}
                style={{ fontSize: 'var(--text-small)' }}
              >
                {/* The title, not the clock, in a cell a seventh of a phone wide: the time is back
                    from `sm` up, and the Schedule states both at any width. */}
                {!isAllDay(entry) && (
                  <span className="hidden font-mono opacity-80 sm:inline">{timeOfDay(entry.start.toISOString())} </span>
                )}
                {entry.event.title}
              </button>
            ))}

            {drag && drag.target === index && !listed.some((e) => e.key === drag.entry.key) && (
              <div
                aria-hidden
                className={cn(
                  'pointer-events-none min-h-(--control-h) truncate rounded-sm border border-(length:--hairline) border-l-[3px] px-1 shadow-sm',
                  toneClass(drag.entry.event),
                )}
                style={{ fontSize: 'var(--text-small)' }}
              >
                {drag.entry.event.title}
              </div>
            )}

            {hidden > 0 && (
              <button
                type="button"
                onClick={() => onPickDay(day)}
                className="min-h-(--control-h) self-start rounded-sm px-1 text-muted-foreground hover:bg-muted hover:text-foreground"
                style={{ fontSize: 'var(--text-small)' }}
              >
                +{hidden} more
              </button>
            )}
          </div>
        )
      })}
    </Card>
  )
}
