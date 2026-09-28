import { useLayoutEffect, useRef, useState, type CSSProperties, type KeyboardEvent } from 'react'
import { Card } from '@/components/ui/card'
import {
  addDays,
  columnAt,
  daysTouched,
  drawnRange,
  firstHour,
  hourLabel,
  isAllDay,
  layoutDay,
  MINUTES_PER_DAY,
  minutesIntoDay,
  movedByDays,
  movedTo,
  resizedTo,
  sameDay,
  segmentOn,
  snapMinutes,
  startOfDay,
} from '@/lib/calendarGrid'
import { timeOfDay } from '@/lib/format'
import { isModalOpen } from '@/lib/shortcuts'
import { cn } from '@/lib/utils'
import { mayChange, spotOf, toneClass, type Change, type Entry, type Spot } from './entry'
import { beginPress, dayIndexAt, type PointerPoint } from './pointer'

/** The hours down the side, from 1 AM: midnight is the top edge and needs no label. */
const HOUR_LABELS = Array.from({ length: 23 }, (_, i) => ({ hour: i + 1, label: hourLabel(i + 1) }))

/** The weekday over a column, in the viewer's language. */
const weekday = new Intl.DateTimeFormat(undefined, { weekday: 'short' })

/** What is being dragged right now, and where it would land. */
type Drag =
  | { kind: 'create'; day: number; start: Date; end: Date }
  | { kind: 'move' | 'resize'; entry: Entry; start: Date; end: Date }
  | { kind: 'shift'; entry: Entry; target: number; start: Date; end: Date }

/**
 * The Day and Week views: hours down the side, one column a day, an all-day row on top for events
 * a day or longer, and a red line at the time it is now.
 *
 * Everything here is drawn in the viewer's own time. Pressing on empty time draws a new event;
 * pressing on an event drags it (to another time, or another day across the week), and its bottom
 * edge changes the end. Nothing is saved while the pointer moves: `onChange` is called once, when
 * it lets go. For somebody who may only look, events open and nothing moves.
 */
