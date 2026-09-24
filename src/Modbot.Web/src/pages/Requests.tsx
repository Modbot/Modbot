import { useCallback, useEffect, useMemo, useState } from 'react'
import { Button } from '@/components/ui/button'
import { Badge } from '@/components/ui/badge'
import { Card } from '@/components/ui/card'
import { EmptyRow } from '@/components/PanelGrid'
import { Dialog, DialogContent } from '@/components/ui/dialog'
import { ReasonButtons } from '@/components/CaseFileForm'
import { SubjectLink } from '@/components/facts'
import { TrustRankBadge } from '@/components/TrustRankBadge'
import {
  api,
  ApiError,
  type BanReasonView,
  type CurrentUser,
  type JoinRequestAnswer,
  type JoinRequestList,
  type JoinRequestRow,
  type ModerationActionResult,
} from '@/lib/api'
import { formatDay } from '@/lib/format'
import { confirmTitle, historyNote, mayAnswer, resultText, rowIsAnswered } from '@/lib/joinRequests'
import { vrchatMedia } from '@/lib/vrchatMedia'

const PAGE_SIZE = 50

/**
 * The people waiting to be let into the group.
 *
 * Read from VRChat when the page opens, and never stored: this is the one list in Modbot whose
 * rows disappear because somebody else acted, and a cached copy would be a list of buttons that do
 * nothing. Paged by number, because VRChat pages this list by offset and sends no total.
 *
 * Answering a row takes it out of the list here rather than re-reading the page, so that clearing
 * a backlog costs one VRChat request per answer instead of two.
 */
