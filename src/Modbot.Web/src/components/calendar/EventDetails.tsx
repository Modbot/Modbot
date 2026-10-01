import { useState, type ReactNode } from 'react'
import { Popover } from 'radix-ui'
import { X } from 'lucide-react'
import { ConfirmDialog } from '@/components/ConfirmDialog'
import { VRChatPermissionMissing } from '@/components/VRChatPermissionMissing'
import { WorldLink } from '@/components/facts'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogFoot } from '@/components/ui/dialog'
import { Checkbox, Outcome } from '@/components/settings/fields'
import { ApiError } from '@/lib/api'
import {
  calendarApi,
  canOpenNow,
  PLACE_LABEL,
  PLACE_STATE_LABEL,
  STATE_LABEL,
  worldAt,
  type CalendarEvent,
  type CalendarOccurrence,
  type CalendarInvites,
} from '@/lib/calendar'
import { worldPickApi } from '@/lib/worldLists'
import { sameDay } from '@/lib/calendarGrid'
import { DESTINATION_LABEL, notSetUp, type CalendarReady } from '@/lib/calendarPlaces'
import { timeOfDay } from '@/lib/format'
import { openInstance } from '@/lib/subject'
import { dateAt, type Spot } from './entry'
import { EventResults } from './EventResults'
import { NextGame } from './NextGame'
import { NotSetUp } from './NotSetUp'
import { SHEET, useMedia } from './phone'

const longDay = new Intl.DateTimeFormat(undefined, { weekday: 'long', month: 'long', day: 'numeric' })

/**
 * The event's state, and where it was made when that was VRChat (calendar design §12). A date
 * cancelled on its own (`dateCancelled`) reads as cancelled whatever the event's state.
 */
export function StateBadge({ event, dateCancelled = false }: { event: CalendarEvent; dateCancelled?: boolean }) {
  const state = dateCancelled ? 'cancelled' : event.state

  return (
    <span className="inline-flex flex-wrap gap-1">
      <Badge variant={state === 'open' ? 'ok' : state === 'cancelled' ? 'destructive' : state === 'draft' ? 'outline' : 'secondary'}>
        {STATE_LABEL[state] ?? state}
      </Badge>
      {event.madeOnVRChat && <Badge variant="outline">VRChat</Badge>}
    </span>
  )
}

export function PlaceBadge({
  place,
  state,
}: {
  place: CalendarEvent['places'][number]['place']
  state: CalendarEvent['places'][number]['state']
}) {
  return (
    <Badge variant={state === 'failed' ? 'destructive' : state === 'published' ? 'secondary' : 'outline'}>
      {PLACE_LABEL[place] ?? place}: {PLACE_STATE_LABEL[state] ?? state}
    </Badge>
  )
}

/** "Sunday, September 27 · 8:00 – 10:00 PM", or both dates when it runs past midnight. */
function when(start: Date, end: Date): string {
  if (sameDay(start, new Date(end.getTime() - 1)))
    return `${longDay.format(start)} · ${timeOfDay(start.toISOString())} – ${timeOfDay(end.toISOString())}`
  return `${longDay.format(start)}, ${timeOfDay(start.toISOString())} – ${longDay.format(end)}, ${timeOfDay(end.toISOString())}`
}

type Actions = {
  canManage: boolean
  onEdit: () => void
  /** "This date" chosen for Edit: the form for this one date of a repeating event. */
  onEditDate: (occurrence: CalendarOccurrence) => void
  onDuplicate: () => void
  /** After a cancel, a delete, a Pick again or a place's Try again went through. */
  onChanged: () => void
  onClose: () => void
}

/**
 * One event, as it opens from the calendar: when, the world, the instance, where it is published
 * and how that went (with VRChat's or Discord's own words when it failed), and, once the time
 * clicked has started, what it did (`results`). Its buttons come last.
 */
