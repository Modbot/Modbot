import { useCallback, useEffect, useMemo, useState } from 'react'
import { changesCalendar } from '@/lib/liveRules'
import { useLiveVersion } from '@/lib/useLiveVersion'
import { ChevronLeft, ChevronRight } from 'lucide-react'
import { CalendarEventForm } from '@/components/calendar/CalendarEventForm'
import { WorldLink } from '@/components/facts'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardHeader, CardTitle } from '@/components/ui/card'
import { Dialog, DialogContent } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { SwitchBank } from '@/components/ui/switch-bank'
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
import { PageMessage } from '@/pages/analytics/shared'

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

  // Read again when the live stream says an event was planned, changed, opened or finished, or
  // VRChat's calendar moved. It used to ask every twenty seconds.
  const live = useLiveVersion(changesCalendar)

  useEffect(() => {
    load()
  }, [load, live])

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
    <div className="flex flex-col gap-3">
      <div className="flex flex-wrap items-center gap-3">
        <SwitchBank
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
          <CardHeader>
            <CardTitle>Drafts</CardTitle>
          </CardHeader>
          <div className="flex flex-col divide-y-(--hairline) divide-border">
            {drafts.map((d) => (
              <button
                key={d.id}
                type="button"
                className="flex min-h-(--row-h) items-center px-(--panel-pad) text-left hover:underline"
                style={{ fontSize: 'var(--text-small)' }}
                onClick={() => setOpenId(d.id)}
              >
                {d.title}
              </button>
            ))}
          </div>
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
    <div className="relative overflow-x-auto">
      {/*
        All seven days fit on a phone rather than four of them and a sideways scroll. A month view
        that has to be scrolled through is not a month view: the point of it is the shape of the
        month, and four days at a time shows no shape at all. The cells get narrow and an event
        title clips to a word or two, which is what tapping the day is for — and the Agenda beside
        this reads the same events out in full.
      */}
      <Card className="grid grid-cols-7 sm:min-w-[720px]">
        {WEEKDAYS.map((d) => (
          <div
            key={d}
            className="flex min-h-(--strip-h) items-center border-b border-b-(length:--hairline) bg-strip px-2 text-muted-foreground"
            style={{ fontSize: 'var(--text-small)' }}
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
              className={cn(
                'min-h-16 border-r border-b border-(length:--hairline) p-1 sm:min-h-24 [&:nth-child(7n)]:border-r-0 [&:nth-last-child(-n+7)]:border-b-0',
                !inMonth && 'bg-strip/50',
              )}
            >
              <div
                className={cn(
                  'mb-1 px-1 font-mono',
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
                      'truncate rounded-sm px-1 text-left hover:bg-muted',
                      entry.event.state === 'cancelled' && 'line-through text-muted-foreground',
                      entry.event.state === 'open' && 'text-ok',
                    )}
                    style={{ fontSize: 'var(--text-small)' }}
                    title={entry.event.title}
                  >
                    {/* The title, not the clock, in a cell a seventh of a phone wide: "3:0…" says
                        nothing about the event and "Qu…" at least says which one. The time is
                        back from `sm` up, and the Agenda states both at any width. */}
                    <span className="hidden font-mono text-muted-foreground sm:inline">{time(entry.startsAt)} </span>
                    {entry.event.title}
                  </button>
                ))}
              </div>
            </div>
          )
        })}
      </Card>
    </div>
  )
}

function Agenda({ entries, now, onOpen }: { entries: Entry[]; now: string; onOpen: (id: string) => void }) {
  const upcoming = entries.filter((e) => e.endsAt.getTime() > new Date(now).getTime() - 86400000)

  if (upcoming.length === 0) return <PageMessage>No events.</PageMessage>

  return (
    <Card className="divide-y-(--hairline) divide-border">
        {upcoming.map((entry) => (
          <div
            key={`${entry.event.id}-${entry.startsAt.toISOString()}`}
            className="flex min-h-(--row-h) flex-wrap items-center gap-x-3 gap-y-1 px-(--panel-pad) py-1.5"
          >
            <span className="w-52 shrink-0 font-mono text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
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
          <div className="font-mono">
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
              ) : event.opening.instanceId ? (
                <button type="button" className="hover:underline" onClick={() => openInstance(event.opening!.instanceId!)}>
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
          <Input readOnly value={link} className="max-w-xl flex-1 font-mono" onFocus={(e) => e.target.select()} />
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
