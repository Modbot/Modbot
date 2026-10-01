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
import { calendarApi, PLACE_LABEL, PLACE_STATE_LABEL, STATE_LABEL, type CalendarEvent } from '@/lib/calendar'
import { sameDay } from '@/lib/calendarGrid'
import { DESTINATION_LABEL, notSetUp, type CalendarReady } from '@/lib/calendarPlaces'
import { timeOfDay } from '@/lib/format'
import { openInstance } from '@/lib/subject'
import type { Spot } from './entry'
import { EventResults } from './EventResults'
import { NotSetUp } from './NotSetUp'
import { SHEET, useMedia } from './phone'

const longDay = new Intl.DateTimeFormat(undefined, { weekday: 'long', month: 'long', day: 'numeric' })

/** The event's state, and where it was made when that was VRChat (calendar design §12). */
export function StateBadge({ event }: { event: CalendarEvent }) {
  return (
    <span className="inline-flex flex-wrap gap-1">
      <Badge variant={event.state === 'open' ? 'ok' : event.state === 'cancelled' ? 'destructive' : event.state === 'draft' ? 'outline' : 'secondary'}>
        {STATE_LABEL[event.state] ?? event.state}
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
  onDuplicate: () => void
  /** After a cancel or a delete went through. */
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
  results,
  children,
}: {
  event: CalendarEvent
  start: Date
  end: Date
  ready?: CalendarReady | null
  results?: ReactNode
  children?: ReactNode
}) {
  // A ticked place that cannot work as things are set up, while the event can still go anywhere.
  const pending = event.state === 'draft' || event.state === 'scheduled' || event.state === 'open'
  const missing = pending ? notSetUp(event, ready) : []

  return (
    <div className="flex flex-col gap-3" style={{ fontSize: 'var(--text-small)' }}>
      <div className="font-mono">{when(start, end)}</div>

      {event.repeat !== 'none' && (
        <div className="text-muted-foreground">
          {{ daily: 'Every day', weekly: 'Every week', monthly: 'Every month' }[event.repeat]}
          {event.repeatUntil ? `, until ${event.repeatUntil}` : ''}
        </div>
      )}

      {event.description && <p className="line-clamp-6 whitespace-pre-wrap">{event.description}</p>}

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
              </div>
            ))}
          {missing.map((place) => (
            <div key={place} className="flex flex-wrap items-center gap-2">
              <Badge variant="destructive">{place === 'instance' ? DESTINATION_LABEL.instance : PLACE_LABEL[place]}</Badge>
              <NotSetUp place={place} />
            </div>
          ))}
        </div>
      )}

      {results}

      {children}
    </div>
  )
}

/** Edit, Duplicate, Cancel event and Delete, for somebody who may change the event. */
function EventButtons({
  event,
  onEdit,
  onDuplicate,
  onAsk,
}: Pick<Actions, 'onEdit' | 'onDuplicate'> & { event: CalendarEvent; onAsk: (what: 'cancel' | 'delete') => void }) {
  const live = event.state === 'scheduled' || event.state === 'open'

  return (
    <>
      {event.state !== 'cancelled' && (
        <Button size="sm" onClick={onEdit}>
          Edit
        </Button>
      )}
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

function Heading({ event, children }: { event: CalendarEvent; children?: ReactNode }) {
  return (
    <div className="flex items-start gap-2">
      <div className="flex min-w-0 flex-1 flex-col gap-1">
        <div className="font-label [overflow-wrap:anywhere]" style={{ fontSize: 'calc(var(--text-base) + 1px)' }}>
          {event.title}
        </div>
        <div>
          <StateBadge event={event} />
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
}) {
  const [confirm, setConfirm] = useState<'cancel' | 'delete' | null>(null)
  const sheet = useMedia(SHEET)
  const buttons = actions.canManage && (
    <EventButtons event={event} onEdit={actions.onEdit} onDuplicate={actions.onDuplicate} onAsk={setConfirm} />
  )
  const shown = results ? <EventResults event={event} start={start} live={live} /> : undefined
  const body = (
    <EventBody event={event} start={start} end={end} ready={ready} results={shown}>
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
    </>
  )

  if (sheet)
    return (
      <>
        <Dialog open onOpenChange={(open) => !open && actions.onClose()}>
          <DialogContent
            title={event.title}
            subtitle={<StateBadge event={event} />}
            foot={buttons && <DialogFoot>{buttons}</DialogFoot>}
          >
            <EventBody event={event} start={start} end={end} ready={ready} results={shown} />
          </DialogContent>
        </Dialog>
        {confirmation}
      </>
    )

  if (!spot)
    return (
      <>
        <Dialog open onOpenChange={(open) => !open && actions.onClose()}>
          <DialogContent title={event.title} subtitle={<StateBadge event={event} />} className="max-w-[560px]">
            {body}
          </DialogContent>
        </Dialog>
        {confirmation}
      </>
    )

  const holdOpen = (e: Event) => {
    if (confirm !== null) e.preventDefault()
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
            aria-label={event.title}
            onInteractOutside={holdOpen}
            onEscapeKeyDown={holdOpen}
            className="z-40 flex max-h-[var(--radix-popover-content-available-height)] w-[22rem] max-w-[calc(100vw-1rem)] flex-col gap-3 overflow-y-auto rounded-sm border-(length:--hairline) bg-popover p-(--panel-pad) text-popover-foreground shadow-sm outline-none"
          >
            <Heading event={event}>
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
