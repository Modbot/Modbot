import { useCallback, useEffect, useMemo, useState } from 'react'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { FactSentence } from '@/components/factSentence'
import { FactTime, SourceBadge } from '@/components/facts'
import { formatDay, sourceLabel } from '@/lib/format'
import { api, ApiError, type AuditEntry, type AuditFilters, type AuditPage } from '@/lib/api'
import { cn } from '@/lib/utils'

/**
 * The merged timeline (spec 5.9.5).
 *
 * One log with source chips on by default, because the questions people actually ask span sources:
 * *"who changed the ban threshold just before these bans?"* is unanswerable in either log alone.
 * Filtering to one source gives back the old separate-logs view whenever that is what is wanted.
 *
 * The filters offered here come from the server and are already narrowed to what this account may
 * read. That is a convenience, not the enforcement — the server filters every query regardless,
 * so a hand-edited request gains nothing.
 */

const SOURCES = ['AuditLog', 'SyncDiff', 'Client', 'Discord', 'Manual', 'Modbot']

export function AuditLog() {
  const [filters, setFilters] = useState<AuditFilters | null>(null)
  const [pages, setPages] = useState<AuditPage[]>([])
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)

  const [types, setTypes] = useState<string[]>([])
  const [sources, setSources] = useState<string[]>([])
  const [actor, setActor] = useState('')
  const [subject, setSubject] = useState('')
  const [from, setFrom] = useState('')
  const [to, setTo] = useState('')

  const query = useMemo(
    () => ({
      type: types.length > 0 ? types : undefined,
      source: sources.length > 0 ? sources : undefined,
      actor: actor.trim() || undefined,
      subject: subject.trim() || undefined,
      from: from ? `${from}T00:00:00Z` : undefined,
      // The server's upper bound is exclusive, so the day the operator picked is included by
      // asking for midnight at the start of the next one. Sending the picked day directly would
      // silently drop everything that happened on it, which is the filter people would trust
      // least once they noticed and would not notice at all before that.
      to: to ? new Date(Date.parse(`${to}T00:00:00Z`) + 86_400_000).toISOString() : undefined,
      limit: 50,
    }),
    [types, sources, actor, subject, from, to],
  )

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

  if (error) {
    return (
      <Card>
        <CardContent className="py-10 text-center text-muted-foreground">{error}</CardContent>
      </Card>
    )
  }

  return (
    <div className="flex flex-col gap-3">
      <Filters
        filters={filters}
        types={types}
        setTypes={setTypes}
        sources={sources}
        setSources={setSources}
        actor={actor}
        setActor={setActor}
        subject={subject}
        setSubject={setSubject}
        from={from}
        setFrom={setFrom}
        to={to}
        setTo={setTo}
      />

      {coverage && <Coverage coverage={coverage} />}

      <Card>
        <CardContent className="p-0">
          {entries.length === 0 && !loading ? (
            <div className="py-10 text-center text-muted-foreground">
              <div className="font-medium text-foreground">Nothing to show</div>
              <p className="mx-auto mt-1 max-w-md" style={{ fontSize: 'var(--text-small)' }}>
                No recorded fact matches these filters. That is not the same as nothing having
                happened — Modbot only holds what it has synced.
              </p>
            </div>
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
                  {entries.map((entry) => (
                    <Row key={entry.id} entry={entry} />
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
function Row({ entry }: { entry: AuditEntry }) {
  return (
    <tr className="border-b last:border-0 hover:bg-muted/40" style={{ borderBottomWidth: 'var(--hairline)' }}>
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
    <p className="px-1 text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
      Oldest recorded entry: {formatDay(coverage.oldestFact)}.{' '}
      {coverage.catchUpComplete
        ? 'Modbot has read back as far as VRChat’s own audit log still goes.'
        : 'Modbot is still walking backwards through VRChat’s audit log, so this start date is still moving.'}
    </p>
  )
}

function Filters(props: {
  filters: AuditFilters | null
  types: string[]
  setTypes: (v: string[]) => void
  sources: string[]
  setSources: (v: string[]) => void
  actor: string
  setActor: (v: string) => void
  subject: string
  setSubject: (v: string) => void
  from: string
  setFrom: (v: string) => void
  to: string
  setTo: (v: string) => void
}) {
  const { filters } = props
  const [open, setOpen] = useState(false)

  const toggle = (list: string[], set: (v: string[]) => void, value: string) =>
    set(list.includes(value) ? list.filter((v) => v !== value) : [...list, value])

  return (
    <Card>
      <CardContent className="flex flex-col gap-3 py-3">
        <div className="flex flex-wrap items-center gap-2">
          <span className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
            Source
          </span>
          {SOURCES.map((source) => (
            <Chip
              key={source}
              active={props.sources.length === 0 || props.sources.includes(source)}
              onClick={() => toggle(props.sources, props.setSources, source)}
            >
              {sourceLabel(source)}
            </Chip>
          ))}
          <span className="flex-1" />
          <Button variant="ghost" size="sm" onClick={() => setOpen((o) => !o)}>
            {open ? 'Fewer filters' : 'More filters'}
          </Button>
        </div>

        {props.sources.length === 0 && (
          <p className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
            All sources, merged. The questions people ask span them.
          </p>
        )}

        {open && (
          <div className="flex flex-col gap-3 border-t pt-3" style={{ borderTopWidth: 'var(--hairline)' }}>
            <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
              <Field label="Subject id" value={props.subject} onChange={props.setSubject} placeholder="usr_…" />
              <Field label="Actor id" value={props.actor} onChange={props.setActor} placeholder="usr_…" />
              <Field label="From" value={props.from} onChange={props.setFrom} type="date" />
              <Field label="To" value={props.to} onChange={props.setTo} type="date" />
            </div>

            {filters && filters.actors.length > 0 && (
              <div className="flex flex-wrap items-center gap-2">
                <span className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
                  Recent actors
                </span>
                {filters.actors.slice(0, 8).map((a) => (
                  <Chip
                    key={a.id}
                    active={props.actor === a.id}
                    onClick={() => props.setActor(props.actor === a.id ? '' : a.id)}
                  >
                    {a.name ?? a.id} · {a.actions}
                  </Chip>
                ))}
              </div>
            )}

            {filters && (
              <div className="flex flex-wrap items-center gap-2">
                <span className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
                  Type
                </span>
                {filters.types.map((t) => (
                  <Chip
                    key={t.value}
                    active={props.types.includes(t.value)}
                    onClick={() => toggle(props.types, props.setTypes, t.value)}
                  >
                    {t.label}
                  </Chip>
                ))}
              </div>
            )}

            {filters && !filters.canViewOperational && (
              <p className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
                This account can read moderation history but not Modbot’s operational record —
                logins, settings changes and sync failures are a separate permission, and none of
                them appear above.
              </p>
            )}
            {filters && !filters.canViewModeration && (
              <p className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
                This account can read Modbot’s operational record but not moderation history, so no
                bans, kicks or role changes appear above.
              </p>
            )}
          </div>
        )}
      </CardContent>
    </Card>
  )
}

function Chip({
  active,
  onClick,
  children,
}: {
  active: boolean
  onClick: () => void
  children: React.ReactNode
}) {
  return (
    <button
      type="button"
      aria-pressed={active}
      onClick={onClick}
      className={cn(
        'inline-flex items-center rounded-full border px-2.5 font-medium transition-colors',
        active
          ? 'border-transparent bg-accent text-accent-foreground'
          : 'text-muted-foreground hover:text-foreground',
      )}
      style={{
        fontSize: 'var(--text-small)',
        borderWidth: 'var(--hairline)',
        height: 'calc(var(--control-h) - 6px)',
      }}
    >
      {children}
    </button>
  )
}

function Field({
  label,
  value,
  onChange,
  placeholder,
  type,
}: {
  label: string
  value: string
  onChange: (v: string) => void
  placeholder?: string
  type?: string
}) {
  return (
    <label className="flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
      <span className="text-muted-foreground">{label}</span>
      <Input
        type={type}
        placeholder={placeholder}
        value={value}
        onChange={(e) => onChange(e.target.value)}
      />
    </label>
  )
}
