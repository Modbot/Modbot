import { useCallback, useEffect, useState } from 'react'
import { ConfirmDialog } from '@/components/ConfirmDialog'
import { Field, Outcome } from '@/components/settings/fields'
import { Button } from '@/components/ui/button'
import { Card } from '@/components/ui/card'
import { Dialog, DialogContent, DialogFoot } from '@/components/ui/dialog'
import { ApiError } from '@/lib/api'
import { calendarCategoryApi, type CalendarCategories, type CalendarCategoryRow } from '@/lib/calendarCategories'
import { useShortcuts } from '@/lib/shortcuts'
import { PageMessage } from '@/pages/analytics/shared'

/** The longest a category's name may be; the server holds the same number. */
const MAX_NAME = 40

/**
 * Event categories: the ones admins make in Modbot, such as Karaoke or Movie night. They add to the
 * fixed list VRChat gives an event, and are picked on the event form.
 */
export function EventCategories() {
  const [data, setData] = useState<CalendarCategories | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [editing, setEditing] = useState<CalendarCategoryRow | 'new' | null>(null)
  const [deleting, setDeleting] = useState<CalendarCategoryRow | null>(null)

  useShortcuts(data?.canManage ? [{ label: 'New category', group: 'Page', page: true, run: () => setEditing('new') }] : [])

  const load = useCallback(() => {
    calendarCategoryApi
      .all()
      .then((answer) => {
        setData(answer)
        setError(null)
      })
      .catch((e: unknown) =>
        setError(
          e instanceof ApiError && e.status === 403
            ? 'You do not have permission to see this.'
            : e instanceof ApiError
              ? e.message
              : 'Could not load the categories.',
        ),
      )
  }, [])

  useEffect(() => {
    load()
  }, [load])

  if (!data) return <PageMessage tone={error ? 'danger' : 'loading'} onTryAgain={load}>{error}</PageMessage>

  return (
    <div className="flex flex-col gap-3">
      {data.canManage && (
        <div className="flex flex-wrap items-center gap-3">
          <div className="flex-1" />
          <Button onClick={() => setEditing('new')}>New category</Button>
        </div>
      )}

      {data.categories.length === 0 ? (
        <PageMessage>No categories.</PageMessage>
      ) : (
        <Card className="flex flex-col">
          <ul className="divide-y-(--hairline) divide-border">
            {data.categories.map((category) => (
              <li key={category.id} className="flex min-h-(--row-h) flex-wrap items-center gap-x-3 gap-y-1 px-(--panel-pad) py-1.5">
                <span className="min-w-0 flex-1 font-medium [overflow-wrap:anywhere]">{category.name}</span>
                <span className="font-mono text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
                  {category.usedBy} {category.usedBy === 1 ? 'event' : 'events'}
                </span>
                {data.canManage && (
                  <span className="flex gap-2">
                    <Button size="sm" variant="outline" onClick={() => setEditing(category)}>
                      Rename
                    </Button>
                    <Button size="sm" variant="destructive" onClick={() => setDeleting(category)}>
                      Delete
                    </Button>
                  </span>
                )}
              </li>
            ))}
          </ul>
        </Card>
      )}

      {editing && (
        <CategoryForm
          category={editing === 'new' ? null : editing}
          onClose={() => setEditing(null)}
          onSaved={() => {
            setEditing(null)
            load()
          }}
        />
      )}

      <ConfirmDialog
        open={deleting !== null}
        onOpenChange={(open) => !open && setDeleting(null)}
        title={`Delete the category “${deleting?.name ?? ''}”?`}
        subtitle={deleting && deleting.usedBy > 0 ? `${deleting.usedBy} ${deleting.usedBy === 1 ? 'event carries' : 'events carry'} it.` : undefined}
        action="Delete"
        failed="Could not delete the category."
        onConfirm={() => (deleting ? calendarCategoryApi.remove(deleting.id) : Promise.resolve())}
        onDone={() => {
          setDeleting(null)
          load()
        }}
      />
    </div>
  )
}

/** The make and rename form: just the name. */
function CategoryForm({
  category,
  onClose,
  onSaved,
}: {
  category: CalendarCategoryRow | null
  onClose: () => void
  onSaved: () => void
}) {
  const [name, setName] = useState(category?.name ?? '')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const save = () => {
    setBusy(true)
    setError(null)

    const request = category ? calendarCategoryApi.rename(category.id, name) : calendarCategoryApi.create(name)

    request
      .then(onSaved)
      .catch((e: unknown) => setError(e instanceof ApiError ? e.message : 'Could not save the category.'))
      .finally(() => setBusy(false))
  }

  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent
        title={category ? 'Rename category' : 'New category'}
        className="max-w-[480px]"
        foot={
          <DialogFoot>
            {error && <Outcome tone="problem">{error}</Outcome>}
            <Button size="sm" variant="outline" onClick={onClose} disabled={busy}>
              Cancel
            </Button>
            <Button size="sm" onClick={save} disabled={busy}>
              {busy ? 'Saving…' : 'Save'}
            </Button>
          </DialogFoot>
        }
      >
        <div className="flex flex-col gap-4" style={{ fontSize: 'var(--text-small)' }}>
          <Field label="Name" value={name} maxLength={MAX_NAME} onChange={setName} />
        </div>
      </DialogContent>
    </Dialog>
  )
}
