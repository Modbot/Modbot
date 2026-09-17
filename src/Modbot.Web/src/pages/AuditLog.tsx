import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { FilterBar } from '@/components/filters/FilterBar'
import { FactSentence } from '@/components/factSentence'
import { FactTime, SourceBadge } from '@/components/facts'
import { formatDay, sourceLabel } from '@/lib/format'
import { useFilters, type FilterProperty } from '@/lib/filters'
import { useListSelection } from '@/lib/listSelection'
import { AUDIT_DEFAULTS, auditQueryFrom } from '@/lib/pageFilters'
import { useLocation } from '@/lib/router'
import { api, ApiError, type AuditEntry, type AuditFilters, type AuditPage } from '@/lib/api'
import { openDiscordPerson, openInstance, openPerson } from '@/lib/subject'
import { cn } from '@/lib/utils'

/**
 * The merged timeline (spec 5.9.5).
 *
 * One log, because the questions people actually ask span sources: *"who changed the ban
 * threshold just before these bans?"* is unanswerable in either log alone. The default chips show
 * VRChat, Discord and Client and leave Sync out -- a sweep's noticed changes have a time window
 * and no actor, and they crowd the exact entries when both are on. One click on the chip brings
 * them back.
 *
 * The filters offered here come from the server and are already narrowed to what this account may
 * read. That is a convenience, not the enforcement — the server filters every query regardless,
 * so a hand-edited request gains nothing.
 */

const SOURCES = ['AuditLog', 'SyncDiff', 'Client', 'Discord', 'Manual', 'Modbot']

export function AuditLog() {
  const [location] = useLocation()

  // `?fact=` opens the log at one entry: the timeline starts there and the row is marked. It is
  // what a source chip under a Chat answer links to.
  const factId = location.search.get('fact')

  const [fetched, setFetched] = useState<{ factId: string; entry: AuditEntry } | null>(null)

  // Only the entry that was fetched for the fact currently in the address counts, so nothing has
  // to be cleared when the address changes.
  const openAt = fetched?.factId === factId ? fetched.entry : null
  const [filters, setFilters] = useState<AuditFilters | null>(null)
  const [pages, setPages] = useState<AuditPage[]>([])
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)

  const [chips, setChips] = useFilters('audit', AUDIT_DEFAULTS)

  useEffect(() => {
    if (!factId) return

    let cancelled = false
    api
      .auditEntry(factId)
      .then((entry) => {
        if (!cancelled) setFetched({ factId, entry })
      })
      .catch(() => {
        // An entry this account may not read, or one that is gone: the log opens where it always does.
      })

    return () => {
      cancelled = true
    }
  }, [factId])

  const query = useMemo(() => {
    const from = auditQueryFrom(chips)
    return {
      ...from,
      // Opened at one entry: a second past it, so the entry itself is the first row rather than
      // the one above it. A day picked in the filter still wins.
      to: from.to ?? (openAt ? new Date(Date.parse(openAt.occurredAt) + 1000).toISOString() : undefined),
      limit: 50,
    }
  }, [chips, openAt])

  useEffect(() => {
    api.auditFilters().then(setFilters).catch(() => setFilters(null))
  }, [])

  useEffect(() => {
    let cancelled = false

    // The previous page stays on screen until the new one lands. Blanking it first would flash an
    // empty table between every keystroke in the id filters, and an empty audit log is a sentence
    // nobody should read by accident.
    api
      .audit(query)
      .then((page) => {
        if (cancelled) return
        setPages([page])
        setError(null)
      })
      .catch((e: unknown) => {
        if (cancelled) return
        setError(
          e instanceof ApiError && e.status === 403
            ? 'You do not have permission to read either log.'
            : 'Could not load the audit log.',
        )
      })
      .finally(() => {
        if (!cancelled) setLoading(false)
      })

    return () => {
      cancelled = true
    }
  }, [query])

  const more = useCallback(() => {
    const last = pages[pages.length - 1]
    if (!last?.next) return

    setLoading(true)
    api
      .audit({ ...query, before: last.next })
      .then((page) => setPages((p) => [...p, page]))
      .catch(() => setError('Could not load more.'))
      .finally(() => setLoading(false))
  }, [pages, query])

  const entries = pages.flatMap((p) => p.entries)
  const coverage = pages[0]?.coverage
  const next = pages[pages.length - 1]?.next

  // `j`/`k` move down and up the rows; `Enter` opens what the selected row is about.
  const { rowProps } = useListSelection(entries.length, (i) => {
    const entry = entries[i]
    if (!entry) return
    if (entry.subjectKind === 'Person')
      (entry.subjectPlatform.toLowerCase() === 'discord' ? openDiscordPerson : openPerson)(entry.subjectId)
    else if (entry.subjectKind === 'Instance' && entry.roomId) openInstance(entry.roomId)
  })

  const properties = useMemo<FilterProperty[]>(
    () => [
      { id: 'source', label: 'Source', kind: 'choice', options: SOURCES.map((s) => ({ value: s, label: sourceLabel(s) })) },
      {
        id: 'type',
        label: 'Type',
        kind: 'choice',
        options: (filters?.types ?? []).map((t) => ({ value: t.value, label: t.label })),
        placeholder: 'Type',
      },
      {
        id: 'category',
        label: 'Log',
        kind: 'choice',
        multi: false,
        negatable: false,
        options: [
          ...(filters?.canViewModeration !== false ? [{ value: 'Moderation', label: 'Moderation' }] : []),
          ...(filters?.canViewOperational !== false ? [{ value: 'Operational', label: 'Operational' }] : []),
        ],
      },
      {
        id: 'actor',
        label: 'Done by',
        kind: 'choice',
        multi: false,
        negatable: false,
        freeText: true,
        options: (filters?.actors ?? []).map((a) => ({ value: a.id, label: a.name ?? a.id, count: a.actions })),
        placeholder: 'Name or id',
      },
      { id: 'subject', label: 'About', kind: 'id', placeholder: 'usr_…' },
      { id: 'world', label: 'World', kind: 'id', placeholder: 'wrld_…' },
      { id: 'instance', label: 'Instance number', kind: 'id', placeholder: '39047' },
      { id: 'when', label: 'When', kind: 'date' },
      {
        id: 'precision',
        label: 'Time',
        kind: 'choice',
        multi: false,
        negatable: false,
        options: [
          { value: 'Exact', label: 'Exact' },
          { value: 'Window', label: 'A window' },
        ],
      },
      { id: 'hasActor', label: 'Somebody named', kind: 'yesno' },
      { id: 'text', label: 'Text', kind: 'text', placeholder: 'A word or phrase' },
    ],
    [filters],
  )

  if (error) {
    return (
      <Card>
        <CardContent className="py-10 text-center text-muted-foreground">{error}</CardContent>
      </Card>
    )
  }

  return (
    <div className="flex flex-col gap-3">
      <FilterBar properties={properties} chips={chips} onChange={setChips}>
        {coverage && <Coverage coverage={coverage} />}
      </FilterBar>

      <Card>
        <CardContent className="p-0">
          {entries.length === 0 && !loading ? (
            <div className="py-10 text-center text-muted-foreground">No entries match these filters.</div>
          ) : (
            <div className="overflow-x-auto">
              <table className="w-full" style={{ fontSize: 'var(--text-small)' }}>
                <thead className="text-muted-foreground">
                  <tr className="border-b" style={{ borderBottomWidth: 'var(--hairline)' }}>
                    <th className="px-3 py-2 text-left font-normal">When</th>
                    <th className="px-3 py-2 text-left font-normal">Source</th>
                    <th className="px-3 py-2 text-left font-normal">What happened</th>
                  </tr>
                </thead>
                <tbody>
                  {entries.map((entry, i) => (
                    <Row key={entry.id} entry={entry} marked={String(entry.id) === factId} {...rowProps(i)} />
                  ))}
                </tbody>
              </table>
            </div>
          )}

          <div
            className="flex items-center gap-3 border-t px-3 py-2 text-muted-foreground"
            style={{ borderTopWidth: 'var(--hairline)', fontSize: 'var(--text-small)' }}
          >
            <span>
              {entries.length} {entries.length === 1 ? 'entry' : 'entries'} shown
            </span>
            <span className="flex-1" />
            {next && (
              <Button size="sm" variant="outline" disabled={loading} onClick={more}>
                {loading ? 'Loading…' : 'Load more'}
              </Button>
            )}
          </div>
        </CardContent>
      </Card>
    </div>
  )
}

