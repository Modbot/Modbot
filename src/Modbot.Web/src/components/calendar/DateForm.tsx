import { useState } from 'react'
import { Field, LongField, Outcome } from '@/components/settings/fields'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { ApiError } from '@/lib/api'
import { calendarApi, localInputValue, type CalendarEvent, type CalendarOccurrence } from '@/lib/calendar'

/**
 * The form for one date of a repeating event (calendar design §2.2): its own times, title and
 * description, leaving every other date as it is. Times are the browser's own, as everywhere on the
 * page; the server keeps the date by the planned start it names.
 *
 * A title or description left as the event's own is sent as the event's, so the date keeps
 * following the series when the series is reworded.
 */
export function DateForm({
  event,
  occurrence,
  onClose,
  onSaved,
}: {
  event: CalendarEvent
  occurrence: CalendarOccurrence
  onClose: () => void
  onSaved: (saved: { startsAt: Date; endsAt: Date }) => void
}) {
  const [title, setTitle] = useState(occurrence.title || event.title)
  const [description, setDescription] = useState(occurrence.description || event.description)
  const [startsAt, setStartsAt] = useState(() => localInputValue(new Date(occurrence.startsAt)))
  const [endsAt, setEndsAt] = useState(() => localInputValue(new Date(occurrence.endsAt)))
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const save = () => {
    const starts = new Date(startsAt)
    const ends = new Date(endsAt)

    if (Number.isNaN(starts.getTime()) || Number.isNaN(ends.getTime())) {
      setError('The start and end need a date and time.')
      return
    }

    setBusy(true)
    setError(null)
    calendarApi
      .changeDate(event.id, {
        plannedStartsAt: occurrence.plannedStartsAt,
        startsAt: starts.toISOString(),
        endsAt: ends.toISOString(),
        title: title.trim() === event.title ? null : title.trim() || null,
        description: description.trim() === event.description ? null : description.trim() || null,
      })
      .then(() => onSaved({ startsAt: starts, endsAt: ends }))
      .catch((e: unknown) => setError(e instanceof ApiError ? e.message : 'Could not save the date.'))
      .finally(() => setBusy(false))
  }

  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent title="Edit date" className="max-w-[560px]" bodyClassName="max-h-[75vh] overflow-y-auto">
        <div className="flex flex-col gap-4">
          <Field label="Title" value={title} maxLength={100} onChange={setTitle} />
          <LongField label="Description" value={description} placeholder="" onChange={setDescription} />

          <div className="grid gap-3 sm:grid-cols-2">
            <label className="flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
              <span className="text-muted-foreground">Starts</span>
              <Input type="datetime-local" value={startsAt} onChange={(e) => setStartsAt(e.target.value)} />
            </label>
            <label className="flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
              <span className="text-muted-foreground">Ends</span>
              <Input type="datetime-local" value={endsAt} onChange={(e) => setEndsAt(e.target.value)} />
            </label>
          </div>

          <Outcome tone="problem">{error}</Outcome>

          <div className="flex flex-wrap justify-end gap-2">
            <Button size="sm" variant="outline" disabled={busy} onClick={onClose}>
              Cancel
            </Button>
            <Button size="sm" disabled={busy} onClick={save}>
              Save
            </Button>
          </div>
        </div>
      </DialogContent>
    </Dialog>
  )
}