export function Requests({ me, onOpenSubject }: { me: CurrentUser; onOpenSubject: (id: string) => void }) {
  const [page, setPage] = useState(1)
  const [list, setList] = useState<JoinRequestList | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)

  // Bumped by Refresh. The queue changes underneath a moderator whenever anybody answers one in
  // VRChat, and there is nothing to poll it with that would not spend the budget on a guess.
  const [reload, setReload] = useState(0)

  // The rows this moderator has answered, so they leave without a re-read.
  const [answered, setAnswered] = useState<string[]>([])

  const [open, setOpen] = useState<{ answer: JoinRequestAnswer; row: JoinRequestRow } | null>(null)

  useEffect(() => {
    let cancelled = false
    setLoading(true)

    api
      .joinRequests({ page, pageSize: PAGE_SIZE })
      .then((next) => {
        if (cancelled) return
        setList(next)
        setAnswered([])
        setError(null)
      })
      .catch((e: unknown) => {
        if (cancelled) return
        setList(null)
        setError(
          e instanceof ApiError
            ? e.status === 403
              ? 'You do not have permission to see join requests.'
              : e.message
            : 'Could not read the join requests.',
        )
      })
      .finally(() => {
        if (!cancelled) setLoading(false)
      })

    return () => {
      cancelled = true
    }
  }, [page, reload])

  const onAnswered = useCallback((userId: string) => setAnswered((ids) => [...ids, userId]), [])

  const rows = useMemo(
    () => (list?.requests ?? []).filter((r) => !answered.includes(r.userId)),
    [list, answered],
  )

  const canAnswer = mayAnswer(me)

  return (
    <div className="flex flex-col gap-3">
      {/* No "last synced" line: this list is not synced. It is read when the page is opened and
          again when Refresh is pressed, and saying so in a sentence would be explaining the
          screen rather than driving it. */}
      <div className="flex items-center justify-end">
        <Button size="sm" variant="outline" onClick={() => setReload((n) => n + 1)} disabled={loading}>
          Refresh
        </Button>
      </div>

      <Card>
        {error ? (
          <EmptyRow>{error}</EmptyRow>
        ) : loading && !list ? (
          <EmptyRow>Loading…</EmptyRow>
        ) : rows.length === 0 ? (
          <EmptyRow>Nobody is waiting</EmptyRow>
        ) : (
          <div className="overflow-x-auto">
            <table className="w-full" style={{ fontSize: 'var(--text-small)' }}>
              <thead className="bg-strip text-muted-foreground">
                <tr className="border-b" style={{ borderBottomWidth: 'var(--hairline)' }}>
                  <th className="px-3 py-2 text-left font-normal whitespace-nowrap">Person</th>
                  <th className="px-3 py-2 text-left font-normal whitespace-nowrap">Asked</th>
                  <th className="px-3 py-2 text-left font-normal whitespace-nowrap">History</th>
                  {canAnswer && (
                    <th className="px-3 py-2 text-left font-normal whitespace-nowrap">
                      <span className="sr-only">Actions</span>
                    </th>
                  )}
                </tr>
              </thead>
              <tbody>
                {rows.map((row) => (
                  <tr
                    key={row.userId}
                    className="border-b last:border-0 hover:bg-muted/40"
                    style={{ borderBottomWidth: 'var(--hairline)' }}
                  >
                    <td className="px-3" style={{ height: 'var(--row-h)' }}>
                      <div className="flex items-center gap-2">
                        {row.avatarThumbnailUrl ? (
                          <img
                            src={vrchatMedia(row.avatarThumbnailUrl)}
                            alt=""
                            className="size-7 shrink-0 rounded-full bg-muted object-cover"
                            referrerPolicy="no-referrer"
                          />
                        ) : (
                          <div className="size-7 shrink-0 rounded-full bg-muted" />
                        )}
                        <div className="min-w-0">
                          <div className="flex flex-wrap items-center gap-1.5">
                            <SubjectLink id={row.userId} name={row.displayName} onOpen={onOpenSubject} />
                            <TrustRankBadge rank={row.trustRank} />
                          </div>
                          {row.plainName && (
                            <div className="truncate text-muted-foreground" style={{ fontSize: '0.75rem' }}>
                              {row.plainName}
                            </div>
                          )}
                          {row.displayName && (
                            <div
                              className="truncate font-mono text-muted-foreground/70"
                              style={{ fontSize: '0.6875rem' }}
                            >
                              {row.userId}
                            </div>
                          )}
                        </div>
                      </div>
                    </td>
                    <td className="px-3 whitespace-nowrap font-mono">
                      {row.askedAt ? formatDay(row.askedAt) : <span className="text-muted-foreground">—</span>}
                    </td>
                    <td className="px-3">
                      <HistoryMark row={row} />
                    </td>
                    {canAnswer && (
                      <td className="px-3 text-right">
                        <div className="flex flex-wrap items-center justify-end gap-1.5">
                          <Button size="xs" variant="ghost" onClick={() => setOpen({ answer: 'approve', row })}>
                            Approve
                          </Button>
                          <Button size="xs" variant="outline" onClick={() => setOpen({ answer: 'reject', row })}>
                            Reject
                          </Button>
                        </div>
                      </td>
                    )}
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}

        {list && (list.page > 1 || list.hasMore) && (
          <div
            className="flex flex-wrap items-center gap-1 border-t bg-strip px-(--panel-pad) py-1.5"
            style={{ borderTopWidth: 'var(--hairline)', fontSize: 'var(--text-small)' }}
          >
            <Button size="xs" variant="outline" disabled={list.page <= 1} onClick={() => setPage((p) => p - 1)}>
              Previous
            </Button>
            <Button size="xs" variant="outline" disabled={!list.hasMore} onClick={() => setPage((p) => p + 1)}>
              Next
            </Button>
          </div>
        )}
      </Card>

      <Dialog open={open !== null} onOpenChange={(next) => !next && setOpen(null)}>
        {open !== null && (
          <ConfirmAnswer
            answer={open.answer}
            row={open.row}
            onClose={() => setOpen(null)}
            onAnswered={onAnswered}
          />
        )}
      </Dialog>
    </div>
  )
}

/** The one mark a row earns: that the group has dealt with this person before. */
function HistoryMark({ row }: { row: JoinRequestRow }) {
  const note = historyNote(row)

  if (!note) return <span className="text-muted-foreground">—</span>

  return <Badge variant={row.banned ? 'destructive' : 'outline'}>{note}</Badge>
}

/**
 * The confirmation, built the way the kick and ban one is: a key made when the dialog opens and
 * carried by every press, so a double click, a retry or a reload all produce one answer.
 */
function ConfirmAnswer({
  answer,
  row,
  onClose,
  onAnswered,
}: {
  answer: JoinRequestAnswer
  row: JoinRequestRow
  onClose: () => void
  onAnswered: (userId: string) => void
}) {
  const key = useMemo(() => crypto.randomUUID(), [])

  const [reasons, setReasons] = useState<BanReasonView[] | null>(null)
  const [picked, setPicked] = useState<string[]>([])
  const [note, setNote] = useState('')
  const [sending, setSending] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)
  const [result, setResult] = useState<ModerationActionResult | null>(null)

  useEffect(() => {
    if (answer !== 'reject') return

    api
      .banReasons()
      .then((list) => setReasons(list.reasons.filter((r) => r.isActive)))
      .catch(() => setReasons([]))
  }, [answer])

  const send = () => {
    setSending(true)
    setProblem(null)

    const body = { userId: row.userId, key, reasonIds: picked, note }
    const call = answer === 'approve' ? api.approveJoinRequest : api.rejectJoinRequest

    call(body)
      .then((r) => {
        setResult(r)
        if (rowIsAnswered(r)) onAnswered(row.userId)
      })
      .catch((e: unknown) =>
        setProblem(e instanceof ApiError ? e.message : 'Modbot could not reach VRChat.'),
      )
      .finally(() => setSending(false))
  }

  return (
    <DialogContent
      title={confirmTitle(answer, row.displayName ?? row.userId)}
      subtitle={
        <span className="font-mono" title={row.userId}>
          {row.userId}
        </span>
      }
      className="max-w-[460px]"
    >
      <div className="flex flex-col gap-3">
        {result === null && (
          <>
            {answer === 'reject' && (
              <>
                {reasons === null ? (
                  <p className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
                    Loading the reasons…
                  </p>
                ) : (
                  <ReasonButtons reasons={reasons} picked={picked} onChange={setPicked} />
                )}

                <label className="flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
                  <span className="text-muted-foreground">Note (optional)</span>
                  <textarea
                    className="w-full rounded-sm border border-(length:--hairline) border-input bg-card px-2.5 py-1 outline-none focus-visible:border-ring focus-visible:ring-1 focus-visible:ring-ring"
                    rows={3}
                    value={note}
                    onChange={(e) => setNote(e.target.value)}
                  />
                </label>
              </>
            )}

            <div className="flex flex-wrap items-center justify-end gap-2">
              <Button size="sm" variant="outline" onClick={onClose} disabled={sending}>
                Cancel
              </Button>
              <Button
                size="sm"
                variant={answer === 'approve' ? 'default' : 'destructive'}
                onClick={send}
                disabled={sending}
              >
                {sending ? 'Sending…' : answer === 'approve' ? 'Approve' : 'Reject'}
              </Button>
            </div>

            {problem && (
              <p className="text-destructive" style={{ fontSize: 'var(--text-small)' }}>
                {problem}
              </p>
            )}
          </>
        )}

        {result !== null && (
          <>
            <p className={result.done || result.gone ? '' : 'text-destructive'}>
              {resultText(answer, result)}
            </p>

            <div className="flex justify-end">
              <Button size="sm" onClick={onClose}>
                Close
              </Button>
            </div>
          </>
        )}
      </div>
    </DialogContent>
  )
}