/**
 * One fact, as a sentence.
 *
 * It used to print the raw type and then the subject and the actor as ids in two more columns,
 * which is three things to read and none of them words. The sentence names who did what to whom
 * and where, and every name in it opens its own popup (see components/factSentence.tsx).
 */
function Row({
  entry,
  marked,
  ...rowAttributes
}: {
  entry: AuditEntry
  marked: boolean
  'data-row-index': number
  'data-selected': boolean | undefined
  'aria-selected': boolean
}) {
  const row = useRef<HTMLTableRowElement>(null)
  const brought = useRef(false)

  useEffect(() => {
    if (!marked || brought.current) return
    brought.current = true
    row.current?.scrollIntoView({ block: 'center' })
  }, [marked])

  return (
    <tr
      ref={row}
      {...rowAttributes}
      className={cn('border-b last:border-0 hover:bg-muted/40 data-[selected]:bg-accent/60', marked && 'bg-accent')}
      style={{ borderBottomWidth: 'var(--hairline)' }}
    >
      <td className="whitespace-nowrap px-3 align-top" style={{ height: 'var(--row-h)' }}>
        <div className="flex flex-col py-1 leading-tight">
          <FactTime entry={entry} />
          <span className="text-muted-foreground/70">{formatDay(entry.occurredAt)}</span>
        </div>
      </td>
      <td className="px-3 py-1 align-top">
        <SourceBadge source={entry.source} />
      </td>
      <td className="max-w-3xl px-3 py-1.5 align-top" title={entry.type}>
        {/* Payload text is user-controlled (spec 5.3). The sentence renders it as text, never as
            markup. */}
        <FactSentence entry={entry} />
      </td>
    </tr>
  )
}

/**
 * Where the timeline actually starts.
 *
 * Shown always, not on a threshold. The oldest entry on screen looks like the beginning of the
 * history whether or not it is, and while the catch-up is still running it is not even stable.
 */
function Coverage({ coverage }: { coverage: AuditPage['coverage'] }) {
  if (!coverage.oldestFact) return null

  return (
    <span className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
      Oldest recorded entry: {formatDay(coverage.oldestFact)}
      {coverage.catchUpComplete ? '' : ' · catch-up still running'}
    </span>
  )
}
