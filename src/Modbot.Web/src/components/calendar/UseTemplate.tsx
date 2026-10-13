import { useEffect, useMemo, useState } from 'react'
import { Field, Outcome } from '@/components/settings/fields'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogFoot } from '@/components/ui/dialog'
import { Select } from '@/components/ui/select'
import { blankEvent, type CalendarEventInput } from '@/lib/calendar'
import { fillInFields, fillInsOfTemplate, unfilled } from '@/lib/calendarFillIns'
import { calendarTemplateApi } from '@/lib/calendarTemplateApi'
import { newEventFromTemplate, type CalendarTemplate } from '@/lib/calendarTemplates'

/**
 * "Use template" beside New event (calendar templates plan, step 3): pick a template, give a value
 * for each fill-in it has, and the new event form opens filled in, with the dates still to choose.
 *
 * Shown only while there is a template to use. A fill-in left empty is refused with the names of the
 * empty ones, so no event starts with a hole in its title.
 */
export function UseTemplate({
  version,
  onUse,
}: {
  /** Changes whenever a template was made, changed or deleted on the page, so the list is read again. */
  version: number
  onUse: (input: CalendarEventInput) => void
}) {
  const [templates, setTemplates] = useState<CalendarTemplate[]>([])
  const [open, setOpen] = useState(false)
  const [pickedId, setPickedId] = useState('')
  const [typed, setTyped] = useState<Record<string, string>>({})
  const [problem, setProblem] = useState<string | null>(null)

  useEffect(() => {
    calendarTemplateApi
      .all()
      .then((answer) => setTemplates(answer.templates))
      .catch(() => setTemplates([]))
  }, [version])

  const picked = templates.find((t) => t.id === pickedId) ?? templates[0] ?? null
  const fillIns = useMemo(() => (picked ? fillInsOfTemplate(picked.fields) : []), [picked])

  if (templates.length === 0) return null

  const pick = (id: string) => {
    setPickedId(id)
    setTyped({})
    setProblem(null)
  }

  const use = () => {
    if (!picked) return

    const empty = unfilled(fillIns, typed)
    if (empty.length > 0) {
      setProblem(`Fill in ${empty.join(', ')}.`)
      return
    }

    setOpen(false)
    onUse(newEventFromTemplate(fillInFields(picked.fields, typed), blankEvent(new Date())))
  }

  return (
    <>
      <Button
        variant="outline"
        onClick={() => {
          pick(templates[0].id)
          setOpen(true)
        }}
      >
        Use template
      </Button>

      <Dialog open={open} onOpenChange={setOpen}>
        <DialogContent
          title="Use template"
          className="max-w-[480px]"
          foot={
            <DialogFoot>
              {problem && (
                <span role="alert" className="mr-auto min-w-0 basis-full sm:basis-auto">
                  <Outcome tone="problem">{problem}</Outcome>
                </span>
              )}
              <Button size="sm" variant="ghost" onClick={() => setOpen(false)}>
                Cancel
              </Button>
              <Button size="sm" onClick={use}>
                Use
              </Button>
            </DialogFoot>
          }
        >
          <div className="flex flex-col gap-3">
            <label className="flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
              <span className="text-muted-foreground">Template</span>
              <Select value={picked?.id ?? ''} onChange={pick} aria-label="Template">
                {templates.map((t) => (
                  <option key={t.id} value={t.id}>
                    {t.name}
                  </option>
                ))}
              </Select>
            </label>

            {fillIns.map((name) => (
              <Field
                key={`${picked?.id}|${name}`}
                label={name}
                value={typed[name] ?? ''}
                onChange={(v) => {
                  setTyped((current) => ({ ...current, [name]: v }))
                  setProblem(null)
                }}
              />
            ))}
          </div>
        </DialogContent>
      </Dialog>
    </>
  )
}
