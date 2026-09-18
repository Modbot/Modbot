import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { ChevronRight } from 'lucide-react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { EntryDetail } from '@/components/audit/EntryDetail'
import { FilterBar } from '@/components/filters/FilterBar'
import { FactSentence } from '@/components/factSentence'
import { FactTime, SourceBadge } from '@/components/facts'
import { formatDay, sourceLabel } from '@/lib/format'
import { useFilters, type FilterProperty } from '@/lib/filters'
import { useListSelection } from '@/lib/listSelection'
import { auditMatches } from '@/lib/liveRules'
import type { LiveEvent } from '@/lib/liveStream'
import { AUDIT_DEFAULTS, auditQueryFrom } from '@/lib/pageFilters'
import { useLocation } from '@/lib/router'
import { api, ApiError, type AuditEntry, type AuditFilters, type AuditPage } from '@/lib/api'
import { useShortcuts } from '@/lib/shortcuts'
import { openAccount, openDiscordPerson, openInstance, openPerson } from '@/lib/subject'
import { useLiveStream } from '@/lib/useLiveStream'
import { cn } from '@/lib/utils'

/** A burst of facts -- a sweep, six clients reporting one join -- is one read, not one each. */
const SETTLE_MS = 400

/** Scrolled less than this is "at the top": new rows go straight in. Further down, they wait behind a count. */
const AT_TOP_PX = 40

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

const SOURCES = ['AuditLog', 'SyncDiff', 'Client', 'Discord', 'Manual', 'Modbot', 'Import']

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

  // New facts waiting above the top row while the list is scrolled (see below).
  const [pending, setPending] = useState(0)

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
        // What was counted as new was counted against the filters before these.
        setPending(0)
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

  // New facts, as they land. A fact that belongs on this list -- it passes the same filters --
  // is read back from the server and put above the top row, so what appears is exactly the entry
  // the log would show, never a guess at it from the event. With the list scrolled, rows arriving
  // under the reader would move what they are looking at, so they wait behind a count instead.
  const settle = useRef<number | undefined>(undefined)

  const prepend = useCallback(() => {
    api
      .audit(query)
      .then((page) => {
        setPages((current) => {
          const first = current[0]
          if (!first) return [page]

          const topId = first.entries[0]?.id ?? 0
          const fresh = page.entries.filter((entry) => entry.id > topId)
          return [{ ...first, entries: [...fresh, ...first.entries], coverage: page.coverage }, ...current.slice(1)]
        })
        setPending(0)
      })
      .catch(() => undefined)
  }, [query])

  useLiveStream(
    useCallback(
      (event: LiveEvent) => {
        if (!auditMatches(event, query)) return

        if (window.scrollY > AT_TOP_PX) {
          setPending((n) => n + 1)
          return
        }

        window.clearTimeout(settle.current)
        settle.current = window.setTimeout(prepend, SETTLE_MS)
      },
      [query, prepend],
    ),
  )

  useEffect(() => () => window.clearTimeout(settle.current), [])

  const showNew = () => {
    window.scrollTo({ top: 0 })
    prepend()
  }

  // Rows open on click into everything the entry holds. The entry the address names opens too,
  // because whoever followed that link came for that one.
  const [expanded, setExpanded] = useState<Set<number>>(() => new Set())
  const isOpen = (entry: AuditEntry) => expanded.has(entry.id) || String(entry.id) === factId
  const toggle = (entry: AuditEntry) =>
    setExpanded((current) => {
      const next = new Set(current)
      if (isOpen(entry)) next.delete(entry.id)
      else next.add(entry.id)
      return next
    })

  // `j`/`k` move down and up the rows; `Enter` opens the selected row; `o` opens what it is about.
  const { rowProps, selected } = useListSelection(entries.length, (i) => {
    const entry = entries[i]
    if (entry) toggle(entry)
  })

  useShortcuts([
    {
      keys: 'o',
      label: 'Open the person, account or instance the selected row is about',
      group: 'Lists',
      page: true,
      run: () => {
        const entry = selected === null ? undefined : entries[selected]
        if (!entry) return
        if (entry.subjectKind === 'Person')
          (entry.subjectPlatform.toLowerCase() === 'discord' ? openDiscordPerson : openPerson)(entry.subjectId)
        else if (entry.subjectKind === 'Account') openAccount(entry.subjectId)
        else if (entry.subjectKind === 'Instance' && entry.modbotInstanceId) openInstance(entry.modbotInstanceId)
      },
    },
  ])

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
        {pending > 0 && (
          <Button size="sm" variant="outline" onClick={showNew}>
            {pending} new
          </Button>
        )}
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
                    <th className="w-6 px-2 py-2" />
                    <th className="px-3 py-2 text-left font-normal">When</th>
                    <th className="px-3 py-2 text-left font-normal">Source</th>
                    <th className="px-3 py-2 text-left font-normal">What happened</th>
                  </tr>
                </thead>
                <tbody>
                  {entries.map((entry, i) => (
                    <Row
                      key={entry.id}
                      entry={entry}
                      marked={String(entry.id) === factId}
                      open={isOpen(entry)}
                      onToggle={() => toggle(entry)}
                      {...rowProps(i)}
                    />
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
 * One fact, as a sentence, and everything it holds underneath when the row is opened.
 *
 * It used to print the raw type and then the subject and the actor as ids in two more columns,
 * which is three things to read and none of them words. The sentence names who did what to whom
 * and where, and every name in it opens its own popup (see components/factSentence.tsx).
 * Clicking the row itself, anywhere that is not one of those names, opens the entry's detail
 * below it: every column, the diff, the snapshot, and the JSON.
 */
function Row({
  entry,
  marked,
  open,
  onToggle,
  ...rowAttributes
}: {
  entry: AuditEntry
  marked: boolean
  open: boolean
  onToggle: () => void
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
    <>
      <tr
        ref={row}
        {...rowAttributes}
        onClick={(e) => {
          // A name inside the sentence opens its popup; the rest of the row opens the entry.
          if ((e.target as HTMLElement).closest('a, button, summary')) return
          onToggle()
        }}
        aria-expanded={open}
        className={cn(
          'cursor-pointer border-b last:border-0 hover:bg-muted/40 data-[selected]:bg-accent/60',
          marked && 'bg-accent',
          open && 'border-b-0',
        )}
        style={{ borderBottomWidth: 'var(--hairline)' }}
      >
        <td className="px-2 align-top" style={{ height: 'var(--row-h)' }}>
          <ChevronRight
            className={cn('mt-2 size-3.5 text-muted-foreground transition-transform', open && 'rotate-90')}
            aria-hidden
          />
        </td>
        <td className="whitespace-nowrap px-3 align-top">
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
          {/* One decision, several facts. The row is the decision; opening it shows every fact. */}
          {entry.linked && entry.linked.length > 0 && (
            <Badge variant="secondary" className="ml-2 align-middle">
              {entry.linked.length + 1} facts
            </Badge>
          )}
        </td>
      </tr>
      {open && (
        <tr className="border-b last:border-0" style={{ borderBottomWidth: 'var(--hairline)' }}>
          <td colSpan={4} className="p-0">
            <EntryDetail entry={entry} />
          </td>
        </tr>
      )}
    </>
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