export function TimeGrid({
  days,
  entries,
  now,
  canManage,
  ready,
  scrollKey,
  ghost,
  onOpen,
  onCreate,
  onChange,
  onPickDay,
}: {
  days: Date[]
  entries: Entry[]
  now: Date
  canManage: boolean
  /** The entries for these days have arrived, so the first one can be scrolled to. */
  ready: boolean
  /** Changes when the days do: the grid scrolls to the morning (or the first event) once for each. */
  scrollKey: string
  /** A new event being written in the quick form, drawn where it will go. */
  ghost: { start: Date; end: Date } | null
  onOpen: (entry: Entry, spot: Spot) => void
  onCreate: (range: { start: Date; end: Date }, spot: Spot) => void
  onChange: (entry: Entry, change: Change) => void
  onPickDay: (day: Date) => void
}) {
  const scrollRef = useRef<HTMLDivElement>(null)
  const columnsRef = useRef<HTMLDivElement>(null)
  const probeRef = useRef<HTMLDivElement>(null)
  const [drag, setDrag] = useState<Drag | null>(null)
  // The shortest block, in minutes: one control tall, whatever the density.
  const [minLength, setMinLength] = useState(30)

  const timed = entries.filter((e) => !isAllDay(e))
  const long = entries.filter((e) => isAllDay(e))

  useLayoutEffect(() => {
    const columns = columnsRef.current
    const probe = probeRef.current
    if (!columns || !probe) return

    const measure = () => {
      const perMinute = columns.offsetHeight / MINUTES_PER_DAY
      if (perMinute > 0) setMinLength(probe.offsetHeight / perMinute)
    }
    measure()

    const observer = new ResizeObserver(measure)
    observer.observe(columns)
    observer.observe(probe)
    return () => observer.disconnect()
  }, [])

  // Scrolled once for each set of days: an hour before the first event, or to 8 AM.
  const scrolledFor = useRef<string | null>(null)
  useLayoutEffect(() => {
    if (!ready || scrolledFor.current === scrollKey) return
    const scroll = scrollRef.current
    const columns = columnsRef.current
    if (!scroll || !columns) return

    scrolledFor.current = scrollKey
    const starts = timed.filter((e) => days.some((d) => sameDay(d, e.start))).map((e) => e.start)
    scroll.scrollTop = (firstHour(starts) * columns.offsetHeight) / 24
  })

  // ── Where the pointer is ──────────────────────────────────────────────────────────────────

  /** The column under a point and the unsnapped minute it is at. */
  const locate = (at: PointerPoint) => {
    const r = columnsRef.current!.getBoundingClientRect()
    return {
      day: columnAt(at.x - r.left, r.width, days.length),
      raw: ((at.y - r.top) / r.height) * MINUTES_PER_DAY,
    }
  }

  /** Where a stretch of time on a day is on screen, for the quick form to point at. */
  const spotFor = (day: number, start: Date, end: Date): Spot => {
    const r = columnsRef.current!.getBoundingClientRect()
    const width = r.width / days.length
    const segment = segmentOn({ start, end }, days[day]) ?? { start: 0, end: 60 }
    return {
      left: r.left + day * width,
      top: r.top + (segment.start / MINUTES_PER_DAY) * r.height,
      width,
      height: Math.max(((segment.end - segment.start) / MINUTES_PER_DAY) * r.height, 24),
    }
  }

  /** Nudges the grid up or down while a drag is held near its top or bottom edge. */
  const edgeScroll = (at: PointerPoint) => {
    const scroll = scrollRef.current
    if (!scroll) return
    const r = scroll.getBoundingClientRect()
    if (at.y < r.top + 40) scroll.scrollTop -= 16
    else if (at.y > r.bottom - 40) scroll.scrollTop += 16
  }

  // ── Presses ───────────────────────────────────────────────────────────────────────────────

  const pressEmpty = (event: React.PointerEvent, day: number) => {
    // A press outside an open event or quick form closes it, as Google's does, and starts nothing.
    if (!canManage || event.target !== event.currentTarget || isModalOpen()) return
    const from = snapMinutes(locate({ x: event.clientX, y: event.clientY }).raw, undefined, 'floor')

    const rangeTo = (at: PointerPoint) => drawnRange(days[day], from, locate(at).raw)

    beginPress(event, {
      onMove: (at) => {
        edgeScroll(at)
        setDrag({ kind: 'create', day, ...rangeTo(at) })
      },
      onEnd: (at) => {
        const range = rangeTo(at)
        setDrag(null)
        onCreate(range, spotFor(day, range.start, range.end))
      },
      onTap: () => {
        const range = drawnRange(days[day], from, from)
        onCreate(range, spotFor(day, range.start, range.end))
      },
      onCancel: () => setDrag(null),
    })
  }

  const pressEntry = (event: React.PointerEvent<HTMLElement>, entry: Entry) => {
    const element = event.currentTarget
    const open = () => onOpen(entry, spotOf(element))

    if (!mayChange(canManage, entry.event)) {
      beginPress(event, { onTap: open, onEnd: open })
      return
    }

    const pressed = locate({ x: event.clientX, y: event.clientY })
    const landing = (at: PointerPoint) => {
      const here = locate(at)
      const day = addDays(startOfDay(entry.start), here.day - pressed.day)
      return movedTo(entry, day, snapMinutes(minutesIntoDay(entry.start) + here.raw - pressed.raw))
    }

    beginPress(event, {
      onMove: (at) => {
        edgeScroll(at)
        setDrag({ kind: 'move', entry, ...landing(at) })
      },
      onEnd: (at) => {
        setDrag(null)
        const next = landing(at)
        if (next.start.getTime() !== entry.start.getTime()) onChange(entry, { ...next, kind: 'move' })
      },
      onTap: open,
      onCancel: () => setDrag(null),
    })
  }

  const pressEdge = (event: React.PointerEvent, entry: Entry) => {
    event.stopPropagation()
    const landing = (at: PointerPoint) => {
      const here = locate(at)
      return resizedTo(entry, days[here.day], snapMinutes(here.raw))
    }

    beginPress(event, {
      onMove: (at) => {
        edgeScroll(at)
        setDrag({ kind: 'resize', entry, ...landing(at) })
      },
      onEnd: (at) => {
        setDrag(null)
        const next = landing(at)
        if (next.end.getTime() !== entry.end.getTime()) onChange(entry, { ...next, kind: 'resize' })
      },
      onCancel: () => setDrag(null),
    })
  }

  /** An all-day chip dragged along the row: the same time of day, on another date. */
  const pressLong = (event: React.PointerEvent<HTMLElement>, entry: Entry, from: number) => {
    const element = event.currentTarget
    const open = () => onOpen(entry, spotOf(element))

    if (!mayChange(canManage, entry.event)) {
      beginPress(event, { onTap: open, onEnd: open })
      return
    }

    const landing = (at: PointerPoint) => {
      const target = dayIndexAt(at) ?? from
      return { target, ...movedByDays(entry, target - from) }
    }

    beginPress(event, {
      onMove: (at) => setDrag({ kind: 'shift', entry, ...landing(at) }),
      onEnd: (at) => {
        setDrag(null)
        const next = landing(at)
        if (next.target !== from) onChange(entry, { start: next.start, end: next.end, kind: 'move' })
      },
      onTap: open,
      onCancel: () => setDrag(null),
    })
  }

  const openByKey = (event: KeyboardEvent<HTMLElement>, entry: Entry) => {
    if (event.key !== 'Enter' && event.key !== ' ') return
    event.preventDefault()
    onOpen(entry, spotOf(event.currentTarget))
  }

  // ── Drawing ───────────────────────────────────────────────────────────────────────────────

  const columnTemplate: CSSProperties = { gridTemplateColumns: `repeat(${days.length}, minmax(0, 1fr))` }
  const dragged = drag && drag.kind !== 'create' ? drag.entry.key : null
  const preview = drag ?? (ghost ? { kind: 'create' as const, day: -1, ...ghost } : null)

  return (
    // An hour is at least 48 px, and more at a density with bigger controls, so a quarter hour
    // stays a quarter of something a pointer can land on.
    <Card className="relative overflow-hidden" style={{ '--cal-hour': 'max(3rem, calc(var(--control-h) * 1.6))' } as CSSProperties}>
      <div ref={probeRef} aria-hidden className="pointer-events-none invisible absolute h-(--control-h) w-px" />
      <div
        ref={scrollRef}
        className="overflow-y-auto overscroll-contain select-none [-webkit-touch-callout:none]"
        style={{ height: 'max(22rem, calc(100dvh - 13rem))' }}
      >
        {/* The day names and the all-day row stay put while the hours scroll under them. */}
        <div className="sticky top-0 z-20 bg-card">
          <div className="flex border-b border-b-(length:--hairline) bg-strip">
            <div className="w-14 shrink-0" />
            <div className="grid flex-1" style={columnTemplate}>
              {days.map((day) => {
                const today = sameDay(day, now)
                return (
                  <button
                    key={day.toISOString()}
                    type="button"
                    onClick={() => onPickDay(day)}
                    className="flex min-h-(--strip-h) min-w-0 items-center justify-center gap-1.5 px-1 hover:bg-muted"
                    style={{ fontSize: 'var(--text-small)' }}
                  >
                    <span className="truncate text-muted-foreground">{weekday.format(day)}</span>
                    <span
                      className={cn(
                        'grid min-w-6 place-items-center rounded-full px-1 font-mono',
                        today && 'bg-primary text-primary-foreground',
                      )}
                    >
                      {day.getDate()}
                    </span>
                  </button>
                )
              })}
            </div>
          </div>

          {(long.length > 0 || drag?.kind === 'shift') && (
            <div className="flex border-b border-b-(length:--hairline)">
              <div className="w-14 shrink-0" />
              <div className="grid flex-1" style={columnTemplate}>
                {days.map((day, index) => (
                  <div
                    key={day.toISOString()}
                    data-day-index={index}
                    className={cn(
                      'flex min-w-0 flex-col gap-0.5 border-l border-l-(length:--hairline) p-0.5',
                      drag?.kind === 'shift' && drag.target === index && 'bg-accent',
                    )}
                  >
                    {long
                      .filter((e) => daysTouched(e).some((d) => sameDay(d, day)))
                      .map((entry) => (
                        <button
                          key={entry.key}
                          type="button"
                          onPointerDown={(e) => pressLong(e, entry, index)}
                          onKeyDown={(e) => openByKey(e, entry)}
                          className={cn(
                            'min-h-(--control-h) truncate rounded-sm border border-(length:--hairline) border-l-[3px] px-1.5 text-left font-medium',
                            toneClass(entry.event),
                            entry.event.state === 'finished' && 'opacity-70',
                            dragged === entry.key && 'opacity-40',
                          )}
                          style={{ fontSize: 'var(--text-small)' }}
                        >
                          {entry.event.title}
                        </button>
                      ))}
                  </div>
                ))}
              </div>
            </div>
          )}
        </div>

        <div className="flex">
          {/* Hours down the side, each label on the line it names. */}
          <div className="relative w-14 shrink-0" style={{ height: 'calc(var(--cal-hour) * 24)' }}>
            {HOUR_LABELS.map(({ hour, label }) => (
              <div
                key={hour}
                className="absolute right-1.5 -translate-y-1/2 font-mono whitespace-nowrap text-muted-foreground"
                style={{ top: `${(hour / 24) * 100}%`, fontSize: 'var(--text-tiny)' }}
              >
                {label}
              </div>
            ))}
          </div>

          <div
            ref={columnsRef}
            className="relative grid flex-1"
            style={{
              ...columnTemplate,
              height: 'calc(var(--cal-hour) * 24)',
              backgroundImage:
                'repeating-linear-gradient(to bottom, var(--border) 0 var(--hairline), transparent var(--hairline) var(--cal-hour))',
            }}
          >
            {days.map((day, index) => {
              const pieces = timed
                .map((entry) => ({ entry, segment: segmentOn(entry, day) }))
                .filter((p): p is { entry: Entry; segment: { start: number; end: number } } => p.segment !== null)
              const placed = new Map(
                layoutDay(pieces.map((p) => ({ key: p.entry.key, ...p.segment })), minLength).map((p) => [p.key, p]),
              )
              const today = sameDay(day, now)

              return (
                <div
                  key={day.toISOString()}
                  onPointerDown={(e) => pressEmpty(e, index)}
                  className={cn('relative min-w-0 border-l border-l-(length:--hairline)', canManage && 'cursor-cell')}
                >
                  {pieces.map(({ entry, segment }) => {
                    const place = placed.get(entry.key)!
                    const last = sameDay(day, new Date(entry.end.getTime() - 1))
                    const editable = mayChange(canManage, entry.event)

                    return (
                      <button
                        key={entry.key}
                        type="button"
                        onPointerDown={(e) => pressEntry(e, entry)}
                        onKeyDown={(e) => openByKey(e, entry)}
                        className={cn(
                          'absolute flex flex-col rounded-sm border border-(length:--hairline) border-l-[3px] px-1.5 py-0.5 text-left focus-visible:outline-2 focus-visible:outline-ring',
                          toneClass(entry.event),
                          entry.event.state === 'finished' && 'opacity-70',
                          editable && 'cursor-grab',
                          dragged === entry.key && 'opacity-40',
                        )}
                        style={{
                          top: `${(segment.start / MINUTES_PER_DAY) * 100}%`,
                          height: `max(${((segment.end - segment.start) / MINUTES_PER_DAY) * 100}%, var(--control-h))`,
                          left: `calc(${(place.column / place.columns) * 100}% + 1px)`,
                          width: `calc(${(place.span / place.columns) * 100}% - 3px)`,
                          fontSize: 'var(--text-small)',
                        }}
                      >
                        <span className="flex min-h-0 flex-col overflow-hidden">
                          <span className="truncate font-medium">{entry.event.title}</span>
                          <span className="truncate font-mono opacity-80" style={{ fontSize: 'var(--text-tiny)' }}>
                            {timeOfDay(entry.start.toISOString())} – {timeOfDay(entry.end.toISOString())}
                          </span>
                        </span>
                        {editable && last && (
                          // The bottom edge: one control tall, centred on the edge, so a finger or a
                          // laser pointer can catch it as well as a mouse. The half below the block
                          // only catches over empty time; an event drawn below sits on top of it.
                          <span
                            aria-hidden
                            onPointerDown={(e) => pressEdge(e, entry)}
                            className="absolute inset-x-0 -bottom-[calc(var(--control-h)/2)] h-(--control-h) cursor-ns-resize"
                          />
                        )}
                      </button>
                    )
                  })}

                  {preview && preview.kind !== 'shift' && segmentOn(preview, day) && (
                    <Preview
                      segment={segmentOn(preview, day)!}
                      title={preview.kind === 'create' ? null : preview.entry.event.title}
                      start={preview.start}
                      end={preview.end}
                      tone={preview.kind === 'create' ? null : toneClass(preview.entry.event)}
                    />
                  )}

                  {today && (
                    <div
                      aria-hidden
                      className="pointer-events-none absolute inset-x-0 z-10 h-0.5 bg-destructive"
                      style={{ top: `${(minutesIntoDay(now) / MINUTES_PER_DAY) * 100}%` }}
                    >
                      <span className="absolute -top-[5px] -left-[6px] size-3 rounded-full bg-destructive" />
                    </div>
                  )}
                </div>
              )
            })}
          </div>
        </div>
      </div>
    </Card>
  )
}

/** Where a drag would land, or the new event the quick form is writing, drawn over the grid. */
function Preview({
  segment,
  title,
  start,
  end,
  tone,
}: {
  segment: { start: number; end: number }
  title: string | null
  start: Date
  end: Date
  tone: string | null
}) {
  return (
    <div
      aria-hidden
      className={cn(
        'pointer-events-none absolute inset-x-0.5 z-10 flex flex-col overflow-hidden rounded-sm border border-(length:--hairline) border-l-[3px] px-1.5 py-0.5 shadow-sm',
        tone ?? 'border-primary bg-primary text-primary-foreground',
      )}
      style={{
        top: `${(segment.start / MINUTES_PER_DAY) * 100}%`,
        height: `max(${((segment.end - segment.start) / MINUTES_PER_DAY) * 100}%, var(--control-h))`,
        fontSize: 'var(--text-small)',
      }}
    >
      {title && <span className="truncate font-medium">{title}</span>}
      <span className="truncate font-mono" style={{ fontSize: 'var(--text-tiny)' }}>
        {timeOfDay(start.toISOString())} – {timeOfDay(end.toISOString())}
      </span>
    </div>
  )
}