function EventBody({
  event,
  start,
  end,
  ready,
  occurrence,
  results,
  canManage = false,
  onChanged,
  children,
}: {
  event: CalendarEvent
  start: Date
  end: Date
  ready?: CalendarReady | null
  /** The date clicked, with its own change when it has one. */
  occurrence: CalendarOccurrence | null
  results?: ReactNode
  canManage?: boolean
  onChanged?: () => void
  children?: ReactNode
}) {
  // A ticked place that cannot work as things are set up, while the event can still go anywhere.
  const pending = event.state === 'draft' || event.state === 'scheduled' || event.state === 'open'
  const missing = pending ? notSetUp(event, ready) : []
  const description = occurrence?.description || event.description
  const planned = occurrence ? new Date(occurrence.plannedStartsAt) : null
  const moved = planned !== null && planned.getTime() !== start.getTime()

  return (
    <div className="flex flex-col gap-3" style={{ fontSize: 'var(--text-small)' }}>
      <div className="font-mono">{when(start, end)}</div>

      {moved && (
        <div className="text-muted-foreground">
          Moved from {longDay.format(planned)}, {timeOfDay(planned.toISOString())}
        </div>
      )}

      {event.repeat !== 'none' && (
        <div className="text-muted-foreground">
          {{ daily: 'Every day', weekly: 'Every week', monthly: 'Every month' }[event.repeat]}
          {event.repeatUntil ? `, until ${event.repeatUntil}` : ''}
        </div>
      )}

      {description && <p className="line-clamp-6 whitespace-pre-wrap">{description}</p>}

      <WorldLine event={event} start={start} canManage={canManage} onChanged={onChanged} />

      {event.worldListId && event.state === 'open' && isCurrent(event, start) && <NextGame eventId={event.id} />}

      {event.opening && (
        <div>
          <span className="text-muted-foreground">Instance </span>
          {event.opening.error ? (
            <span className="text-destructive">{event.opening.error}</span>
          ) : event.opening.instanceId ? (
            <button type="button" className="hover:underline" onClick={() => openInstance(event.opening!.instanceId!)}>
              {event.opening.closed ? 'Closed' : 'Open'}
            </button>
          ) : event.opening.checking ? (
            <span>Checking…</span>
          ) : (
            <span>Opening</span>
          )}
          {event.opening.joinLink && (
            <a className="ml-2 underline" href={event.opening.joinLink} target="_blank" rel="noreferrer">
              Join
            </a>
          )}
          {event.opening.firstJoinDiscordPostError && (
            <div className="text-destructive">{event.opening.firstJoinDiscordPostError}</div>
          )}
          {event.opening.firstJoinVRChatPostError && (
            <div className="text-destructive">{event.opening.firstJoinVRChatPostError}</div>
          )}
        </div>
      )}

      {event.invites && <InviteCounts invites={event.invites} />}

      {(event.places.length > 0 || missing.length > 0) && (
        <div className="flex flex-col gap-1">
          {event.places
            .filter((p) => !missing.some((m) => m === p.place))
            .map((p) => (
              <div key={p.place} className="flex flex-wrap items-center gap-2">
                <PlaceBadge place={p.place} state={p.state} />
                {p.missingGroupPermission ? (
                  <VRChatPermissionMissing missing={p.missingGroupPermission} className="text-destructive" />
                ) : (
                  p.error && <span className="text-destructive">{p.error}</span>
                )}
                {p.canTryAgain && canManage && onChanged && <PlaceTryAgain event={event} onDone={onChanged} />}
              </div>
            ))}
          {missing.map((place) => (
            <div key={place} className="flex flex-wrap items-center gap-2">
              <Badge variant="destructive">{place === 'instance' ? DESTINATION_LABEL.instance : PLACE_LABEL[place]}</Badge>
              <NotSetUp place={place} />
            </div>
          ))}
          {occurrence?.vrChatError && (
            <div className="flex flex-wrap items-center gap-2">
              <PlaceBadge place="vrchat" state="failed" />
              <span className="text-destructive">{occurrence.vrChatError}</span>
            </div>
          )}
        </div>
      )}

      {results}

      {children}
    </div>
  )
}

/** Whether `start` is the date the event is on now: the one its world was picked for. */
function isCurrent(event: CalendarEvent, start: Date): boolean {
  return event.occurrenceStartsAt !== null && Date.parse(event.occurrenceStartsAt) === start.getTime()
}

