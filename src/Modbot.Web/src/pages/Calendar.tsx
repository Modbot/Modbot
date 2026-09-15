import { useCallback, useEffect, useMemo, useState } from 'react'
import { ChevronLeft, ChevronRight } from 'lucide-react'
import { CalendarEventForm } from '@/components/calendar/CalendarEventForm'
import { WorldLink } from '@/components/facts'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { Dialog, DialogContent } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { ApiError } from '@/lib/api'
import {
  calendarApi,
  PLACE_LABEL,
  PLACE_STATE_LABEL,
  STATE_LABEL,
  type CalendarEvent,
  type CalendarFeed,
  type CalendarView,
} from '@/lib/calendar'
import { useLocation } from '@/lib/router'
import { openInstance } from '@/lib/subject'
import { cn } from '@/lib/utils'
import { PageMessage, Toggle } from '@/pages/analytics/shared'

type Mode = 'month' | 'agenda'

/** One occurrence of one event, as the month grid and the agenda both list them. */
type Entry = { event: CalendarEvent; startsAt: Date; endsAt: Date }

const WEEKDAYS = ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun']

/**
 * The calendar (calendar design): planned events in a month grid or a list, where each is
 * published and how that went, and the calendar feed link.
 *
 * Times on the page are the browser's own. The form keeps each event's own time zone, which is
 * what its repeats are counted in.
 */
export function Calendar() {
  const [mode, setMode] = useState<Mode>('month')
  const [month, setMonth] = useState(() => startOfMonth(new Date()))
  const [data, setData] = useState<CalendarView | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [openId, setOpenId] = useState<string | null>(null)

  // `?event=` opens one event, whenever it runs: what a source chip under a Chat answer links to.
  const [location] = useLocation()
  const linkedId = location.search.get('event')
  const [linked, setLinked] = useState<CalendarEvent | null>(null)
  const [editing, setEditing] = useState<CalendarEvent | 'new' | null>(null)

  const range = useMemo(() => {
    if (mode === 'month') {
      const from = startOfWeek(month)
      return { from, to: addDays(from, 42) }
    }

    const from = addDays(new Date(), -1)
    return { from, to: addDays(from, 60) }
  }, [mode, month])

  const load = useCallback(() => {
    calendarApi
      .view(range.from, range.to)
      .then((view) => {
        setData(view)
        setError(null)
      })
      .catch((e: unknown) => {
        setError(
          e instanceof ApiError && e.status === 403
            ? 'You do not have permission to see this.'
            : e instanceof ApiError
              ? e.message
              : 'Could not load the calendar.',
        )
      })
  }, [range])

  useEffect(() => {
    load()
    const timer = window.setInterval(load, 20000)
    return () => window.clearInterval(timer)
  }, [load])

  const entries = useMemo<Entry[]>(
    () =>
      (data?.events ?? [])
        .flatMap((event) =>
          event.occurrences.map((o) => ({ event, startsAt: new Date(o.startsAt), endsAt: new Date(o.endsAt) })),
        )
        .sort((a, b) => a.startsAt.getTime() - b.startsAt.getTime()),
    [data],
  )

  useEffect(() => {
    if (!linkedId) return

    let cancelled = false
    calendarApi
      .event(linkedId)
      .then((e) => {
        if (cancelled) return
        setLinked(e)
        setOpenId(e.id)
      })
      .catch(() => {
        // An event that is gone, or one this account may not see: the calendar opens as usual.
      })

    return () => {
      cancelled = true
    }
  }, [linkedId])

  const drafts = useMemo(() => (data?.events ?? []).filter((e) => e.state === 'draft'), [data])

  // An event linked to may run outside the window on screen, so the one that was fetched stands in.
  const opened =
    data?.events.find((e) => e.id === openId) ?? (linked && linked.id === openId ? linked : null)

  if (!data) return <PageMessage>{error ?? 'Loading…'}</PageMessage>

  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-wrap items-center gap-3">
        <Toggle
          value={mode}
          onChange={setMode}
          options={[
            { value: 'month', label: 'Month' },
            { value: 'agenda', label: 'Agenda' },
          ]}
        />

        {mode === 'month' && (
          <div className="flex items-center gap-1">
            <Button variant="ghost" size="icon-sm" aria-label="Previous month" onClick={() => setMonth(addMonths(month, -1))}>
              <ChevronLeft />
            </Button>
            <span className="min-w-36 text-center font-medium">
              {month.toLocaleDateString(undefined, { month: 'long', year: 'numeric' })}
            </span>
            <Button variant="ghost" size="icon-sm" aria-label="Next month" onClick={() => setMonth(addMonths(month, 1))}>
              <ChevronRight />
            </Button>
            <Button variant="outline" size="sm" onClick={() => setMonth(startOfMonth(new Date()))}>
              Today
            </Button>
          </div>
        )}

        <div className="flex-1" />

        {data.canManage && <Button onClick={() => setEditing('new')}>New event</Button>}
      </div>

      {error && (
        <p className="text-destructive" style={{ fontSize: 'var(--text-small)' }}>
          {error}
        </p>
      )}

      {data.canManage && <FeedRow />}

      {mode === 'month' ? (
        <MonthGrid month={month} entries={entries} now={data.now} onOpen={setOpenId} />
      ) : (
        <Agenda entries={entries} now={data.now} onOpen={setOpenId} />
      )}

      {drafts.length > 0 && (
        <Card>
          <CardContent className="flex flex-col gap-1 py-3">
            <span className="font-medium">Drafts</span>
            {drafts.map((d) => (
              <button
                key={d.id}
                type="button"
                className="text-left hover:underline"
                style={{ fontSize: 'var(--text-small)' }}
                onClick={() => setOpenId(d.id)}
              >
                {d.title}
              </button>
            ))}
          </CardContent>
        </Card>
      )}

      {opened && (
        <EventDialog
          event={opened}
          canManage={data.canManage}
          onClose={() => setOpenId(null)}
          onEdit={() => {
            setEditing(opened)
            setOpenId(null)
          }}
          onChanged={load}
        />
      )}

      {editing && (
        <CalendarEventForm
          event={editing === 'new' ? null : editing}
          categories={data.categories}
          platforms={data.platforms}
          onClose={() => setEditing(null)}
          onSaved={(saved) => {
            setEditing(null)
            load()
            setOpenId(saved.id)
          }}
        />
      )}
    </div>
  )
}

