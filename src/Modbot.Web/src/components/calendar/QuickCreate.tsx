import { useId, useState, type FormEvent } from 'react'
import { Popover } from 'radix-ui'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogFoot } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { ApiError } from '@/lib/api'
import { calendarApi, newEventAt, type CalendarEvent, type CalendarEventInput } from '@/lib/calendar'
import { sameDay } from '@/lib/calendarGrid'
import { timeOfDay } from '@/lib/format'
import type { Spot } from './entry'
import { SHEET, useMedia } from './phone'

const day = new Intl.DateTimeFormat(undefined, { weekday: 'long', month: 'long', day: 'numeric' })

/**
 * The small form that opens where a new event was drawn: a title, then **Save draft**, or **More
 * options** for the whole form with the time already in it.
 *
 * It saves a draft rather than scheduling, because a scheduled event goes out to VRChat and Discord
 * at once, and VRChat's calendar will not take one without a description. A draft goes nowhere
 * until it is scheduled from the full form.
 *
 * On a phone it rises from the bottom as a sheet, as an opened event does (EventDetails), since a
 * popover beside the tap has no room there.
 */
export function QuickCreate({
  start,
  end,
  spot,
  onClose,
  onMore,
  onSaved,
}: {
  start: Date
  end: Date
  spot: Spot
  onClose: () => void
  onMore: (input: CalendarEventInput) => void
  onSaved: (saved: CalendarEvent) => void
}) {
  const [title, setTitle] = useState('')
  const [busy, setBusy] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)

  const save = () => {
    setBusy(true)
    setProblem(null)
    calendarApi
      .create({ ...newEventAt(title, start, end), draft: true })
      .then(onSaved)
      .catch((e: unknown) => setProblem(e instanceof ApiError ? e.message : 'Could not save the event.'))
      .finally(() => setBusy(false))
  }

  const lastMinute = new Date(end.getTime() - 1)
  const sheet = useMedia(SHEET)
  const formId = useId()

  const submit = (e: FormEvent) => {
    e.preventDefault()
    if (title.trim()) save()
  }

  const titleField = (
    <Input autoFocus aria-label="Title" placeholder="Title" value={title} maxLength={100} onChange={(e) => setTitle(e.target.value)} />
  )
  const when = (
    <div className="font-mono text-muted-foreground">
      {day.format(start)} · {timeOfDay(start.toISOString())} –{' '}
      {sameDay(start, lastMinute) ? '' : `${day.format(end)}, `}
      {timeOfDay(end.toISOString())}
    </div>
  )
  const problemLine = problem && <div className="text-destructive">{problem}</div>
  const buttons = (
    <>
      <Button type="button" size="sm" variant="ghost" disabled={busy} onClick={() => onMore(newEventAt(title, start, end))}>
        More options
      </Button>
      <Button type="submit" form={formId} size="sm" disabled={busy || !title.trim()}>
        Save draft
      </Button>
    </>
  )

  if (sheet)
    return (
      <Dialog open onOpenChange={(open) => !open && onClose()}>
        <DialogContent
          title="New event"
          foot={<DialogFoot style={{ fontSize: 'var(--text-small)' }}>{buttons}</DialogFoot>}
        >
          {/* Save draft is pinned under the body, outside the form, and names it with `form`. */}
          <form id={formId} className="flex flex-col gap-3" style={{ fontSize: 'var(--text-small)' }} onSubmit={submit}>
            {titleField}
            {when}
            {problemLine}
          </form>
        </DialogContent>
      </Dialog>
    )

  return (
    <Popover.Root open onOpenChange={(open) => !open && onClose()}>
      <Popover.Anchor asChild>
        <div aria-hidden className="pointer-events-none fixed" style={spot} />
      </Popover.Anchor>
      <Popover.Portal>
        <Popover.Content
          side="right"
          align="start"
          sideOffset={8}
          collisionPadding={8}
          aria-label="New event"
          className="z-40 flex w-[22rem] max-w-[calc(100vw-1rem)] flex-col gap-3 rounded-sm border-(length:--hairline) bg-popover p-(--panel-pad) text-popover-foreground shadow-sm outline-none"
          style={{ fontSize: 'var(--text-small)' }}
        >
          <form id={formId} className="flex flex-col gap-3" onSubmit={submit}>
            {titleField}
            {when}
            {problemLine}
            <div className="flex flex-wrap justify-end gap-2">{buttons}</div>
          </form>
        </Popover.Content>
      </Popover.Portal>
    </Popover.Root>
  )
}