/**
 * The world, or for an event that picks from a list, the world picked for the date it is on now and
 * the list (world lists design §5). Pick again, for an editor, while that date is scheduled.
 */
function WorldLine({
  event,
  start,
  canManage,
  onChanged,
}: {
  event: CalendarEvent
  start: Date
  canManage: boolean
  onChanged?: () => void
}) {
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const shown = worldAt(event, start)

  if (!shown) return null

  const again = () => {
    setBusy(true)
    setError(null)
    worldPickApi
      .pickAgain(event.id)
      .then(() => onChanged?.())
      .catch((e: unknown) => setError(e instanceof ApiError ? e.message : 'Could not pick again.'))
      .finally(() => setBusy(false))
  }

  return (
    <div className="flex flex-col gap-1">
      {'worldId' in shown && (
        <div>
          <span className="text-muted-foreground">World </span>
          <WorldLink id={shown.worldId} name={shown.worldName} unnamed="id" />
        </div>
      )}
      {event.worldListId && (
        <div className="flex flex-wrap items-center gap-x-2 gap-y-1">
          <span>
            <span className="text-muted-foreground">World list </span>
            {event.worldListName}
          </span>
          {event.worldListEmpty && <span className="text-destructive">List is empty</span>}
          {canManage && event.state === 'scheduled' && isCurrent(event, start) && (
            <Button size="sm" variant="outline" disabled={busy} onClick={again}>
              Pick again
            </Button>
          )}
        </div>
      )}
      {error && <span className="text-destructive">{error}</span>}
    </div>
  )
}

/**
 * "Try again" on the VRChat place, when VRChat gave no answer to adding the event and does not have
 * it: Modbot never sends that again on its own (calendar design §3.1).
 */
function PlaceTryAgain({ event, onDone }: { event: CalendarEvent; onDone: () => void }) {
  const [sending, setSending] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)

  const press = () => {
    setSending(true)
    setProblem(null)

    calendarApi
      .tryVRChatAgain(event.id)
      .then(onDone)
      .catch((e: unknown) => setProblem(e instanceof ApiError ? e.message : 'Could not try again.'))
      .finally(() => setSending(false))
  }

  return (
    <>
      <Button type="button" variant="outline" size="xs" disabled={sending} onClick={press}>
        Try again
      </Button>
      {problem && <span className="text-destructive">{problem}</span>}
    </>
  )
}

/** "Invited N of M", and how: by VRChat invite, by Discord message, and who could not be reached. */
function InviteCounts({ invites }: { invites: CalendarInvites }) {
  const parts: [string, number][] = [
    ['VRChat invite', invites.vrChat],
    ['Discord message', invites.discord],
    ["Couldn't reach", invites.couldNotReach],
    ['No way to reach', invites.noWay],
    ["Didn't ask for invites", invites.notAsked ?? 0],
  ]

  return (
    <div>
      <span className="text-muted-foreground">Invites </span>
      <span>
        Invited {invites.invited} of {invites.total}
      </span>
      <div className="flex flex-wrap gap-x-3 text-muted-foreground">
        {parts
          .filter(([, count]) => count > 0)
          .map(([label, count]) => (
            <span key={label}>
              {label} <span className="font-mono text-foreground">{count}</span>
            </span>
          ))}
      </div>
    </div>
  )
}

/** Opens the instance for the event's current or next time, once. */
function OpenNowButton({ event, onChanged }: { event: CalendarEvent; onChanged: () => void }) {
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const open = () => {
    setBusy(true)
    setError(null)
    calendarApi
      .openNow(event.id)
      .then(onChanged)
      .catch((e: unknown) => setError(e instanceof ApiError ? e.message : 'Could not open the instance.'))
      .finally(() => setBusy(false))
  }

  return (
    <>
      <Button size="sm" variant="outline" disabled={busy} onClick={open}>
        Open now
      </Button>
      {error && <span className="basis-full text-destructive">{error}</span>}
    </>
  )
}

