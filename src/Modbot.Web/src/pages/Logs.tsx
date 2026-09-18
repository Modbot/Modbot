import { useCallback, useEffect, useMemo, useState } from 'react'
import { ChevronDown, ChevronRight } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { JsonView } from '@/components/JsonView'
import { api, ApiError, type LogFilters, type LogLevel, type LogLine, type LogPage } from '@/lib/api'
import { wholeEntry } from '@/lib/logEntry'
import { cn } from '@/lib/utils'
import { Empty, Select } from './Members'

const PAGE_SIZE = 100

/** The colour each level is written in. Errors have to be findable by eye in a wall of text. */
const LEVEL_TONE: Record<LogLevel, string> = {
  Verbose: 'text-muted-foreground',
  Debug: 'text-muted-foreground',
  Information: 'text-foreground',
  Warning: 'text-warn',
  Error: 'text-destructive',
  Fatal: 'text-destructive',
}

/**
 * Modbot's own log, read from the database.
 *
 * The console, the log files and Seq all still carry it. This page is for the deployment that has
 * none of them to hand: a container whose disk is thrown away on redeploy, no Seq server, and a
 * hosting dashboard that shows the last few hundred lines with no way to search them.
 *
 * Outbound API traffic is not in the table, so it is not on this page either.
 */
export function Logs() {
  const [level, setLevel] = useState<LogLevel | ''>('')
  const [source, setSource] = useState('')
  const [area, setArea] = useState('')
  const [typed, setTyped] = useState('')
  const [text, setText] = useState('')
  const [from, setFrom] = useState('')
  const [to, setTo] = useState('')

  const [pages, setPages] = useState<LogPage[]>([])
  const [filters, setFilters] = useState<LogFilters | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)
  const [open, setOpen] = useState<number | null>(null)
  const [reloads, setReloads] = useState(0)

  // Typing waits a moment before it asks, so a word typed at speed is one request, not five.
  useEffect(() => {
    const timer = setTimeout(() => setText(typed.trim()), 300)
    return () => clearTimeout(timer)
  }, [typed])

  const query = useMemo(
    () => ({
      level: level || undefined,
      source: source || undefined,
      area: area || undefined,
      text: text || undefined,
      from: from ? `${from}T00:00:00Z` : undefined,
      to: to ? new Date(Date.parse(`${to}T00:00:00Z`) + 86_400_000).toISOString() : undefined,
      limit: PAGE_SIZE,
    }),
    [level, source, area, text, from, to],
  )

  useEffect(() => {
    let cancelled = false

    // The previous page stays on screen until the new one lands, so a filter change does not
    // blank the log for the moment in between.
    api
      .logs(query)
      .then((page) => {
        if (cancelled) return
        setPages([page])
        setError(null)
      })
      .catch((e: unknown) => {
        if (cancelled) return
        setError(
          e instanceof ApiError && e.status === 403
            ? 'You do not have permission to read the log.'
            : 'Could not load the log.',
        )
      })
      .finally(() => {
        if (!cancelled) setLoading(false)
      })

    return () => {
      cancelled = true
    }
  }, [query, reloads])

  useEffect(() => {
    api
      .logFilters()
      .then(setFilters)
      .catch(() => setFilters(null))
  }, [reloads])

  const more = useCallback(() => {
    const last = pages[pages.length - 1]
    if (!last?.next) return

    setLoading(true)
    api
      .logs({ ...query, before: last.next })
      .then((page) => setPages((p) => [...p, page]))
      .catch(() => setError('Could not load more.'))
      .finally(() => setLoading(false))
  }, [pages, query])

  if (error) return <Empty>{error}</Empty>

  const lines = pages.flatMap((p) => p.lines)
  const next = pages[pages.length - 1]?.next ?? null

  return (
    <Card>
      <CardContent className="p-0">
        <div
          className="flex flex-wrap items-center gap-2 border-b px-3 py-2"
          style={{ borderBottomWidth: 'var(--hairline)', fontSize: 'var(--text-small)' }}
        >
          <Input
            value={typed}
            onChange={(e) => setTyped(e.target.value)}
            placeholder="Search the text"
            className="h-8 w-64"
            aria-label="Search the log"
          />

          <Select value={level} onChange={(v) => setLevel(v as LogLevel | '')} aria-label="Level">
            <option value="">Any level</option>
            {(filters?.levels ?? []).map((l) => (
              <option key={l} value={l}>
                {l} and above
              </option>
            ))}
          </Select>

          <Select value={source} onChange={setSource} aria-label="Source">
            <option value="">Any source</option>
            {(filters?.sources ?? []).map((s) => (
              <option key={s} value={s}>
                {shortSource(s)}
              </option>
            ))}
          </Select>

          {(filters?.areas.length ?? 0) > 0 && (
            <Select value={area} onChange={setArea} aria-label="Area">
              <option value="">Any area</option>
              {(filters?.areas ?? []).map((a) => (
                <option key={a} value={a}>
                  {a}
                </option>
              ))}
            </Select>
          )}

          <Input
            type="date"
            value={from}
            onChange={(e) => setFrom(e.target.value)}
            className="h-8 w-36"
            aria-label="From"
          />
          <Input
            type="date"
            value={to}
            onChange={(e) => setTo(e.target.value)}
            className="h-8 w-36"
            aria-label="To"
          />

          <Button variant="outline" size="xs" onClick={() => setReloads((n) => n + 1)}>
            Refresh
          </Button>

          <span className="flex-1" />
          <span className="text-muted-foreground tabular-nums">
            {(filters?.stored ?? 0).toLocaleString()} stored
          </span>
        </div>

        {lines.length === 0 ? (
          <div className="py-10 text-center text-muted-foreground">
            {loading ? 'Loading…' : 'Nothing to show.'}
          </div>
        ) : (
          <ul>
            {lines.map((line) => (
              <LogRow
                key={line.id}
                line={line}
                open={open === line.id}
                onToggle={() => setOpen(open === line.id ? null : line.id)}
              />
            ))}
          </ul>
        )}

        {next && (
          <div
            className="flex items-center gap-2 border-t px-3 py-2"
            style={{ borderTopWidth: 'var(--hairline)', fontSize: 'var(--text-small)' }}
          >
            <Button variant="outline" size="xs" disabled={loading} onClick={more}>
              {loading ? 'Loading…' : 'Load more'}
            </Button>
          </div>
        )}
      </CardContent>
    </Card>
  )
}