function MonthGrid({ month, entries, now, onOpen }: { month: Date; entries: Entry[]; now: string; onOpen: (id: string) => void }) {
  const first = startOfWeek(month)
  const days = Array.from({ length: 42 }, (_, i) => addDays(first, i))
  const today = new Date(now)

  return (
    <div className="overflow-x-auto">
      <div className="grid min-w-[720px] grid-cols-7 overflow-hidden rounded-xl border" style={{ borderWidth: 'var(--hairline)' }}>
        {WEEKDAYS.map((d) => (
          <div
            key={d}
            className="border-b bg-secondary px-2 py-1 text-muted-foreground"
            style={{ fontSize: 'var(--text-small)', borderBottomWidth: 'var(--hairline)' }}
          >
            {d}
          </div>
        ))}
        {days.map((day) => {
          const inMonth = day.getMonth() === month.getMonth()
          const onDay = entries.filter((e) => sameDay(e.startsAt, day))

          return (
            <div
              key={day.toISOString()}
              className={cn('min-h-24 border-r border-b p-1', !inMonth && 'bg-secondary/40')}
              style={{ borderWidth: 'var(--hairline)' }}
            >
              <div
                className={cn(
                  'mb-1 px-1 tabular-nums',
                  inMonth ? 'text-foreground' : 'text-muted-foreground',
                  sameDay(day, today) && 'font-semibold',
                )}
                style={{ fontSize: 'var(--text-small)' }}
              >
                {day.getDate()}
              </div>
              <div className="flex flex-col gap-0.5">
                {onDay.map((entry) => (
                  <button
                    key={`${entry.event.id}-${entry.startsAt.toISOString()}`}
                    type="button"
                    onClick={() => onOpen(entry.event.id)}
                    className={cn(
                      'truncate rounded-md px-1 text-left hover:bg-secondary',
                      entry.event.state === 'cancelled' && 'line-through text-muted-foreground',
                      entry.event.state === 'open' && 'text-ok',
                    )}
                    style={{ fontSize: 'var(--text-small)' }}
                    title={entry.event.title}
                  >
                    <span className="tabular-nums text-muted-foreground">{time(entry.startsAt)}</span> {entry.event.title}
                  </button>
                ))}
              </div>
            </div>
          )
        })}
      </div>
    </div>
  )
}