/** Edit, Open now, Duplicate, Cancel event and Delete, for somebody who may change the event. */
function EventButtons({
  event,
  now,
  onEdit,
  onDuplicate,
  onChanged,
  onAsk,
}: Pick<Actions, 'onEdit' | 'onDuplicate' | 'onChanged'> & {
  event: CalendarEvent
  now: Date
  onAsk: (what: 'cancel' | 'delete') => void
}) {
  const live = event.state === 'scheduled' || event.state === 'open'

  return (
    <>
      {event.state !== 'cancelled' && (
        <Button size="sm" onClick={onEdit}>
          Edit
        </Button>
      )}
      {canOpenNow(event, now) && <OpenNowButton event={event} onChanged={onChanged} />}
      <Button size="sm" variant="outline" onClick={onDuplicate}>
        Duplicate
      </Button>
      {live && (
        <Button size="sm" variant="outline" onClick={() => onAsk('cancel')}>
          Cancel event
        </Button>
      )}
      <Button size="sm" variant="destructive" onClick={() => onAsk('delete')}>
        Delete
      </Button>
    </>
  )
}

/**
 * The cancel's confirmation. With a channel on the event it offers to post in that channel that the
 * event is cancelled, ticked when the event has a channel post (calendar design §14.4): an edit to
 * the old card notifies nobody. Closes only once the server has said yes, like `ConfirmDialog`.
 */
function CancelBody({ event, onClose, onDone }: { event: CalendarEvent; onClose: () => void; onDone: () => void }) {
  const channel = !!event.channelId
  const [post, setPost] = useState(channel && event.postToChannel)
  const [sending, setSending] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)

  const send = () => {
    setSending(true)
    setProblem(null)

    calendarApi
      .cancel(event.id, channel && post)
      .then(() => {
        onClose()
        onDone()
      })
      .catch((e: unknown) => setProblem(e instanceof ApiError ? e.message : 'Could not cancel the event.'))
      .finally(() => setSending(false))
  }

  return (
    <DialogContent
      title={`Cancel “${event.title}”?`}
      className="max-w-[460px]"
      foot={
        <DialogFoot>
          <Button size="sm" variant="outline" onClick={onClose} disabled={sending}>
            Cancel
          </Button>
          <Button size="sm" variant="destructive" onClick={send} disabled={sending}>
            {sending ? 'Sending…' : 'Cancel event'}
          </Button>
        </DialogFoot>
      }
    >
      {channel || problem ? (
        <div className="flex flex-col gap-3">
          {channel && (
            <Checkbox checked={post} onChange={setPost}>
              Post that it&apos;s cancelled in the channel
            </Checkbox>
          )}
          {problem && <Outcome tone="problem">{problem}</Outcome>}
        </div>
      ) : null}
    </DialogContent>
  )
}

function Heading({
  event,
  title,
  dateCancelled,
  children,
}: {
  event: CalendarEvent
  title: string
  dateCancelled: boolean
  children?: ReactNode
}) {
  return (
    <div className="flex items-start gap-2">
      <div className="flex min-w-0 flex-1 flex-col gap-1">
        <div className="font-label [overflow-wrap:anywhere]" style={{ fontSize: 'calc(var(--text-base) + 1px)' }}>
          {title}
        </div>
        <div>
          <StateBadge event={event} dateCancelled={dateCancelled} />
        </div>
      </div>
      {children}
    </div>
  )
}

/**
 * The event, next to where it was clicked, the way Google opens one. Without a spot to point at (a
 * link to `?event=`), it opens as a dialog in the middle instead.
 *
 * On a phone it rises from the bottom as a sheet with its buttons pinned under it, wherever it was
 * tapped: a popover 22rem wide has no room on either side of a tap there, and Radix can flip it to
 * the other side but not slide it along, so it hung off the left edge (mobile review 2026-09-28, #2).
 */
