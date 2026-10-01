import { useCallback, useState } from 'react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { ConfirmDialog } from '@/components/ConfirmDialog'
import { Block, Empty, Note as Muted } from '@/components/subject/shared'
import { api, ApiError, type PersonWatches, type Watch } from '@/lib/api'
import { dateTime, formatDay } from '@/lib/format'
import { useLoad } from '@/lib/useLoad'

/** The longest a reason may be. The server says the same. */
const MAX_WATCH_REASON = 200

/**
 * Watching a person (watching a person design): the watch that stands on them, with Stop watching,
 * or a Watch button for somebody who may start one. At the top of the Notes tab, because a watch is
 * the same kind of thing as a note -- a moderator's own word about somebody -- under the same
 * permission.
 *
 * A watch never acts. It flags the person and tells the team when they arrive; the end day and the
 * check-back day are both optional.
 */
export function PersonWatch({
  vrchatId,
  discordId,
  name,
  onChanged,
}: {
  vrchatId: string | null
  discordId: string | null
  /** The person's name, for the confirmation. Falls back to the id. */
  name?: string | null
  /** Called after a watch is started, stopped or followed up, so the chip above can be read again. */
  onChanged?: () => void
}) {
  const [version, setVersion] = useState(0)

  const load = useCallback(() => api.personWatches({ vrchat: vrchatId, discord: discordId }), [vrchatId, discordId])
  const { data, error, reload } = useLoad<PersonWatches>(load, version)

  const again = () => {
    setVersion((n) => n + 1)
    onChanged?.()
  }

  if (error) return <Empty tone="danger" onTryAgain={reload}>{error}</Empty>
  if (!data) return null

  const standing = data.watches.find((w) => w.standing) ?? null

  if (standing) return <Standing watch={standing} about={name ?? standing.subjectId} onChanged={again} />

  if (!data.canWrite) return null

  // A watch is started on the VRChat account where there is one, as notes are filed.
  const target = vrchatId
    ? { userId: vrchatId, platform: 'VRChat' as const }
    : discordId
      ? { userId: discordId, platform: 'Discord' as const }
      : null

  return target ? <StartWatch target={target} onStarted={again} /> : null
}

function Standing({ watch, about, onChanged }: { watch: Watch; about: string; onChanged: () => void }) {
  const [stopping, setStopping] = useState(false)
  const [busy, setBusy] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)

  const followedUp = () => {
    setBusy(true)
    setProblem(null)

    api
      .followedUp(watch.id)
      .then(onChanged)
      .catch((e: unknown) => setProblem(e instanceof ApiError ? e.message : 'Could not save that.'))
      .finally(() => setBusy(false))
  }

  return (
    <Block className="gap-1.5">
      <div className="flex flex-wrap items-center gap-2" style={{ fontSize: 'var(--text-small)' }}>
        <Badge variant="destructive">Watched</Badge>
        {watch.followUpDue && <Badge variant="outline">Follow-up due</Badge>}
        <span className="flex-1" />
        {watch.canChange && watch.followUpDue && (
          <Button size="xs" variant="outline" disabled={busy} onClick={followedUp}>
            Followed up
          </Button>
        )}
        {watch.canChange && (
          <Button size="xs" variant="ghost" onClick={() => setStopping(true)}>
            Stop watching
          </Button>
        )}
      </div>

      <p className="break-words whitespace-pre-wrap" style={{ fontSize: 'var(--text-small)' }}>
        {watch.reason}
      </p>

      <Muted>
        {watch.setByName} · <span className="font-mono">{dateTime(watch.setAt)}</span>
        {watch.endsAt && (
          <>
            {' '}· until <span className="font-mono">{formatDay(watch.endsAt)}</span>
          </>
        )}
        {watch.followUpAt && (
          <>
            {' '}· check back <span className="font-mono">{formatDay(watch.followUpAt)}</span>
          </>
        )}
      </Muted>

      {problem && <Muted className="text-destructive">{problem}</Muted>}

      {watch.canChange && (
        <ConfirmDialog
          open={stopping}
          onOpenChange={setStopping}
          title={`Stop watching ${about}?`}
          subtitle={watch.reason}
          action="Stop watching"
          failed="Could not stop the watch."
          onConfirm={() => api.stopWatch(watch.id)}
          onDone={onChanged}
        />
      )}
    </Block>
  )
}

/** Today in the browser's own calendar, as a date box wants it. */
function today(): string {
  const d = new Date()
  const pad = (n: number) => String(n).padStart(2, '0')
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`
}

function StartWatch({
  target,
  onStarted,
}: {
  target: { userId: string; platform: 'VRChat' | 'Discord' }
  onStarted: () => void
}) {
  const [open, setOpen] = useState(false)
  const [reason, setReason] = useState('')
  const [ends, setEnds] = useState('')
  const [followUp, setFollowUp] = useState('')
  const [sending, setSending] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)

  if (!open) {
    return (
      <Block>
        <div>
          <Button size="sm" variant="outline" onClick={() => setOpen(true)}>
            Watch
          </Button>
        </div>
      </Block>
    )
  }

  const send = () => {
    setSending(true)
    setProblem(null)

    api
      .startWatch({
        userId: target.userId,
        platform: target.platform,
        reason: reason.trim(),
        // The end of the day picked, and the start of the follow-up day, in the browser's own time.
        endsAt: ends ? new Date(`${ends}T23:59:59`).toISOString() : null,
        followUpAt: followUp ? new Date(`${followUp}T00:00:00`).toISOString() : null,
      })
      .then(() => {
        setOpen(false)
        setReason('')
        setEnds('')
        setFollowUp('')
        onStarted()
      })
      .catch((e: unknown) => setProblem(e instanceof ApiError ? e.message : 'Could not start the watch.'))
      .finally(() => setSending(false))
  }

  const from = today()

  return (
    <Block className="gap-2">
      <label className="flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
        <span className="text-muted-foreground">Reason</span>
        <Input value={reason} maxLength={MAX_WATCH_REASON} onChange={(e) => setReason(e.target.value)} autoFocus />
      </label>

      <div className="flex flex-wrap gap-2">
        <label className="flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
          <span className="text-muted-foreground">Ends</span>
          <Input type="date" min={from} value={ends} onChange={(e) => setEnds(e.target.value)} />
        </label>
        <label className="flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
          <span className="text-muted-foreground">Check back on</span>
          <Input type="date" min={from} max={ends || undefined} value={followUp} onChange={(e) => setFollowUp(e.target.value)} />
        </label>
      </div>

      <div className="flex flex-wrap items-center gap-2">
        <Button size="sm" onClick={send} disabled={sending || reason.trim().length === 0}>
          {sending ? 'Saving…' : 'Watch'}
        </Button>
        <Button size="sm" variant="ghost" onClick={() => setOpen(false)} disabled={sending}>
          Cancel
        </Button>
        {problem && <Muted className="text-destructive">{problem}</Muted>}
      </div>
    </Block>
  )
}
