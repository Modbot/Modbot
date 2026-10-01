import { useCallback, useEffect, useState } from 'react'
import { Button } from '@/components/ui/button'
import { EmptyRow } from '@/components/PanelGrid'
import { InstanceLink } from '@/components/facts'
import { Panel } from '@/components/subject/shared'
import { api, type PersonVisit, type PersonVisitsPage } from '@/lib/api'
import { dateTime, lengthOfTime, whenRange } from '@/lib/format'

const PAGE = 10

/**
 * Where a person has been seen, a visit per row, newest first: the instance, when they arrived and
 * left, how long that was, the name and avatars they were seen with, and which moderators'
 * companions saw them.
 *
 * Made on the server from the same reports, by the same rule, as the time-in-world figures, kept
 * inside each instance's own life. A visit nobody saw end runs to the last report from that
 * instance, which is the last moment anything is known, and the row says it was not seen ending.
 * Hidden when there is nothing to show: a person no companion has seen has no visits, and the
 * figures above already leave that out.
 */
export function PersonVisits({ userId, currentName }: { userId: string; currentName: string | null }) {
  const [pages, setPages] = useState<PersonVisitsPage[]>([])
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)

  // Read once per person, and again when a failed first read is tried again. The Overview that
  // holds this is remounted, not re-rendered, when what it shows changes.
  const [tries, setTries] = useState(0)

  useEffect(() => {
    let cancelled = false
    api
      .personVisits(userId, null, PAGE)
      .then((page) => {
        if (cancelled) return
        setPages([page])
        setError(null)
      })
      .catch(() => {
        if (!cancelled) setError('Could not load the visits.')
      })
      .finally(() => {
        if (!cancelled) setLoading(false)
      })
    return () => {
      cancelled = true
    }
  }, [userId, tries])

  const first = useCallback(() => {
    setError(null)
    setLoading(true)
    setTries((n) => n + 1)
  }, [])

  const next = pages[pages.length - 1]?.next ?? null

  const more = useCallback(() => {
    if (!next) return
    setError(null)
    setLoading(true)
    api
      .personVisits(userId, next, PAGE)
      .then((page) => setPages((current) => [...current, page]))
      .catch(() => setError('Could not load more.'))
      .finally(() => setLoading(false))
  }, [userId, next])

  const visits = pages.flatMap((p) => p.visits)

  if (!error && pages.length > 0 && visits.length === 0) return null
  if (!error && pages.length === 0) return null

  return (
    <Panel title="Visits" flush>
      {error && <EmptyRow tone="danger" onTryAgain={pages.length > 0 ? more : first}>{error}</EmptyRow>}
      {visits.length > 0 && (
        <ol className="flex flex-col">
          {visits.map((visit) => (
            <Visit key={visit.arrived.id} visit={visit} currentName={currentName} />
          ))}
        </ol>
      )}
      {next && (
        <div className="flex justify-end border-t border-t-(length:--hairline) px-(--panel-pad) py-2">
          <Button size="xs" variant="outline" disabled={loading} onClick={more}>
            {loading ? 'Loading…' : 'Load more'}
          </Button>
        </div>
      )}
    </Panel>
  )
}

/**
 * One visit, in three lines that fit a phone as well as a desk: where and how long; when, with
 * whether they were already there and whether anybody saw them go; then what they were seen as and
 * who saw them. The instance opens its own popup.
 */
function Visit({ visit, currentName }: { visit: PersonVisit; currentName: string | null }) {
  const { arrived, seenLeaving, until, name, avatars, seenBy } = visit
  const minutes = (Date.parse(until) - Date.parse(arrived.occurredAt)) / 60_000

  // "Already here" is a client arriving to find them there: when they really arrived is not known.
  const alreadyThere = arrived.type === 'vrchat.instance.presence'

  const marks = [alreadyThere ? 'Already there' : null, seenLeaving ? null : 'Not seen leaving'].filter(Boolean)
  const watchers = seenBy.map((r) => r.name).filter((n): n is string => !!n)
  const seen = [
    name && name !== currentName ? `As ${name}` : null,
    avatars.length > 0 ? `${avatars.length === 1 ? 'Avatar' : 'Avatars'}: ${avatars.join(', ')}` : null,
    watchers.length > 0 ? `Seen by ${watchers.join(', ')}` : null,
  ].filter(Boolean)

  return (
    <li
      className="border-t border-t-(length:--hairline) px-(--panel-pad) py-2 first:border-t-0"
      style={{ fontSize: 'var(--text-small)' }}
    >
      <div className="flex items-baseline gap-2">
        <span className="min-w-0 truncate">
          <InstanceLink
            modbotInstanceId={arrived.modbotInstanceId}
            worldId={arrived.worldId}
            worldName={arrived.worldName}
            number={arrived.instanceId}
            name={arrived.instanceName}
          />
        </span>
        <span className="flex-1" />
        <span className="shrink-0 font-mono">{lengthOfTime(minutes)}</span>
      </div>
      <div className="mt-0.5 text-muted-foreground">
        <span className="font-mono" title={`${dateTime(arrived.occurredAt)} – ${dateTime(until)}`}>
          {whenRange(arrived.occurredAt, until)}
        </span>
        {marks.length > 0 && <> · {marks.join(' · ')}</>}
      </div>
      {seen.length > 0 && <div className="mt-0.5 break-words text-muted-foreground">{seen.join(' · ')}</div>}
    </li>
  )
}