export function EventDetails({
  event,
  start,
  end,
  spot,
  results,
  live,
  ready,
  now,
  ...actions
}: Actions & {
  event: CalendarEvent
  start: Date
  end: Date
  spot: Spot | null
  /** Show what this time did: it has started, and the account may see analytics. */
  results: boolean
  live: number
  /** Which places are set up, from the calendar's own read. */
  ready?: CalendarReady | null
  /** The page's clock: a date still to come can be changed on its own, and Open now is offered by it. */
  now: Date
}) {
  const [confirm, setConfirm] = useState<'cancel' | 'delete' | null>(null)
  const [choosing, setChoosing] = useState<'edit' | 'cancel' | null>(null)
  const sheet = useMedia(SHEET)

  // The date clicked. A repeating event's Edit and Cancel ask "This date" or "All dates" while that
  // date is still to come and was not cancelled on its own (calendar design §2.2).
  const found = dateAt(event, start)
  const occurrence = found?.occurrence ?? null
  const dateCancelled = found?.cancelled ?? false
  const title = occurrence?.title || event.title
  const ownDate =
    event.repeat !== 'none' &&
    (event.state === 'scheduled' || event.state === 'open') &&
    occurrence !== null &&
    !dateCancelled &&
    end.getTime() > now.getTime()

  const buttons = actions.canManage && (
    <EventButtons
      event={event}
      now={now}
      onEdit={() => (ownDate ? setChoosing('edit') : actions.onEdit())}
      onDuplicate={actions.onDuplicate}
      onChanged={actions.onChanged}
      onAsk={(what) => (what === 'cancel' && ownDate ? setChoosing('cancel') : setConfirm(what))}
    />
  )
  const shown = results ? <EventResults event={event} start={start} live={live} /> : undefined
  const body = (
    <EventBody
      event={event}
      start={start}
      end={end}
      ready={ready}
      occurrence={occurrence}
      results={shown}
      canManage={actions.canManage}
      onChanged={actions.onChanged}
    >
      {buttons && <div className="flex flex-wrap gap-2 pt-1">{buttons}</div>}
    </EventBody>
  )

  const done = () => {
    actions.onChanged()
    actions.onClose()
  }

  // Beside the popover rather than inside it, so the confirmation is not taken for a click outside
  // the popover that closes both.
  const confirmation = (
    <>
      <ConfirmDialog
        open={confirm === 'delete'}
        onOpenChange={(open) => !open && setConfirm(null)}
        title={`Delete “${event.title}”?`}
        action="Delete"
        failed="Could not delete the event."
        onConfirm={() => calendarApi.remove(event.id)}
        onDone={done}
      />
      <Dialog open={confirm === 'cancel'} onOpenChange={(open) => !open && setConfirm(null)}>
        {confirm === 'cancel' && <CancelBody event={event} onClose={() => setConfirm(null)} onDone={done} />}
      </Dialog>
      {choosing && occurrence && (
        <DatesDialog
          title={choosing === 'cancel' ? `Cancel “${title}”?` : `Edit “${title}”?`}
          when={when(start, end)}
          destructive={choosing === 'cancel'}
          failed={choosing === 'cancel' ? 'Could not cancel the event.' : 'Could not open the event.'}
          channelTick={choosing === 'cancel' && !!event.channelId ? event.postToChannel : null}
          onClose={() => setChoosing(null)}
          onThisDate={(post) => {
            if (choosing === 'edit') {
              actions.onEditDate(occurrence)
              return null
            }
            return calendarApi.cancelDate(event.id, occurrence.plannedStartsAt, post).then(done)
          }}
          onAllDates={(post) => {
            if (choosing === 'edit') {
              actions.onEdit()
              return null
            }
            return calendarApi.cancel(event.id, post).then(done)
          }}
        />
      )}
    </>
  )

  if (sheet)
    return (
      <>
        <Dialog open onOpenChange={(open) => !open && actions.onClose()}>
          <DialogContent
            title={title}
            subtitle={<StateBadge event={event} dateCancelled={dateCancelled} />}
            foot={buttons && <DialogFoot>{buttons}</DialogFoot>}
          >
            <EventBody
              event={event}
              start={start}
              end={end}
              ready={ready}
              occurrence={occurrence}
              results={shown}
              canManage={actions.canManage}
              onChanged={actions.onChanged}
            />
          </DialogContent>
        </Dialog>
        {confirmation}
      </>
    )

  if (!spot)
    return (
      <>
        <Dialog open onOpenChange={(open) => !open && actions.onClose()}>
          <DialogContent
            title={title}
            subtitle={<StateBadge event={event} dateCancelled={dateCancelled} />}
            className="max-w-[560px]"
          >
            {body}
          </DialogContent>
        </Dialog>
        {confirmation}
      </>
    )

  const holdOpen = (e: Event) => {
    if (confirm !== null || choosing !== null) e.preventDefault()
  }

  return (
    <>
      <Popover.Root open onOpenChange={(open) => !open && actions.onClose()}>
        <Popover.Anchor asChild>
          <div aria-hidden className="pointer-events-none fixed" style={spot} />
        </Popover.Anchor>
        <Popover.Portal>
          <Popover.Content
            side="right"
            align="start"
            sideOffset={8}
            collisionPadding={8}
            aria-label={title}
            onInteractOutside={holdOpen}
            onEscapeKeyDown={holdOpen}
            className="z-40 flex max-h-[var(--radix-popover-content-available-height)] w-[22rem] max-w-[calc(100vw-1rem)] flex-col gap-3 overflow-y-auto rounded-sm border-(length:--hairline) bg-popover p-(--panel-pad) text-popover-foreground shadow-sm outline-none"
          >
            <Heading event={event} title={title} dateCancelled={dateCancelled}>
              <Popover.Close
                aria-label="Close"
                className="grid shrink-0 place-items-center rounded-sm text-muted-foreground hover:bg-muted hover:text-foreground"
                style={{ height: 'var(--control-h)', width: 'var(--control-h)' }}
              >
                <X className="size-4" />
              </Popover.Close>
            </Heading>
            {body}
          </Popover.Content>
        </Popover.Portal>
      </Popover.Root>
      {confirmation}
    </>
  )
}