function Agenda({ entries, now, onOpen }: { entries: Entry[]; now: string; onOpen: (id: string) => void }) {
  const upcoming = entries.filter((e) => e.endsAt.getTime() > new Date(now).getTime() - 86400000)

  if (upcoming.length === 0) return <PageMessage>No events.</PageMessage>

  return (
    <Card>
      <CardContent className="flex flex-col py-2">
        {upcoming.map((entry) => (
          <div
            key={`${entry.event.id}-${entry.startsAt.toISOString()}`}
            className="flex flex-wrap items-center gap-x-3 gap-y-1 border-b py-2 last:border-b-0"
            style={{ borderBottomWidth: 'var(--hairline)' }}
          >
            <span className="w-44 shrink-0 tabular-nums text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
              {entry.startsAt.toLocaleDateString(undefined, { weekday: 'short', day: 'numeric', month: 'short' })}{' '}
              {time(entry.startsAt)}–{time(entry.endsAt)}
            </span>
            <button type="button" className="font-medium hover:underline" onClick={() => onOpen(entry.event.id)}>
              {entry.event.title}
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
      </CardContent>
    </Card>
  )
}

function EventDialog({
  event,
  canManage,
  onClose,
  onEdit,
  onChanged,
}: {
  event: CalendarEvent
  canManage: boolean
  onClose: () => void
  onEdit: () => void
  onChanged: () => void
}) {
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const act = (run: () => Promise<void>, close: boolean) => {
    setBusy(true)
    setError(null)
    run()
      .then(() => {
        onChanged()
        if (close) onClose()
      })
      .catch((e: unknown) => setError(e instanceof ApiError ? e.message : 'That did not work.'))
      .finally(() => setBusy(false))
  }

  const start = new Date(event.occurrenceStartsAt ?? event.startsAt)
  const end = new Date(event.occurrenceEndsAt ?? event.endsAt)
  const live = event.state === 'scheduled' || event.state === 'open'

  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent title={event.title} subtitle={<StateBadge event={event} />} className="max-w-[560px]">
        <div className="flex flex-col gap-3" style={{ fontSize: 'var(--text-small)' }}>
          <div className="tabular-nums">
            {start.toLocaleString(undefined, { weekday: 'long', day: 'numeric', month: 'long', hour: 'numeric', minute: '2-digit' })}
            {' – '}
            {time(end)}
          </div>

          {event.description && <p className="whitespace-pre-wrap">{event.description}</p>}

          {event.worldId && (
            <div>
              <span className="text-muted-foreground">World </span>
              <WorldLink id={event.worldId} name={event.worldName} unnamed="id" />
            </div>
          )}

          {event.opening && (
            <div>
              <span className="text-muted-foreground">Instance </span>
              {event.opening.error ? (
                <span className="text-destructive">{event.opening.error}</span>
              ) : event.opening.roomId ? (
                <button type="button" className="hover:underline" onClick={() => openInstance(event.opening!.roomId!)}>
                  {event.opening.closed ? 'Closed' : 'Open'}
                </button>
              ) : (
                <span>Opening</span>
              )}
              {event.opening.joinLink && (
                <a className="ml-2 underline" href={event.opening.joinLink} target="_blank" rel="noreferrer">
                  Join
                </a>
              )}
            </div>
          )}

          {event.places.length > 0 && (
            <div className="flex flex-col gap-1">
              {event.places.map((p) => (
                <div key={p.place} className="flex flex-wrap items-center gap-2">
                  <PlaceBadge place={p.place} state={p.state} />
                  {p.error && <span className="text-destructive">{p.error}</span>}
                </div>
              ))}
            </div>
          )}

          {error && <p className="text-destructive">{error}</p>}

          {canManage && (
            <div className="flex flex-wrap gap-2 pt-1">
              {event.state !== 'cancelled' && (
                <Button size="sm" disabled={busy} onClick={onEdit}>
                  Edit
                </Button>
              )}
              {live && (
                <Button size="sm" variant="outline" disabled={busy} onClick={() => act(() => calendarApi.cancel(event.id), false)}>
                  Cancel event
                </Button>
              )}
              <Button size="sm" variant="destructive" disabled={busy} onClick={() => act(() => calendarApi.remove(event.id), true)}>
                Delete
              </Button>
            </div>
          )}
        </div>
      </DialogContent>
    </Dialog>
  )
}

function FeedRow() {
  const [feed, setFeed] = useState<CalendarFeed | null>(null)
  const [busy, setBusy] = useState(false)
  const [copied, setCopied] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    calendarApi
      .feed()
      .then(setFeed)
      .catch(() => setError('Could not load the feed link.'))
  }, [])

  const link = feed?.url ?? (feed?.path ? `${window.location.origin}${feed.path}` : null)

  const regenerate = () => {
    setBusy(true)
    setError(null)
    calendarApi
      .regenerateFeed()
      .then(setFeed)
      .catch((e: unknown) => setError(e instanceof ApiError ? e.message : 'Could not make a new link.'))
      .finally(() => setBusy(false))
  }

  const copy = () => {
    if (!link) return
    void navigator.clipboard.writeText(link).then(() => {
      setCopied(true)
      window.setTimeout(() => setCopied(false), 1500)
    })
  }

  return (
    <div className="flex flex-wrap items-center gap-2" style={{ fontSize: 'var(--text-small)' }}>
      <span className="text-muted-foreground">Calendar feed</span>
      {link ? (
        <>
          <Input readOnly value={link} className="h-8 max-w-xl flex-1 font-mono" onFocus={(e) => e.target.select()} />
          <Button size="sm" variant="outline" onClick={copy}>
            {copied ? 'Copied' : 'Copy'}
          </Button>
          <Button size="sm" variant="outline" disabled={busy} onClick={regenerate}>
            New link
          </Button>
        </>
      ) : (
        <Button size="sm" variant="outline" disabled={busy || !feed} onClick={regenerate}>
          Make link
        </Button>
      )}
      {error && <span className="text-destructive">{error}</span>}
    </div>
  )
}

