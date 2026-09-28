import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { ChevronRight } from 'lucide-react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardFooter, CardHeader } from '@/components/ui/card'
import { EmptyRow } from '@/components/PanelGrid'
import { NarrowRow, NarrowRows, Table, Td, Th, Tr } from '@/components/ui/data-table'
import { EntryDetail } from '@/components/audit/EntryDetail'
import { mergeSameFacts, type FactRow } from '@/lib/factRows'
import { FilterBar } from '@/components/filters/FilterBar'
import { FactSentence } from '@/components/factSentence'
import { FactTime, SourceBadge } from '@/components/facts'
import { formatDay, sourceLabel } from '@/lib/format'
import { useFilters, type FilterChip, type FilterOption, type FilterProperty } from '@/lib/filters'
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
import { Empty } from '@/components/ListParts'

/** A burst of facts -- a sweep, six clients reporting one join -- is one read, not one each. */
const SETTLE_MS = 400

/** Scrolled less than this is "at the top": new rows go straight in. Further down, they wait behind a count. */
const AT_TOP_PX = 40

/**
 * What actually scrolls this page. The app scrolls inside its own `<main>`, not the window, so
 * `window.scrollY` stays 0 however far down the reader is -- and read that way, every new row went
 * straight in under them and pushed what they were reading down the screen.
 */
function scrollerOf(el: HTMLElement | null): HTMLElement {
  for (let node = el?.parentElement ?? null; node; node = node.parentElement) {
    const { overflowY } = getComputedStyle(node)
    if ((overflowY === 'auto' || overflowY === 'scroll') && node.scrollHeight > node.clientHeight) return node
  }
  return (document.scrollingElement as HTMLElement | null) ?? document.documentElement
}

/**
 * Newest first by when it happened, as the server orders it. New facts are merged in by that
 * order, not stacked on top by arrival: a client that sends its backlog late delivers facts
 * that happened an hour ago, and stacking them put 6:40 PM above 7:04 PM.
 */
const newestFirst = (a: AuditEntry, b: AuditEntry) =>
  b.occurredAt.localeCompare(a.occurredAt) || b.id - a.id

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

// `Import` is a legacy source: imports file each record under where it really came from now, and
// nothing writes it any more. It stays on the list, and in the defaults, so the rows that do hold
// it are still findable (import design §5.1).
const SOURCES = ['AuditLog', 'SyncDiff', 'Companion', 'Discord', 'Manual', 'Modbot', 'Import']