/**
 * "This date" or "All dates", for Edit and Cancel on a repeating event. For a cancel, either button
 * is the confirmation itself; it stays open with the server's words when the cancel fails. A cancel
 * of an event with a channel offers the same tick as the one-off cancel (`CancelBody`), for either
 * choice (calendar design §14.4).
 */
function DatesDialog({
  title,
  when,
  destructive,
  failed,
  channelTick,
  onClose,
  onThisDate,
  onAllDates,
}: {
  title: string
  when: string
  destructive: boolean
  failed: string
  /** Where the channel-post tick starts, or null to show none. */
  channelTick: boolean | null
  onClose: () => void
  /** A request to wait for, or null when the choice only opens something. `post`: the tick. */
  onThisDate: (post: boolean) => Promise<unknown> | null
  onAllDates: (post: boolean) => Promise<unknown> | null
}) {
  const [sending, setSending] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)
  const [post, setPost] = useState(channelTick ?? false)

  const choose = (pick: (post: boolean) => Promise<unknown> | null) => {
    const request = pick(channelTick !== null && post)
    if (!request) {
      onClose()
      return
    }

    setSending(true)
    setProblem(null)
    request
      .then(onClose)
      .catch((e: unknown) => setProblem(e instanceof ApiError ? e.message : failed))
      .finally(() => setSending(false))
  }

  return (
    <Dialog open onOpenChange={(open) => !open && !sending && onClose()}>
      <DialogContent
        title={title}
        subtitle={<span className="font-mono">{when}</span>}
        className="max-w-[460px]"
        foot={
          <DialogFoot>
            <Button size="sm" variant="outline" disabled={sending} onClick={() => choose(onThisDate)}>
              This date
            </Button>
            <Button size="sm" variant={destructive ? 'destructive' : 'default'} disabled={sending} onClick={() => choose(onAllDates)}>
              All dates
            </Button>
          </DialogFoot>
        }
      >
        {channelTick !== null || problem ? (
          <div className="flex flex-col gap-3">
            {channelTick !== null && (
              <Checkbox checked={post} onChange={setPost}>
                Post that it&apos;s cancelled in the channel
              </Checkbox>
            )}
            {problem && <Outcome tone="problem">{problem}</Outcome>}
          </div>
        ) : null}
      </DialogContent>
    </Dialog>
  )
}
