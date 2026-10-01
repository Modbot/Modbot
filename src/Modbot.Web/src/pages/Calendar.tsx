import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { ChevronLeft, ChevronRight, RefreshCw } from 'lucide-react'
import { CalendarEventForm } from '@/components/calendar/CalendarEventForm'
import { DateForm } from '@/components/calendar/DateForm'
import type { Change, Entry, Spot } from '@/components/calendar/entry'
import { dateAt, spotOf } from '@/components/calendar/entry'
import { EventDetails } from '@/components/calendar/EventDetails'
import { MiniMonth } from '@/components/calendar/MiniMonth'
import { MonthView } from '@/components/calendar/MonthView'
import { PastEvents } from '@/components/calendar/PastEvents'
import { NARROW, useMedia } from '@/components/calendar/phone'
import { QuickCreate } from '@/components/calendar/QuickCreate'
import { ScheduleView } from '@/components/calendar/ScheduleView'
import { TimeGrid } from '@/components/calendar/TimeGrid'
import { UndoToast, type Toast } from '@/components/calendar/UndoToast'
import { ConfirmDialog } from '@/components/ConfirmDialog'
import { Button } from '@/components/ui/button'
import { Card, CardHeader, CardTitle } from '@/components/ui/card'
import { Dialog, DialogContent, DialogFoot } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { SwitchBank } from '@/components/ui/switch-bank'
import { ApiError } from '@/lib/api'
import {
  calendarApi,
  inputFrom,
  type CalendarEvent,
  type CalendarEventInput,
  type CalendarFeed,
  type CalendarOccurrence,
  type CalendarOccurrenceResult,
  type CalendarView,
  hasRun,
  vrchatReadProblem,
} from '@/lib/calendar'
import {
  movedInput,
  PHONE_WEEK_LENGTH,
  sameDay,
  startOfDay,
  stepAnchor,
  viewDays,
  viewRange,
  viewTitle,
  type CalendarViewName,
} from '@/lib/calendarGrid'
import { firstView, rememberView } from '@/lib/calendarView'
import { timeOfDay } from '@/lib/format'
import { changesCalendar } from '@/lib/liveRules'
import { go, useLocation } from '@/lib/router'
import { useShortcuts } from '@/lib/shortcuts'
import { useLiveVersion } from '@/lib/useLiveVersion'
import { PageMessage } from '@/pages/analytics/shared'

const VIEWS: { value: CalendarViewName; label: string }[] = [
  { value: 'day', label: 'Day' },
  { value: 'week', label: 'Week' },
  { value: 'month', label: 'Month' },
  { value: 'schedule', label: 'Schedule' },
]

const shortDay = new Intl.DateTimeFormat(undefined, { weekday: 'short', month: 'short', day: 'numeric' })

/** An open event: which one, which of its dates, and where on screen it was clicked (none for a link). */
type Detail = { id: string; start: Date; end: Date; spot: Spot | null }

/**
 * The calendar (calendar design), laid out the way Google Calendar is: Day, Week, Month and
 * Schedule views, a small month to jump with, and events that are moved by dragging them.
 *
 * Times on the page are the browser's own. The form keeps each event's own time zone, which is what
 * its repeats are counted in; a drag is measured on screen and saved in that zone (calendarGrid
 * `movedInput`), through the same update the form's Save sends, so it is recorded and published
 * exactly as an edit is.
 */
