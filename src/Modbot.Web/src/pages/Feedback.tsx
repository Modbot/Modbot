import { useCallback, useEffect, useState } from 'react'
import { EmptyRow } from '@/components/PanelGrid'
import { Badge } from '@/components/ui/badge'
import { Card } from '@/components/ui/card'
import { Dialog, DialogContent } from '@/components/ui/dialog'
import { SwitchBank } from '@/components/ui/switch-bank'
import { ApiError } from '@/lib/api'
import { feedbackApi, type Feedback as FeedbackItem, type FeedbackSummary } from '@/lib/feedback'
import {
  FEEDBACK_STATUSES,
  feedbackKindLabel,
  feedbackStatusBadge,
  feedbackStatusLabel,
  type FeedbackFilter,
  type FeedbackStatus,
} from '@/lib/feedbackWords'
import { dateTime } from '@/lib/format'

const problemText = (e: unknown, what: string): string =>
  e instanceof ApiError && e.status === 403 ? 'You do not have permission to see this.' : `Could not load ${what}.`

/**
 * Feedback: what staff sent with the Feedback button, for the admins to read and mark.
 *
 * Admins only: the server needs Review feedback for every read and every change, and the page holds
 * no permission check of its own.
 */
export function Feedback() {
  const [filter, setFilter] = useState<FeedbackFilter>('all')
  const [items, setItems] = useState<FeedbackSummary[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [openId, setOpenId] = useState<string | null>(null)

  const load = useCallback(() => {
    return feedbackApi
      .list(filter)
      .then((answer) => {
        setItems(answer.items)
        setError(null)
      })
      .catch((e: unknown) => setError(problemText(e, 'the feedback')))
  }, [filter])

  useEffect(() => {
    load()
  }, [load])

  return (
    <div className="flex flex-col gap-3">
      <SwitchBank
        value={filter}
        onChange={setFilter}
        label="Status"
        options={[{ value: 'all', label: 'All' }, ...FEEDBACK_STATUSES]}
      />

      {error && (
        <Card>
          <EmptyRow tone="danger" onTryAgain={load}>
            {error}
          </EmptyRow>
        </Card>
      )}

      {!error && !items && (
        <Card>
          <EmptyRow tone="loading" />
        </Card>
      )}

      {!error && items && items.length === 0 && (
        <Card>
          <EmptyRow>{filter === 'all' ? 'No feedback yet.' : 'No feedback matches.'}</EmptyRow>
        </Card>
      )}

      {!error && items && items.length > 0 && (
        <Card>
          <ul className="flex flex-col">
            {items.map((item) => (
              <FeedbackRow key={item.id} item={item} onOpen={() => setOpenId(item.id)} />
            ))}
          </ul>
        </Card>
      )}

      {openId && (
        <FeedbackDialog
          id={openId}
          onClose={() => setOpenId(null)}
          onChanged={() => {
            void load()
          }}
        />
      )}
    </div>
  )
}

/** One item as a list row: when, who, what kind, its title and where it stands. */
function FeedbackRow({ item, onOpen }: { item: FeedbackSummary; onOpen: () => void }) {
  return (
    <li className="border-t border-t-(length:--hairline) first:border-t-0">
      <button
        type="button"
        onClick={onOpen}
        className="flex w-full flex-col gap-1 px-(--panel-pad) py-1.5 text-left hover:bg-muted focus-visible:outline-2 focus-visible:-outline-offset-2 focus-visible:outline-ring"
        style={{ fontSize: 'var(--text-small)' }}
      >
        <span className="flex min-h-(--row-h) flex-wrap items-center gap-x-2 gap-y-1">
          <Badge variant="secondary">{feedbackKindLabel(item.kind)}</Badge>
          <span className="min-w-0 flex-1 basis-40 font-medium [overflow-wrap:anywhere]">{item.title}</span>
          <Badge variant={feedbackStatusBadge(item.status)}>{feedbackStatusLabel(item.status)}</Badge>
        </span>
        <span className="flex flex-wrap items-center gap-x-3 text-muted-foreground">
          <span className="[overflow-wrap:anywhere]">{item.senderName}</span>
          <span className="font-mono">{dateTime(item.createdAt)}</span>
        </span>
      </button>
    </li>
  )
}

/** One item in full, with the four statuses to mark it with. */
function FeedbackDialog({ id, onClose, onChanged }: { id: string; onClose: () => void; onChanged: () => void }) {
  const [item, setItem] = useState<FeedbackItem | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const load = useCallback(() => {
    return feedbackApi
      .get(id)
      .then((next) => {
        setItem(next)
        setError(null)
      })
      .catch((e: unknown) => setError(problemText(e, 'the feedback')))
  }, [id])

  useEffect(() => {
    load()
  }, [load])

  const mark = (status: FeedbackStatus) => {
    if (!item || item.status === status) return

    setBusy(true)
    setError(null)
    feedbackApi
      .setStatus(item.id, status)
      .then((next) => {
        setItem(next)
        onChanged()
      })
      .catch((e: unknown) => setError(e instanceof ApiError ? e.message : 'Could not save the status.'))
      .finally(() => setBusy(false))
  }

  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent title={item?.title ?? 'Feedback'} className="max-w-[640px]" bodyClassName="max-h-[78vh] overflow-y-auto">
        {!item && (
          <EmptyRow tone={error ? 'danger' : 'loading'} onTryAgain={load}>
            {error}
          </EmptyRow>
        )}

        {item && (
          <div className="flex flex-col gap-3" style={{ fontSize: 'var(--text-small)' }}>
            <div className="flex flex-wrap items-center gap-x-2 gap-y-1">
              <Badge variant="secondary">{feedbackKindLabel(item.kind)}</Badge>
              <Badge variant={feedbackStatusBadge(item.status)}>{feedbackStatusLabel(item.status)}</Badge>
              <span className="text-muted-foreground [overflow-wrap:anywhere]">{item.senderName}</span>
              <span className="font-mono text-muted-foreground">{dateTime(item.createdAt)}</span>
            </div>

            <p className="whitespace-pre-wrap [overflow-wrap:anywhere]">{item.details}</p>

            <SwitchBank value={item.status as FeedbackStatus} onChange={mark} label="Status" disabled={busy} options={FEEDBACK_STATUSES} />

            {item.statusChangedAt && (
              <p className="text-muted-foreground [overflow-wrap:anywhere]">
                {feedbackStatusLabel(item.status)}
                {item.statusChangedBy ? ` by ${item.statusChangedBy}` : ''}{' '}
                <span className="font-mono">{dateTime(item.statusChangedAt)}</span>
              </p>
            )}

            {error && <p className="text-destructive">{error}</p>}
          </div>
        )}
      </DialogContent>
    </Dialog>
  )
}