function LogRow({ line, open, onToggle }: { line: LogLine; open: boolean; onToggle: () => void }) {
  const Chevron = open ? ChevronDown : ChevronRight

  return (
    <li className="border-b last:border-b-0" style={{ borderBottomWidth: 'var(--hairline)' }}>
      <button
        type="button"
        onClick={onToggle}
        className="flex w-full items-baseline gap-3 px-3 py-1.5 text-left hover:bg-muted/50"
        style={{ fontSize: 'var(--text-small)' }}
      >
        <Chevron className="size-3.5 shrink-0 self-center text-muted-foreground" aria-hidden />
        <span className="shrink-0 tabular-nums text-muted-foreground">{when(line.at)}</span>
        <span className={cn('w-16 shrink-0 font-medium', LEVEL_TONE[line.level])}>{line.level}</span>
        <span className="min-w-0 flex-1 truncate">{line.message}</span>
        <span className="hidden shrink-0 text-muted-foreground sm:inline">
          {shortSource(line.source)}
        </span>
      </button>

      {open && (
        <div className="px-3 pb-3 pl-10" style={{ fontSize: 'var(--text-small)' }}>
          <div className="whitespace-pre-wrap break-words">{line.message}</div>

          {/* The record below holds all of this, but a JSON document is read, not scanned. One
              line of it stays in plain text so the eye can take in when and where at a glance. */}
          <div className="mt-1 flex flex-wrap items-center gap-x-2 text-muted-foreground">
            <span className="tabular-nums text-foreground">{new Date(line.at).toLocaleString()}</span>
            {line.source && (
              <>
                <span aria-hidden>·</span>
                <span className="break-all text-foreground">{line.source}</span>
              </>
            )}
            {line.area && (
              <>
                <span aria-hidden>·</span>
                <span className="text-foreground">{line.area}</span>
              </>
            )}
          </div>

          <JsonView className="mt-2" title="Entry" value={wholeEntry(line)} />

          {/* Shown here as well as in the record above: a stack trace read through JSON escaping
              is one long line with `\n` written in it, and a stack trace is the thing on this page
              most likely to be read line by line. The record still carries it, so what the copy
              button gives is the whole line. */}
          {line.exception && (
            <pre className="mt-2 overflow-x-auto rounded-md bg-destructive/10 p-2 font-mono text-destructive">
              {line.exception}
            </pre>
          )}
        </div>
      )}
    </li>
  )
}

/** `Modbot.VRChat.Sync.AuditLogProducer` reads as `AuditLogProducer` in a narrow column. */
function shortSource(source: string | null): string {
  if (!source) return ''
  const dot = source.lastIndexOf('.')
  return dot === -1 ? source : source.slice(dot + 1)
}

function when(at: string): string {
  const date = new Date(at)
  return `${String(date.getHours()).padStart(2, '0')}:${String(date.getMinutes()).padStart(2, '0')}:${String(date.getSeconds()).padStart(2, '0')}`
}