const NO_FILTERS: FilterChip[] = []

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

  // Opened at one entry, by a link that says nothing about filters: none, so that entry is on the
  // list whatever it is. A case file's link to its ban, a Chat answer's source chip.
  const [chips, setChips] = useFilters('audit', AUDIT_DEFAULTS, factId ? NO_FILTERS : undefined)

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
      // Opened at one entry: a minute past it, so the entry is near the top rather than a page
      // down, and the row is brought into view (see Row). A minute and not a second, because the
      // entry may be the second fact of a decision whose row is VRChat's record of it, which lands
      // seconds after Modbot's own. A day picked in the filter still wins.
      to: from.to ?? (openAt ? new Date(Date.parse(openAt.occurredAt) + 60_000).toISOString() : undefined),
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

  const entries = useMemo(() => pages.flatMap((p) => p.entries), [pages])
  // One thing two sources both recorded is one row with both badges (lib/factRows). Merged after
  // the pages are joined, so a pair split across "Load more" comes together once both are loaded.
  const rows = useMemo(() => mergeSameFacts(entries), [entries])
  const coverage = pages[0]?.coverage
  const next = pages[pages.length - 1]?.next

  // New facts, as they land. A fact that belongs on this list -- it passes the same filters --
  // is read back from the server and put above the top row, so what appears is exactly the entry
  // the log would show, never a guess at it from the event. With the list scrolled, rows arriving
  // under the reader would move what they are looking at, so they wait behind a count instead.
  const settle = useRef<number | undefined>(undefined)
  const listRef = useRef<HTMLDivElement>(null)

  const prepend = useCallback(() => {
    api
      .audit(query)
      .then((page) => {
        setPages((current) => {
          const first = current[0]
          if (!first) return [page]

          const known = new Set(first.entries.map((entry) => entry.id))
          const fresh = page.entries.filter((entry) => !known.has(entry.id))
          if (fresh.length === 0) return current
          const merged = [...fresh, ...first.entries].sort(newestFirst)
          return [{ ...first, entries: merged, coverage: page.coverage }, ...current.slice(1)]
        })
        setPending(0)
      })
      .catch(() => undefined)
  }, [query])

  useLiveStream(
    useCallback(
      (event: LiveEvent) => {
        if (!auditMatches(event, query)) return

        if (scrollerOf(listRef.current).scrollTop > AT_TOP_PX) {
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
    scrollerOf(listRef.current).scrollTo({ top: 0 })
    prepend()
  }

  // Rows open on click into everything the entry holds. The entry the address names opens too,
  // because whoever followed that link came for that one.
  const [expanded, setExpanded] = useState<Set<number>>(() => new Set())
  const isOpen = (row: FactRow) => expanded.has(row.entry.id) || isNamed(row, factId)
  const toggle = (row: FactRow) =>
    setExpanded((current) => {
      const next = new Set(current)
      if (isOpen(row)) next.delete(row.entry.id)
      else next.add(row.entry.id)
      return next
    })

  // `j`/`k` move down and up the rows; `Enter` opens the selected row; `o` opens what it is about.
  const { rowProps, selected } = useListSelection(rows.length, (i) => {
    const row = rows[i]
    if (row) toggle(row)
  })

  useShortcuts([
    {
      keys: 'o',
      label: 'Open the person, account or instance the selected row is about',
      group: 'Lists',
      page: true,
      keyboardOnly: true,
      run: () => {
        const entry = selected === null ? undefined : rows[selected]?.entry
        if (!entry) return
        if (entry.subjectKind === 'Person')
          (entry.subjectPlatform.toLowerCase() === 'discord' ? openDiscordPerson : openPerson)(entry.subjectId)
        else if (entry.subjectKind === 'Account') openAccount(entry.subjectId)
        else if (entry.subjectKind === 'Instance' && entry.modbotInstanceId) openInstance(entry.modbotInstanceId)
      },
    },
    ...(pending > 0 ? [{ label: `${pending} new`, group: 'Page' as const, page: true, run: showNew }] : []),
  ])

  // Names for the ids the About and World chips hold: the ones picked from a search, and the ones
  // the rows on screen already carry, so a chip read back from the address shows a name as well.
  const [picked, setPicked] = useState<Record<string, string>>({})
  const remember = useCallback(
    (option: FilterOption) => setPicked((current) => ({ ...current, [option.value]: option.label })),
    [],
  )
  const names = useMemo(() => {
    const people = new Map<string, string>()
    const worlds = new Map<string, string>()
    for (const entry of entries) {
      if (entry.subjectName && entry.subjectKind === 'Person') people.set(entry.subjectId, entry.subjectName)
      if (entry.worldId && entry.worldName) worlds.set(entry.worldId, entry.worldName)
    }
    for (const [id, name] of Object.entries(picked)) {
      people.set(id, name)
      worlds.set(id, name)
    }
    const known = (map: Map<string, string>, chip: string) =>
      chips
        .filter((c) => c.property === chip)
        .flatMap((c) => c.values)
        .filter((id) => map.has(id))
        .map((id) => ({ value: id, label: map.get(id)! }))
    return { people: known(people, 'subject'), worlds: known(worlds, 'world') }
  }, [entries, picked, chips])

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
      {
        id: 'subject',
        label: 'About',
        kind: 'search',
        placeholder: 'Name or id',
        options: names.people,
        search: searchPeople,
        onPick: remember,
      },
      {
        id: 'world',
        label: 'World',
        kind: 'search',
        placeholder: 'Name or id',
        options: names.worlds,
        search: searchWorlds,
        onPick: remember,
      },
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
    [filters, names, remember],
  )

  if (error) return <Empty tone="danger">{error}</Empty>

  // Nothing has been read yet. Later reads keep the page on screen until they land (see above).
  if (pages.length === 0) return <Empty>Loading…</Empty>

  return (
    <div ref={listRef} className="flex flex-col gap-3">
      <FilterBar properties={properties} chips={chips} onChange={setChips}>
        {pending > 0 && (
          <Button size="sm" variant="outline" onClick={showNew}>
            {pending} new
          </Button>
        )}
      </FilterBar>

      <Card>
          {coverage?.oldestFact && (
            <CardHeader>
              <Coverage coverage={coverage} />
            </CardHeader>
          )}
          {entries.length === 0 && !loading ? (
            <EmptyRow>No entries match these filters.</EmptyRow>
          ) : (
            <Table
              nameColumn={3}
              narrow={
                <NarrowRows>
                  {rows.map((row) => (
                    <NarrowFact
                      key={row.entry.id}
                      row={row}
                      marked={isNamed(row, factId)}
                      open={isOpen(row)}
                      onToggle={() => toggle(row)}
                    />
                  ))}
                </NarrowRows>
              }
              head={
                <>
                  <Th className="w-0 pr-0" />
                  <Th>When</Th>
                  <Th>Source</Th>
                  <Th>What happened</Th>
                </>
              }
            >
              {rows.map((row, i) => (
                <Row
                  key={row.entry.id}
                  row={row}
                  marked={isNamed(row, factId)}
                  open={isOpen(row)}
                  onToggle={() => toggle(row)}
                  {...rowProps(i)}
                />
              ))}
            </Table>
          )}

          <CardFooter className="gap-3 text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
            <span>
              <span className="font-mono">{rows.length}</span> {rows.length === 1 ? 'entry' : 'entries'} shown
            </span>
            <span className="flex-1" />
            {next && (
              <Button size="xs" variant="outline" disabled={loading} onClick={more}>
                {loading ? 'Loading…' : 'Load more'}
              </Button>
            )}
          </CardFooter>
      </Card>
    </div>
  )
}

/**
 * Whether a row is the entry the address names. A decision's second fact is shown inside the row
 * for the decision rather than on its own (spec 5.3.2), so a link to it -- a case file's to the ban
 * pressed in Modbot -- names the row that holds it. So does a link to another source's record of
 * the same fact, which is shown in the same row.
 */
function isNamed(row: FactRow, factId: string | null): boolean {
  if (!factId) return false
  return [row.entry, ...row.also].some(
    (entry) => String(entry.id) === factId || (entry.linked ?? []).some((fact) => String(fact.id) === factId),
  )
}

/** The command palette's search, narrowed to people: VRChat and Discord, since either can be what an entry is about. */
async function searchPeople(words: string): Promise<FilterOption[]> {
  const found = await api.search(words)
  return [
    ...found.people.map((p) => ({ value: p.userId, label: p.displayName ?? p.userId, detail: p.displayName ? p.userId : null })),
    ...found.discordPeople.map((p) => ({ value: p.userId, label: p.displayName, detail: `Discord · @${p.username}` })),
  ]
}

/** The command palette's search, narrowed to worlds. */
async function searchWorlds(words: string): Promise<FilterOption[]> {
  const found = await api.search(words)
  return found.worlds.map((w) => ({ value: w.worldId, label: w.name ?? w.worldId, detail: w.name ? w.worldId : null }))
}

/**
 * Scrolls the row the address names to the middle of the screen, once. Both forms of a row ask:
 * the one that is hidden has no place on the screen, so only the one being read moves.
 */
function useBroughtIntoView<T extends HTMLElement>(marked: boolean) {
  const row = useRef<T>(null)
  const brought = useRef(false)

  useEffect(() => {
    if (!marked || brought.current) return
    brought.current = true
    row.current?.scrollIntoView({ block: 'center' })
  }, [marked])

  return row
}

/** What happened, as a sentence, and how many facts it stands for when it is one decision of several. */
function Sentence({ entry }: { entry: AuditEntry }) {
  return (
    <>
      {/* Payload text is user-controlled (spec 5.3). The sentence renders it as text, never as
          markup. */}
      <FactSentence entry={entry} />
      {/* One decision, several facts. The row is the decision; opening it shows every fact. */}
      {entry.linked && entry.linked.length > 0 && (
        <Badge variant="secondary" className="ml-2 align-middle">
          <span className="font-mono">{entry.linked.length + 1}</span> facts
        </Badge>
      )}
    </>
  )
}

/**
 * One fact on a phone: the sentence on top, because it is what the page is for, then the sources
 * and when, muted. A tap opens the same detail the table's row does, under it.
 */
function NarrowFact({
  row: { entry, also },
  marked,
  open,
  onToggle,
}: {
  row: FactRow
  marked: boolean
  open: boolean
  onToggle: () => void
}) {
  const row = useBroughtIntoView<HTMLLIElement>(marked)

  return (
    <NarrowRow
      ref={row}
      main={<Sentence entry={entry} />}
      facts={[
        <span key="sources" className="inline-flex gap-1">
          {[entry, ...also].map((seen) => (
            <SourceBadge key={seen.id} source={seen.source} />
          ))}
        </span>,
        <span key="when" className="inline-flex items-baseline gap-2 font-mono">
          <FactTime entry={entry} />
          <span>{formatDay(entry.occurredAt)}</span>
        </span>,
      ]}
      onOpen={onToggle}
      open={open}
      hasLinks
      className={cn(marked && 'bg-accent')}
    >
      {open && (
        <div>
          <EntryDetail entry={entry} />
          {also.map((seen) => (
            <EntryDetail key={seen.id} entry={seen} around={false} />
          ))}
        </div>
      )}
    </NarrowRow>
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
 *
 * A fact two sources both recorded is one row with both badges, and opening it shows each
 * source's own entry, one under the other, so neither record is hidden by the merge.
 */
function Row({
  row: { entry, also },
  marked,
  open,
  onToggle,
  ...rowAttributes
}: {
  row: FactRow
  marked: boolean
  open: boolean
  onToggle: () => void
  'data-row-index': number
  'data-selected': boolean | undefined
  'aria-selected': boolean
}) {
  const row = useBroughtIntoView<HTMLTableRowElement>(marked)

  return (
    <>
      <Tr
        ref={row}
        {...rowAttributes}
        onClick={(e) => {
          // A name inside the sentence opens its popup; the rest of the row opens the entry.
          if ((e.target as HTMLElement).closest('a, button, summary')) return
          onToggle()
        }}
        aria-expanded={open}
        className={cn('cursor-pointer hover:bg-muted/40 data-[selected]:bg-accent/60', marked && 'bg-accent')}
      >
        <Td className="pr-0" data-only-desk>
          <ChevronRight
            className={cn('size-3.5 text-muted-foreground transition-transform', open && 'rotate-90')}
            aria-hidden
          />
        </Td>
        <Td>
          <div className="leading-tight">
            <FactTime entry={entry} withDay />
          </div>
        </Td>
        <Td>
          <div className="flex flex-wrap gap-1">
            {[entry, ...also].map((seen) => (
              <SourceBadge key={seen.id} source={seen.source} />
            ))}
          </div>
        </Td>
        <Td className="max-w-3xl min-w-[20rem] whitespace-normal" title={entry.type}>
          <Sentence entry={entry} />
        </Td>
      </Tr>
      {/* No line of its own above: the detail belongs to the row over it. The next row's line
          closes it off. */}
      {open && (
        <tr>
          <td colSpan={4} className="p-0">
            <EntryDetail entry={entry} />
            {also.map((seen) => (
              <EntryDetail key={seen.id} entry={seen} around={false} />
            ))}
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
      Oldest recorded entry: <span className="font-mono">{formatDay(coverage.oldestFact)}</span>
      {coverage.catchUpComplete ? '' : ' · catch-up still running'}
    </span>
  )
}
