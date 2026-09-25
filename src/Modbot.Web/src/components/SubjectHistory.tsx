import { useEffect, useState } from 'react'
import { Badge } from '@/components/ui/badge'
import { EmptyRow } from '@/components/PanelGrid'
import { Ago } from '@/components/Freshness'
import { Footer, Panel } from '@/components/subject/shared'
import { formatDay } from '@/lib/format'
import { api, ApiError, type RepeatOffenderView, type SubjectHistory as History } from '@/lib/api'

/**
 * The "History" block on a person's pane: how many times they have been acted on, by how many
 * moderators, and when last (spec 5.8.4).
 *
 * Read from the repeat-offender counts, which the detection run rebuilds from the fact log on the
 * daily totals schedule. That is why the block says when the counts were last rebuilt: a number a
 * quarter-hour old is fine, but a number that looks live and is not would be the wrong kind of
 * wrong. The status word is never shown without the rule that decided it (spec 5.10.3).
 */
export function SubjectHistory({ subjectId }: { subjectId: string }) {
  const [history, setHistory] = useState<History | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    let cancelled = false

    api
      .subjectHistory(subjectId)
      .then((next) => {
        if (!cancelled) setHistory(next)
      })
      .catch((e: unknown) => {
        if (cancelled) return
        setError(
          e instanceof ApiError && e.status === 403
            ? 'You do not have permission to see this history.'
            : 'Could not load this person’s history.',
        )
      })

    return () => {
      cancelled = true
    }
  }, [subjectId])

  return (
    <Panel title="History" flush>
      {error && <EmptyRow className="text-destructive">{error}</EmptyRow>}

      {!error && !history && <EmptyRow>Loading…</EmptyRow>}

      {history && !history.known && (
        <EmptyRow>{history.lastRunAt === null ? 'Counts not built yet.' : 'No actions recorded.'}</EmptyRow>
      )}

      {history?.counts && <Counts counts={history.counts} rule={history.rule} now={history.now} />}

      {history?.lastRunAt && (
        <Footer>Counts rebuilt <Ago iso={history.lastRunAt} now={history.now} />.</Footer>
      )}
    </Panel>
  )
}

function Counts({ counts, rule, now }: { counts: RepeatOffenderView; rule: string; now: string }) {
  const times = counts.actions === 1 ? 'once' : `${counts.actions} times`
  const by =
    counts.moderators === 0
      ? 'with no moderator named'
      : counts.moderators === 1
        ? 'by one moderator'
        : `by ${counts.moderators} different moderators`

  return (
    <div className="flex flex-col gap-1.5 p-(--panel-pad)" style={{ fontSize: 'var(--text-small)' }}>
      <p>
        Acted on <span className="font-medium">{times}</span> {by}
        {counts.actionsLast30Days > 0 && (
          <>
            , <span className="font-medium">{counts.actionsLast30Days}</span> in the last 30 days
          </>
        )}
        .
      </p>

      <p className="text-muted-foreground">
        Last: {counts.lastActionLabel.toLowerCase()} <Ago iso={counts.lastActionAt} now={now} /> (<span className="font-mono">{formatDay(counts.lastActionAt)}</span>)
        {counts.lastBy && (
          <>
            {' '}by {counts.lastBy.name ?? <span className="font-mono">{counts.lastBy.id}</span>}
          </>
        )}
        . First: {formatDay(counts.firstActionAt)}.
      </p>

      <div className="flex flex-wrap gap-1">
        <Count n={counts.instanceKicks} label="instance kick" plural="instance kicks" />
        <Count n={counts.warns} label="warn" plural="warns" />
        <Count n={counts.bans} label="ban" plural="bans" />
        <Count n={counts.removals} label="removed from group" plural="removed from group" />
        <Count n={counts.rejections} label="join request turned away" plural="join requests turned away" />
        <Count n={counts.unbans} label="unban" plural="unbans" muted />
      </div>

      <div className="flex items-center gap-2">
        <StatusPill status={counts.status} />
        <span className="text-muted-foreground" title={rule}>
          {rule}
        </span>
      </div>
    </div>
  )
}

function Count({ n, label, plural, muted }: { n: number; label: string; plural: string; muted?: boolean }) {
  if (n === 0) return null
  return (
    <Badge variant={muted ? 'outline' : 'secondary'}>
      <span className="font-mono">{n}</span> {n === 1 ? label : plural}
    </Badge>
  )
}

/** The three words, styled so "repeat" is the one the eye lands on. */
export function StatusPill({ status }: { status: RepeatOffenderView['status'] }) {
  const words = status === 'repeat' ? 'Repeat' : status === 'more-than-once' ? 'More than once' : 'Once'

  return (
    <Badge variant={status === 'repeat' ? 'destructive' : 'outline'}>{words}</Badge>
  )
}
