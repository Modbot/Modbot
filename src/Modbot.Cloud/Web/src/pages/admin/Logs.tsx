import { useCallback, useEffect, useMemo, useState } from 'react'
import { Button } from '@/components/ui/button'
import { Card } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { api, type LogLevel, type LogLinePage, type LogLineView, type LogSenderView } from '@/lib/api'
import { when } from '@/lib/format'
import { cn } from '@/lib/utils'
import { useAdminLoad } from '@/lib/useLoad'

const PAGE = 100

const LEVELS: LogLevel[] = ['Verbose', 'Debug', 'Information', 'Warning', 'Error', 'Fatal']

const LEVEL_TONE: Record<LogLevel, string> = {
  Verbose: 'text-muted-foreground',
  Debug: 'text-muted-foreground',
  Information: 'text-foreground',
  Warning: 'text-warn',
  Error: 'text-destructive',
  Fatal: 'text-destructive',
}

/**
 * The log lines Modbot deployments sent Cloud.
 *
 * Everything here is somebody else's text, rendered as text and never as markup. The filters are
 * the same ones a deployment's own Logs page offers, so the two read the same way.
 */
export function Logs() {
  const [installId, setInstallId] = useState('')
  const [level, setLevel] = useState<LogLevel | ''>('')
  const [typed, setTyped] = useState('')
  const [text, setText] = useState('')

  const [pages, setPages] = useState<LogLinePage[]>([])
  const [failure, setFailure] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [open, setOpen] = useState<number | null>(null)

  const senders = useAdminLoad(() => api.logSenders(), [])

  useEffect(() => {
    const timer = setTimeout(() => setText(typed.trim()), 300)
    return () => clearTimeout(timer)
  }, [typed])

  const query = useMemo(
    () => ({
      installId: installId || undefined,
      level: level || undefined,
      text: text || undefined,
      limit: PAGE,
    }),
    [installId, level, text],
  )

  useEffect(() => {
    let cancelled = false
    setBusy(true)

    api
      .logs(query)
      .then((page) => {
        if (cancelled) return
        setPages([page])
        setFailure(null)
      })
      .catch(() => {
        if (!cancelled) setFailure('Could not load the log.')
      })
      .finally(() => {
        if (!cancelled) setBusy(false)
      })

    return () => {
      cancelled = true
    }
  }, [query])

  const more = useCallback(() => {
    const last = pages[pages.length - 1]
    if (!last?.next) return

    setBusy(true)
    api
      .logs({ ...query, before: last.next })
      .then((page) => setPages((p) => [...p, page]))
      .catch(() => setFailure('Could not load more.'))
      .finally(() => setBusy(false))
  }, [pages, query])

  const lines = pages.flatMap((p) => p.items)
  const next = pages[pages.length - 1]?.next ?? null

  return (
    <>
      <h1 className="font-display text-lg">Logs</h1>

      <Card className="gap-0 py-0">
        <div className="flex flex-wrap items-center gap-2 border-b px-4 py-3">
          <Input
            value={typed}
            onChange={(e) => setTyped(e.target.value)}
            placeholder="Search the text"
            className="h-9 w-64"
            aria-label="Search the log"
          />

          <Picker value={installId} onChange={setInstallId} aria-label="Deployment">
            <option value="">Any deployment</option>
            {(senders.data?.items ?? []).map((sender: LogSenderView) => (
              <option key={sender.installId} value={sender.installId}>
                {sender.installId.slice(0, 8)} · {sender.version}
              </option>
            ))}
          </Picker>

          <Picker value={level} onChange={(v) => setLevel(v as LogLevel | '')} aria-label="Level">
            <option value="">Any level</option>
            {LEVELS.map((l) => (
              <option key={l} value={l}>
                {l} and above
              </option>
            ))}
          </Picker>
        </div>

        {failure ? (
          <p className="px-4 py-6 text-destructive">{failure}</p>
        ) : lines.length === 0 ? (
          <p className="px-4 py-6 text-center text-muted-foreground">{busy ? 'Loading' : 'No lines'}</p>
        ) : (
          <ul>
            {lines.map((line) => (
              <Line
                key={line.id}
                line={line}
                open={open === line.id}
                onToggle={() => setOpen(open === line.id ? null : line.id)}
              />
            ))}
          </ul>
        )}

        {next && (
          <div className="border-t px-4 py-3">
            <Button variant="outline" size="sm" disabled={busy} onClick={more}>
              {busy ? 'Loading' : 'Load more'}
            </Button>
          </div>
        )}
      </Card>
    </>
  )
}

function Line({ line, open, onToggle }: { line: LogLineView; open: boolean; onToggle: () => void }) {
  const properties = open ? pretty(line.properties) : null

  return (
    <li className="border-b last:border-b-0">
      <button
        type="button"
        onClick={onToggle}
        className="flex w-full items-baseline gap-3 px-4 py-1.5 text-left hover:bg-secondary/60"
      >
        <span className="shrink-0 font-mono text-muted-foreground">{clock(line.at)}</span>
        <span className={cn('w-16 shrink-0 font-medium', LEVEL_TONE[line.level])}>{line.level}</span>
        <span className="min-w-0 flex-1 truncate">{line.message}</span>
        <span className="hidden shrink-0 font-mono text-muted-foreground sm:inline">
          {line.installId.slice(0, 8)}
        </span>
      </button>

      {open && (
        <div className="px-4 pb-3 pl-10">
          <p className="whitespace-pre-wrap break-words">{line.message}</p>

          <dl className="mt-2 grid grid-cols-[auto_1fr] gap-x-3 gap-y-0.5 text-muted-foreground">
            <dt>Written</dt>
            <dd className="text-foreground">{when(line.at)}</dd>
            <dt>Received</dt>
            <dd className="text-foreground">{when(line.receivedAt)}</dd>
            <dt>Deployment</dt>
            <dd className="font-mono text-foreground">{line.installId}</dd>
            {line.version && (
              <>
                <dt>Version</dt>
                <dd className="font-mono text-foreground">{line.version}</dd>
              </>
            )}
            {line.source && (
              <>
                <dt>Source</dt>
                <dd className="break-all text-foreground">{line.source}</dd>
              </>
            )}
            {line.area && (
              <>
                <dt>Area</dt>
                <dd className="text-foreground">{line.area}</dd>
              </>
            )}
          </dl>

          {properties && (
            <pre className="mt-2 overflow-x-auto rounded-md bg-secondary p-2 font-mono">{properties}</pre>
          )}

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

function Picker({
  value,
  onChange,
  children,
  ...rest
}: {
  value: string
  onChange: (value: string) => void
  children: React.ReactNode
} & Omit<React.SelectHTMLAttributes<HTMLSelectElement>, 'value' | 'onChange' | 'children'>) {
  return (
    <select
      {...rest}
      value={value}
      onChange={(e) => onChange(e.target.value)}
      className="h-9 rounded-md border border-input bg-background px-2"
    >
      {children}
    </select>
  )
}

function clock(iso: string): string {
  const date = new Date(iso)
  if (Number.isNaN(date.getTime())) return ''
  return date.toLocaleTimeString()
}

function pretty(json: string): string | null {
  if (!json || json === '{}') return null
  try {
    const parsed: unknown = JSON.parse(json)
    if (parsed && typeof parsed === 'object' && Object.keys(parsed).length === 0) return null
    return JSON.stringify(parsed, null, 2)
  } catch {
    return json
  }
}
