import { useCallback, useEffect, useState } from 'react'
import { ConfirmDialog } from '@/components/ConfirmDialog'
import { Button } from '@/components/ui/button'
import { Card, CardHeader, CardTitle } from '@/components/ui/card'
import { blankEvent, type CalendarEventInput } from '@/lib/calendar'
import { calendarTemplateApi } from '@/lib/calendarTemplateApi'
import {
  FIRST_LENGTH_MINUTES,
  inputFromTemplate,
  lengthWords,
  type CalendarTemplate,
  type CalendarTemplates,
} from '@/lib/calendarTemplates'
import type { CalendarReady } from '@/lib/calendarPlaces'
import { CalendarEventForm } from './CalendarEventForm'

/** The form's own settings, from the calendar's read. */
type FormSettings = {
  categories: string[]
  platforms: string[]
  ready?: CalendarReady | null
  pictureUploads?: boolean
}

/**
 * The event templates (calendar templates plan, step 2): a list for anyone who may use them, and
 * New template, Edit and Delete for those who hold Manage templates.
 */
export function TemplatesCard({ settings }: { settings: FormSettings }) {
  const [answer, setAnswer] = useState<CalendarTemplates | null>(null)
  const [editing, setEditing] = useState<{ template: CalendarTemplate | null; initial: CalendarEventInput } | null>(null)
  const [deleting, setDeleting] = useState<CalendarTemplate | null>(null)

  const load = useCallback(() => {
    calendarTemplateApi
      .all()
      .then(setAnswer)
      .catch(() => setAnswer(null))
  }, [])

  useEffect(() => {
    load()
  }, [load])

  if (!answer || (answer.templates.length === 0 && !answer.canManage)) return null

  return (
    <Card>
      <CardHeader>
        <CardTitle>Templates</CardTitle>
        {answer.canManage && (
          <Button
            size="sm"
            variant="outline"
            className="ml-auto"
            onClick={() => setEditing({ template: null, initial: blankEvent(new Date()) })}
          >
            New template
          </Button>
        )}
      </CardHeader>
      {answer.templates.length > 0 && (
        <div className="flex flex-col divide-y-(--hairline) divide-border">
          {answer.templates.map((t) => (
            <div
              key={t.id}
              className="flex min-h-(--row-h) flex-wrap items-center gap-x-3 gap-y-1 px-(--panel-pad) py-1"
              style={{ fontSize: 'var(--text-small)' }}
            >
              <span className="min-w-0 flex-1 truncate">
                {t.name}
                {t.fields.title && <span className="text-muted-foreground"> · {t.fields.title}</span>}
              </span>
              <span className="font-mono text-muted-foreground">{lengthWords(t.fields.lengthMinutes)}</span>
              {answer.canManage && (
                <>
                  <Button
                    size="sm"
                    variant="outline"
                    onClick={() => setEditing({ template: t, initial: inputFromTemplate(t.fields, blankEvent(new Date())) })}
                  >
                    Edit
                  </Button>
                  <Button size="sm" variant="outline" onClick={() => setDeleting(t)}>
                    Delete
                  </Button>
                </>
              )}
            </div>
          ))}
        </div>
      )}

      {editing && (
        <CalendarEventForm
          key={editing.template?.id ?? 'new'}
          event={null}
          initial={editing.initial}
          categories={settings.categories}
          platforms={settings.platforms}
          ready={settings.ready}
          pictureUploads={settings.pictureUploads}
          template={{
            id: editing.template?.id ?? null,
            name: editing.template?.name ?? '',
            lengthMinutes: editing.template?.fields.lengthMinutes ?? FIRST_LENGTH_MINUTES,
          }}
          onClose={() => setEditing(null)}
          onTemplateSaved={() => {
            setEditing(null)
            load()
          }}
        />
      )}

      <ConfirmDialog
        open={deleting !== null}
        onOpenChange={(open) => !open && setDeleting(null)}
        title={`Delete “${deleting?.name ?? ''}”?`}
        action="Delete"
        failed="Could not delete the template."
        onConfirm={() => (deleting ? calendarTemplateApi.remove(deleting.id) : Promise.resolve())}
        onDone={() => {
          setDeleting(null)
          load()
        }}
      />
    </Card>
  )
}