export function Calendar() {
  const [view, setView] = useState<CalendarViewName>(() =>
    firstView(typeof window !== 'undefined' && window.matchMedia(NARROW).matches),
  )
  // Week is three days on a phone held upright (calendarGrid `PHONE_WEEK_LENGTH`), seven elsewhere.
  const weekLength = useMedia(NARROW) ? PHONE_WEEK_LENGTH : 7
  const [anchor, setAnchor] = useState(() => startOfDay(new Date()))
  const [data, setData] = useState<{ key: string; view: CalendarView } | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [detail, setDetail] = useState<Detail | null>(null)
  const [editing, setEditing] = useState<{ event: CalendarEvent | null; initial?: CalendarEventInput } | null>(null)
  const [quick, setQuick] = useState<{ start: Date; end: Date; spot: Spot } | null>(null)
  const [asking, setAsking] = useState<{ entry: Entry; change: Change } | null>(null)
  // An event drawn at its new time while its save is on the way, so it does not jump back: every
  // date of it, or only `date` when one date is moving on its own.
  const [moving, setMoving] = useState<{ eventId: string; date?: string; startBy: number; endBy: number } | null>(null)
  const [editingDate, setEditingDate] = useState<{ event: CalendarEvent; occurrence: CalendarOccurrence } | null>(null)
  const [toast, setToast] = useState<Toast | null>(null)
  const toastCount = useRef(0)
  // How far the server's clock is ahead of the browser's, from the last load.
  const [skew, setSkew] = useState(0)

  // `?event=` opens one event, whenever it runs: what a source chip under a Chat answer links to.
  const [location] = useLocation()
  const linkedId = location.search.get('event')
  const [linked, setLinked] = useState<CalendarEvent | null>(null)

  // `?new` opens the form for a new event: the VRChat page's "Create event" leads here.
  const wantsNew = location.search.has('new')

  const range = useMemo(() => viewRange(view, anchor, weekLength), [view, anchor, weekLength])
  const rangeKey = `${range.from.toISOString()}|${range.to.toISOString()}`
  const days = useMemo(() => viewDays(view, anchor, weekLength), [view, anchor, weekLength])

  const load = useCallback(
    () =>
      calendarApi
        .view(range.from, range.to)
        .then((loaded) => {
          setData({ key: rangeKey, view: loaded })
          setSkew(new Date(loaded.now).getTime() - Date.now())
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
        }),
    [range, rangeKey],
  )

  // Read again when the live stream says an event was planned, changed, opened or finished, or
  // VRChat's calendar moved.
  const live = useLiveVersion(changesCalendar)

  useEffect(() => {
    void load()
  }, [load, live])

  // VRChat's own calendar, for the dates on screen: read when they come into view and on Refresh,
  // never on a timer (calendar design §12.1). The server remembers each month for five minutes, so
  // moving back and forth costs nothing; Refresh asks again.
  const [reading, setReading] = useState(0)
  const [readProblem, setReadProblem] = useState<string | null>(null)

  const readVRChat = useCallback(
    (refresh: boolean) => {
      setReading((n) => n + 1)
      return calendarApi
        .readVRChat({ from: range.from, to: range.to, refresh })
        .then((read) => {
          setReadProblem(vrchatReadProblem(read))
          if (read.outcome === 'read') return load()
        })
        .catch((e: unknown) => setReadProblem(e instanceof ApiError ? e.message : "Could not read VRChat's calendar."))
        .finally(() => setReading((n) => n - 1))
    },
    [range, load],
  )

  useEffect(() => {
    void readVRChat(false)
  }, [readVRChat])

  const now = useNow(skew)

  useEffect(() => {
    if (!linkedId) return

    let cancelled = false
    calendarApi
      .event(linkedId)
      .then((e) => {
        if (cancelled) return
        setLinked(e)
        setDetail({
          id: e.id,
          start: new Date(e.occurrenceStartsAt ?? e.startsAt),
          end: new Date(e.occurrenceEndsAt ?? e.endsAt),
          spot: null,
        })
      })
      .catch(() => {
        // An event that is gone, or one this account may not see: the calendar opens as usual.
      })

    return () => {
      cancelled = true
    }
  }, [linkedId])

  const entries = useMemo<Entry[]>(
    () =>
      (data?.view.events ?? [])
        .flatMap((event) => {
          // A date is known by its planned start, so one moved on its own keeps its place in the
          // list; dates cancelled on their own are drawn too, struck through (calendar design §2.2).
          const toEntry = (o: CalendarOccurrence, cancelled: boolean): Entry => {
            const shift =
              !cancelled && moving?.eventId === event.id && (moving.date === undefined || moving.date === o.plannedStartsAt)
                ? moving
                : null
            return {
              key: `${event.id}|${o.plannedStartsAt}`,
              event,
              start: new Date(new Date(o.startsAt).getTime() + (shift?.startBy ?? 0)),
              end: new Date(new Date(o.endsAt).getTime() + (shift?.endBy ?? 0)),
              date: o.plannedStartsAt,
              title: o.title || event.title,
              cancelled,
            }
          }

          return [
            ...event.occurrences.map((o) => toEntry(o, false)),
            ...(event.cancelledDates ?? []).map((o) => toEntry(o, true)),
          ]
        })
        .sort((a, b) => a.start.getTime() - b.start.getTime() || a.key.localeCompare(b.key)),
    [data, moving],
  )

  const canManage = data?.view.canManage ?? false

  const step = (direction: 1 | -1) => setAnchor((a) => stepAnchor(view, a, direction, weekLength))
  const goToday = () => setAnchor(startOfDay(now))
  const newEvent = () => setEditing({ event: null })
  const pickView = (next: CalendarViewName) => {
    setView(next)
    rememberView(next)
  }
  const pickDay = (day: Date) => {
    setAnchor(startOfDay(day))
    setView('day')
  }

  useShortcuts([
    { keys: 'd', label: 'Day', group: 'Calendar', page: true, run: () => pickView('day') },
    { keys: 'w', label: 'Week', group: 'Calendar', page: true, run: () => pickView('week') },
    { keys: 'm', label: 'Month', group: 'Calendar', page: true, run: () => pickView('month') },
    { keys: 'a', label: 'Schedule', group: 'Calendar', page: true, run: () => pickView('schedule') },
    { keys: 't', label: 'Today', group: 'Calendar', page: true, run: goToday },
    { keys: 'j', label: 'Next', group: 'Calendar', page: true, run: () => step(1) },
    { keys: 'arrowright', label: 'Next', group: 'Calendar', page: true, hidden: true, run: () => step(1) },
    { keys: 'k', label: 'Previous', group: 'Calendar', page: true, run: () => step(-1) },
    { keys: 'arrowleft', label: 'Previous', group: 'Calendar', page: true, hidden: true, run: () => step(-1) },
    ...(canManage ? [{ keys: 'c', label: 'New event', group: 'Calendar' as const, page: true, run: newEvent }] : []),
    { label: 'Refresh', group: 'Calendar', page: true, run: () => void readVRChat(true) },
  ])

  const say = useCallback((next: Omit<Toast, 'id'>) => setToast({ ...next, id: ++toastCount.current }), [])
  const closeToast = useCallback(() => setToast(null), [])

  /** Puts an event back as it was before a drag, through the same update. */
  const undo = (before: CalendarEvent) => {
    calendarApi
      .update(before.id, inputFrom(before))
      .then(() => load())
      .catch((e: unknown) => say({ tone: 'problem', text: e instanceof ApiError ? e.message : 'Could not undo the move.' }))
  }

  /** Saves one drag: one request, when the pointer lets go. */
  const save = async (entry: Entry, change: Change) => {
    const before = entry.event
    setMoving({
      eventId: before.id,
      startBy: change.start.getTime() - entry.start.getTime(),
      endBy: change.end.getTime() - entry.end.getTime(),
    })

    try {
      await calendarApi.update(before.id, movedInput(inputFrom(before), entry, change))
      await load()
      say({ tone: 'done', text: change.kind === 'move' ? 'Event moved' : 'Event changed', undo: () => undo(before) })
    } catch (e: unknown) {
      say({ tone: 'problem', text: e instanceof ApiError ? e.message : 'Could not move the event.' })
    } finally {
      setMoving(null)
    }
  }

  /** Puts one date back as it was before a drag, through the same change. */
  const undoDate = (eventId: string, before: CalendarOccurrence) => {
    calendarApi
      .changeDate(eventId, {
        plannedStartsAt: before.plannedStartsAt,
        startsAt: before.startsAt,
        endsAt: before.endsAt,
        title: before.title ?? null,
        description: before.description ?? null,
      })
      .then(() => load())
      .catch((e: unknown) => say({ tone: 'problem', text: e instanceof ApiError ? e.message : 'Could not undo the move.' }))
  }

  /** Saves one drag of one date of a repeating event, leaving its other dates where they are. */
  const saveDate = async (entry: Entry, change: Change) => {
    const before = dateAt(entry.event, entry.start)?.occurrence
    if (!before) return

    setMoving({
      eventId: entry.event.id,
      date: entry.date,
      startBy: change.start.getTime() - entry.start.getTime(),
      endBy: change.end.getTime() - entry.end.getTime(),
    })

    try {
      await calendarApi.changeDate(entry.event.id, {
        plannedStartsAt: entry.date,
        startsAt: change.start.toISOString(),
        endsAt: change.end.toISOString(),
        title: before.title ?? null,
        description: before.description ?? null,
      })
      await load()
      say({
        tone: 'done',
        text: change.kind === 'move' ? 'Date moved' : 'Date changed',
        undo: () => undoDate(entry.event.id, before),
      })
    } catch (e: unknown) {
      say({ tone: 'problem', text: e instanceof ApiError ? e.message : 'Could not move the date.' })
    } finally {
      setMoving(null)
    }
  }

  const onChange = (entry: Entry, change: Change) => {
    setDetail(null)
    setQuick(null)

    // A repeating event asks first: this one date, or every date (calendar design §2.2). Only the
    // dragged date is drawn at its new place while it asks.
    if (entry.event.repeat !== 'none') {
      setMoving({
        eventId: entry.event.id,
        date: entry.date,
        startBy: change.start.getTime() - entry.start.getTime(),
        endBy: change.end.getTime() - entry.end.getTime(),
      })
      setAsking({ entry, change })
      return
    }

    void save(entry, change)
  }

  const openEntry = (entry: Entry, spot: Spot) => {
    setQuick(null)
    setDetail({ id: entry.event.id, start: entry.start, end: entry.end, spot })
  }

  /** A row of Past events: that time of the event, read on its own when it is off the dates on screen. */
  const openPast = (result: CalendarOccurrenceResult) => {
    setQuick(null)
    const show = () =>
      setDetail({ id: result.eventId, start: new Date(result.startsAt), end: new Date(result.endsAt), spot: null })

    if (data?.view.events.some((e) => e.id === result.eventId)) {
      show()
      return
    }

    calendarApi
      .event(result.eventId)
      .then((e) => {
        setLinked(e)
        show()
      })
      .catch(() => {
        // Deleted since the list was read: nothing to open.
      })
  }

  const openCreate = (created: { start: Date; end: Date }, spot: Spot) => {
    setDetail(null)
    setQuick({ ...created, spot })
  }

  // Closing the form takes `?new` out of the address, so going back to the page does not open it again.
  const closeForm = () => {
    setEditing(null)
    if (!wantsNew) return

    const params = new URLSearchParams(window.location.search)
    params.delete('new')
    const query = params.toString()
    go(window.location.pathname + (query ? `?${query}` : ''), { replace: true })
  }

  const drafts = useMemo(() => (data?.view.events ?? []).filter((e) => e.state === 'draft'), [data])

  // An event linked to may run outside the days on screen, so the one that was fetched stands in.
  const opened = detail
    ? (data?.view.events.find((e) => e.id === detail.id) ?? (linked?.id === detail.id ? linked : null))
    : null

  if (!data) return <PageMessage tone={error ? 'danger' : 'loading'} onTryAgain={load}>{error}</PageMessage>

  const form = editing ?? (wantsNew && canManage ? { event: null } : null)
  const ready = data.key === rangeKey

  return (
    <div className="flex flex-col gap-3">
      <div className="flex flex-wrap items-center gap-2">
        <Button variant="outline" onClick={goToday}>
          Today
        </Button>
        <div className="flex items-center">
          <Button variant="ghost" size="icon" aria-label="Previous" onClick={() => step(-1)}>
            <ChevronLeft />
          </Button>
          <Button variant="ghost" size="icon" aria-label="Next" onClick={() => step(1)}>
            <ChevronRight />
          </Button>
        </div>
        <h2 className="min-w-0 truncate font-label" style={{ fontSize: 'calc(var(--text-base) + 3px)' }}>
          {viewTitle(view, anchor, undefined, weekLength)}
        </h2>
        <div className="flex-1" />
        <SwitchBank value={view} onChange={pickView} options={VIEWS} label="View" />
        <Button variant="outline" onClick={() => void readVRChat(true)} disabled={reading > 0}>
          <RefreshCw className={reading > 0 ? 'animate-spin' : undefined} /> Refresh
        </Button>
        {canManage && <Button onClick={newEvent}>New event</Button>}
      </div>

      {error && (
        <p className="text-destructive" style={{ fontSize: 'var(--text-small)' }}>
          {error}
        </p>
      )}

      {readProblem && (
        <p className="text-destructive" style={{ fontSize: 'var(--text-small)' }}>
          {readProblem}
        </p>
      )}

      <div className="flex items-start gap-3">
        <Card className="hidden w-[max(16rem,calc(var(--control-h)*7+1.5rem))] shrink-0 p-(--panel-pad) xl:flex">
          <MiniMonth anchor={anchor} shown={view === 'schedule' ? [anchor] : days} today={now} onPick={setAnchor} />
        </Card>

        <div className="min-w-0 flex-1">
          {view === 'day' || view === 'week' ? (
            <TimeGrid
              days={days}
              entries={entries}
              now={now}
              canManage={canManage}
              ready={ready}
              scrollKey={`${view}|${rangeKey}`}
              ghost={quick}
              onOpen={openEntry}
              onCreate={openCreate}
              onChange={onChange}
              onPickDay={pickDay}
            />
          ) : view === 'month' ? (
            <MonthView
              days={days}
              month={anchor}
              entries={entries}
              now={now}
              canManage={canManage}
              onOpen={openEntry}
              onCreate={openCreate}
              onChange={onChange}
              onPickDay={pickDay}
            />
          ) : (
            <ScheduleView from={anchor} entries={entries} now={now} onOpen={openEntry} />
          )}
        </div>
      </div>

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
                onClick={(e) =>
                  setDetail({ id: d.id, start: new Date(d.startsAt), end: new Date(d.endsAt), spot: spotOf(e.currentTarget) })
                }
              >
                {d.title}
              </button>
            ))}
          </div>
        </Card>
      )}

      {data.view.canSeeResults && <PastEvents live={live} onOpen={openPast} />}

      {canManage && <FeedRow />}

      {opened && detail && (
        <EventDetails
          key={`${detail.id}|${detail.start.toISOString()}`}
          event={opened}
          start={detail.start}
          end={detail.end}
          spot={detail.spot}
          results={!!data.view.canSeeResults && hasRun(opened, detail.start, now) && !dateAt(opened, detail.start)?.cancelled}
          live={live}
          ready={data.view.ready}
          now={now}
          canManage={canManage}
          onClose={() => setDetail(null)}
          onEdit={() => {
            setEditing({ event: opened })
            setDetail(null)
          }}
          onEditDate={(occurrence) => {
            setEditingDate({ event: opened, occurrence })
            setDetail(null)
          }}
          onDuplicate={() => {
            setEditing({ event: null, initial: { ...inputFrom(opened), draft: false } })
            setDetail(null)
          }}
          onChanged={() => void load()}
        />
      )}

      {quick && (
        <QuickCreate
          key={`${quick.start.toISOString()}|${quick.end.toISOString()}`}
          start={quick.start}
          end={quick.end}
          spot={quick.spot}
          onClose={() => setQuick(null)}
          onMore={(input) => {
            setQuick(null)
            setEditing({ event: null, initial: input })
          }}
          onSaved={() => {
            setQuick(null)
            void load()
          }}
        />
      )}

      <Dialog
        open={asking !== null}
        onOpenChange={(open) => {
          if (open) return
          setAsking(null)
          setMoving(null)
        }}
      >
        {asking && (
          <DialogContent
            title={`${asking.change.kind === 'move' ? 'Move' : 'Change'} “${asking.entry.title}”?`}
            className="max-w-[480px]"
            foot={
              <DialogFoot>
                <Button
                  size="sm"
                  variant="ghost"
                  onClick={() => {
                    setAsking(null)
                    setMoving(null)
                  }}
                >
                  Cancel
                </Button>
                <Button
                  size="sm"
                  variant="outline"
                  onClick={() => {
                    const { entry, change } = asking
                    setAsking(null)
                    void saveDate(entry, change)
                  }}
                >
                  This date
                </Button>
                <Button
                  size="sm"
                  onClick={() => {
                    const { entry, change } = asking
                    setAsking(null)
                    void save(entry, change)
                  }}
                >
                  All dates
                </Button>
              </DialogFoot>
            }
          >
            <div className="flex flex-col gap-3" style={{ fontSize: 'var(--text-small)' }}>
              <p className="font-mono">
                {shortDay.format(asking.entry.start)}, {timeOfDay(asking.entry.start.toISOString())} –{' '}
                {timeOfDay(asking.entry.end.toISOString())}
                {' → '}
                {sameDay(asking.entry.start, asking.change.start) ? '' : `${shortDay.format(asking.change.start)}, `}
                {timeOfDay(asking.change.start.toISOString())} – {timeOfDay(asking.change.end.toISOString())}
              </p>
            </div>
          </DialogContent>
        )}
      </Dialog>

      {form && (
        <CalendarEventForm
          event={form.event}
          initial={'initial' in form ? form.initial : undefined}
          categories={data.view.categories}
          platforms={data.view.platforms}
          ready={data.view.ready}
          pictureUploads={data.view.pictureUploads}
          onClose={closeForm}
          onSaved={(saved) => {
            closeForm()
            void load()
            setDetail({
              id: saved.id,
              start: new Date(saved.occurrenceStartsAt ?? saved.startsAt),
              end: new Date(saved.occurrenceEndsAt ?? saved.endsAt),
              spot: null,
            })
          }}
        />
      )}

      {editingDate && (
        <DateForm
          event={editingDate.event}
          occurrence={editingDate.occurrence}
          onClose={() => setEditingDate(null)}
          onSaved={(saved) => {
            const id = editingDate.event.id
            setEditingDate(null)
            void load()
            setDetail({ id, start: saved.startsAt, end: saved.endsAt, spot: null })
          }}
        />
      )}

      {toast && <UndoToast toast={toast} onClose={closeToast} />}
    </div>
  )
}