function StateBadge({ event }: { event: CalendarEvent }) {
  return (
    <Badge
      variant={event.state === 'open' ? 'default' : event.state === 'cancelled' ? 'destructive' : 'secondary'}
    >
      {STATE_LABEL[event.state] ?? event.state}
    </Badge>
  )
}

function PlaceBadge({ place, state }: { place: CalendarEvent['places'][number]['place']; state: CalendarEvent['places'][number]['state'] }) {
  return (
    <Badge variant={state === 'failed' ? 'destructive' : state === 'published' ? 'secondary' : 'outline'}>
      {PLACE_LABEL[place] ?? place}: {PLACE_STATE_LABEL[state] ?? state}
    </Badge>
  )
}

function time(date: Date): string {
  return date.toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit' })
}

function startOfMonth(date: Date): Date {
  return new Date(date.getFullYear(), date.getMonth(), 1)
}

function addMonths(date: Date, months: number): Date {
  return new Date(date.getFullYear(), date.getMonth() + months, 1)
}

function addDays(date: Date, days: number): Date {
  const next = new Date(date)
  next.setDate(next.getDate() + days)
  return next
}

/** The Monday on or before a date, at midnight. */
function startOfWeek(date: Date): Date {
  const day = new Date(date.getFullYear(), date.getMonth(), date.getDate())
  const offset = (day.getDay() + 6) % 7
  return addDays(day, -offset)
}

function sameDay(a: Date, b: Date): boolean {
  return a.getFullYear() === b.getFullYear() && a.getMonth() === b.getMonth() && a.getDate() === b.getDate()
}
