import { WorldLink } from '@/components/facts'
import { Card } from '@/components/ui/card'
import { addDays, sameDay, startOfDay } from '@/lib/calendarGrid'
import { timeOfDay } from '@/lib/format'
import { cn } from '@/lib/utils'
import { PageMessage } from '@/pages/analytics/shared'
import { spotOf, toneClass, type Entry, type Spot } from './entry'
import { PlaceBadge, StateBadge } from './EventDetails'

const dayName = new Intl.DateTimeFormat(undefined, { weekday: 'short', month: 'short', day: 'numeric' })

/**
 * The Schedule view: every occurrence from the day shown onwards, a day at a time, with the event's
 * status, world and where it is published. What the Agenda was, and the view a phone opens on.
 */
export function ScheduleView({
  from,
  entries,
  now,
  onOpen,
}: {
  from: Date
  entries: Entry[]
  now: Date
  onOpen: (entry: Entry, spot: Spot) => void
}) {
  const start = startOfDay(from)
  const listed = entries.filter((e) => e.end.getTime() > start.getTime())

  if (listed.length === 0) return <PageMessage>No events.</PageMessage>

  const days: { day: Date; entries: Entry[] }[] = []
  for (const entry of listed) {
    const day = entry.start.getTime() < start.getTime() ? start : startOfDay(entry.start)
    const last = days[days.length - 1]
    if (last && sameDay(last.day, day)) last.entries.push(entry)
    else days.push({ day, entries: [entry] })
  }

  return (
    <Card className="divide-y-(--hairline) divide-border">
      {days.map(({ day, entries: onDay }) => (
        <div key={day.toISOString()} className="flex flex-col gap-1 px-(--panel-pad) py-1.5 sm:flex-row sm:gap-3">
          <div
            className={cn(
              'w-32 shrink-0 pt-1.5 font-mono text-muted-foreground',
              sameDay(day, now) && 'font-semibold text-foreground',
              sameDay(day, addDays(now, 1)) && 'text-foreground',
            )}
            style={{ fontSize: 'var(--text-small)' }}
          >
            {dayName.format(day)}
          </div>
          <div className="flex min-w-0 flex-1 flex-col">
            {onDay.map((entry) => (
              <div key={entry.key} className="flex min-h-(--row-h) flex-wrap items-center gap-x-3 gap-y-1 py-0.5">
                <span className="w-36 shrink-0 font-mono text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
                  {timeOfDay(entry.start.toISOString())} – {timeOfDay(entry.end.toISOString())}
                </span>
                <button
                  type="button"
                  className="flex min-h-(--control-h) min-w-0 items-center gap-2 text-left font-medium hover:underline"
                  onClick={(e) => onOpen(entry, spotOf(e.currentTarget))}
                >
                  <span aria-hidden className={cn('size-3 shrink-0 rounded-sm border border-(length:--hairline) border-l-[3px]', toneClass(entry.event))} />
                  <span className="truncate">{entry.event.title}</span>
                </button>
                <StateBadge event={entry.event} />
                {entry.event.worldId && (
                  <span style={{ fontSize: 'var(--text-small)' }}>
                    <WorldLink id={entry.event.worldId} name={entry.event.worldName} unnamed="id" />
                  </span>
                )}
                <div className="flex flex-wrap gap-1">
                  {entry.event.places
                    .filter((p) => p.state !== 'removed')
                    .map((p) => (
                      <PlaceBadge key={p.place} place={p.place} state={p.state} />
                    ))}
                </div>
              </div>
            ))}
          </div>
        </div>
      ))}
    </Card>
  )
}