/**
 * The time it is now, by the server's clock rather than the browser's (which is routinely wrong
 * on a machine that has been asleep), moved on every half minute for the red line.
 */
function useNow(skew: number): Date {
  const [tick, setTick] = useState(() => Date.now())

  useEffect(() => {
    const timer = window.setInterval(() => setTick(Date.now()), 30_000)
    return () => window.clearInterval(timer)
  }, [])

  return new Date(tick + skew)
}

function FeedRow() {
  const [feed, setFeed] = useState<CalendarFeed | null>(null)
  const [busy, setBusy] = useState(false)
  const [copied, setCopied] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [asking, setAsking] = useState(false)

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
          <Button size="sm" variant="outline" disabled={busy} onClick={() => setAsking(true)}>
            New link
          </Button>
        </>
      ) : (
        <Button size="sm" variant="outline" disabled={busy || !feed} onClick={regenerate}>
          Make link
        </Button>
      )}
      {error && <span className="text-destructive">{error}</span>}

      {/* Asks first: a new link breaks the old one for everyone who added it. The explanation in
          it is there because the maintainer asked for it on 2026-10-01 (calendar design §6). */}
      <ConfirmDialog
        open={asking}
        onOpenChange={setAsking}
        title="Make a new feed link?"
        subtitle="The old link stops working. Everyone who added it to their calendar stops getting updates and has to add the new link."
        action="Make new link"
        failed="Could not make a new link."
        onConfirm={() => calendarApi.regenerateFeed().then(setFeed)}
      />
    </div>
  )
}
