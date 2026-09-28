import { useState, type ReactNode } from 'react'
import { Popover } from 'radix-ui'
import { X } from 'lucide-react'
import { ConfirmDialog } from '@/components/ConfirmDialog'
import { VRChatPermissionMissing } from '@/components/VRChatPermissionMissing'
import { WorldLink } from '@/components/facts'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogFoot } from '@/components/ui/dialog'
import { calendarApi, PLACE_LABEL, PLACE_STATE_LABEL, STATE_LABEL, type CalendarEvent } from '@/lib/calendar'
import { sameDay } from '@/lib/calendarGrid'
import { timeOfDay } from '@/lib/format'
import { openInstance } from '@/lib/subject'
import type { Spot } from './entry'
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
 * One event, as it opens from the calendar: when, the world, the instance, and where it is published
 * and how that went (with VRChat's or Discord's own words when it failed). Its buttons come last.
 */
function EventBody({ event, start, end, children }: { event: CalendarEvent; start: Date; end: Date; children?: ReactNode }) {
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

      {event.places.length > 0 && (
        <div className="flex flex-col gap-1">
          {event.places.map((p) => (
            <div key={p.place} className="flex flex-wrap items-center gap-2">
              <PlaceBadge place={p.place} state={p.state} />
              {p.missingGroupPermission ? (
                <VRChatPermissionMissing missing={p.missingGroupPermission} className="text-destructive" />
              ) : (
                p.error && <span className="text-destructive">{p.error}</span>
              )}
            </div>
          ))}
        </div>
      )}

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
  ...actions
}: Actions & { event: CalendarEvent; start: Date; end: Date; spot: Spot | null }) {
  const [confirm, setConfirm] = useState<'cancel' | 'delete' | null>(null)
  const sheet = useMedia(SHEET)
  const buttons = actions.canManage && (
    <EventButtons event={event} onEdit={actions.onEdit} onDuplicate={actions.onDuplicate} onAsk={setConfirm} />
  )
  const body = (
    <EventBody event={event} start={start} end={end}>
      {buttons && <div className="flex flex-wrap gap-2 pt-1">{buttons}</div>}
    </EventBody>
  )

  // Beside the popover rather than inside it, so the confirmation is not taken for a click outside
  // the popover that closes both.
  const confirmation = (
    <ConfirmDialog
      open={confirm !== null}
      onOpenChange={(open) => !open && setConfirm(null)}
      title={confirm === 'cancel' ? `Cancel “${event.title}”?` : `Delete “${event.title}”?`}
      action={confirm === 'cancel' ? 'Cancel event' : 'Delete'}
      failed={confirm === 'cancel' ? 'Could not cancel the event.' : 'Could not delete the event.'}
      onConfirm={() => (confirm === 'cancel' ? calendarApi.cancel(event.id) : calendarApi.remove(event.id))}
      onDone={() => {
        actions.onChanged()
        actions.onClose()
      }}
    />
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
            <EventBody event={event} start={start} end={end} />
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
