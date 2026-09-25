import { useCallback, useEffect, useState } from 'react'
import { EmptyRow } from '@/components/PanelGrid'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Chip } from '@/components/ui/chip'
import { Dialog, DialogContent } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { Select } from '@/components/ui/select'
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@/components/ui/table'
import {
  api,
  ApiError,
  type AiCall,
  type AiCallDetail,
  type AiCallFilters,
  type AiCallLogPage,
} from '@/lib/api'
import { count, money } from '@/lib/aiSpend'
import { cn } from '@/lib/utils'
import { Outcome } from '../fields'
import { SettingsCard, SettingsSection } from '../SettingsCard'

/** `#ai/calls/<id>` opens that call, which is where a flag's "AI call" link goes. */
function callFromHash(): string | null {
  const parts = window.location.hash.slice(1).split('/')
  return parts[0] === 'ai' && parts[1] === 'calls' && parts[2] ? parts[2] : null
}

const noFilters: AiCallFilters = { feature: '', outcome: '', model: '', from: '', to: '', flagged: false }

const failure = (e: unknown) =>
  e instanceof ApiError && e.status === 403
    ? 'You do not have permission to see the call log.'
    : e instanceof ApiError
      ? e.message
      : 'Could not reach the Modbot server.'

const took = (ms: number) => (ms < 1000 ? `${ms} ms` : `${(ms / 1000).toFixed(1)} s`)

const tone = (outcome: string) =>
  outcome === 'answered' ? undefined : outcome === 'limited' ? 'text-warn' : 'text-destructive'

/** Settings → AI → Call log. */
export function AiCallLogSettings() {
  const [page, setPage] = useState<AiCallLogPage | null>(null)
  const [more, setMore] = useState<AiCall[]>([])
  const [filters, setFilters] = useState<AiCallFilters>(noFilters)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [open, setOpen] = useState<string | null>(callFromHash)

  const set = (patch: Partial<AiCallFilters>) => setFilters((all) => ({ ...all, ...patch }))

  const load = useCallback(
    () =>
      api
        .aiCalls(filters)
        .then((d) => {
          setPage(d)
          setMore([])
          setError(null)
        })
        .catch((e: unknown) => setError(failure(e))),
    [filters],
  )

  useEffect(() => {
    void load()
  }, [load])

  const next = () => {
    if (page?.next == null) return

    setBusy(true)

    api
      .aiCalls(filters, page.next)
      .then((d) => {
        setMore((rows) => [...rows, ...d.calls])
        setPage((all) => (all ? { ...all, next: d.next } : all))
      })
      .catch((e: unknown) => setError(failure(e)))
      .finally(() => setBusy(false))
  }

  const rows = [...(page?.calls ?? []), ...more]

  return (
    <SettingsSection id="ai-calls" title="AI call log">
      <SettingsCard
        title="Call log"
        span={12}
        flush
        footer={
          (page?.next != null || error) && (
            <>
              {page?.next != null && (
                <Button size="xs" variant="outline" disabled={busy} onClick={next}>
                  {busy ? 'Loading…' : 'Show more'}
                </Button>
              )}
              <Outcome tone="problem">{error}</Outcome>
            </>
          )
        }
      >
        <div className="flex flex-wrap items-center gap-2 border-b border-b-(length:--hairline) p-(--panel-pad)">
          <Select
            aria-label="Feature"
            value={filters.feature}
            onChange={(feature) => set({ feature })}
          >
            <option value="">All features</option>
            {(page?.features ?? []).map((f) => (
              <option key={f} value={f}>
                {f}
              </option>
            ))}
          </Select>
          <Select
            aria-label="Outcome"
            value={filters.outcome}
            onChange={(outcome) => set({ outcome })}
          >
            <option value="">All outcomes</option>
            {(page?.outcomes ?? []).map((o) => (
              <option key={o} value={o}>
                {o}
              </option>
            ))}
          </Select>
          <Select
            aria-label="Model"
            value={filters.model}
            onChange={(model) => set({ model })}
          >
            <option value="">All models</option>
            {(page?.models ?? []).map((m) => (
              <option key={m} value={m}>
                {m}
              </option>
            ))}
          </Select>
          <Input
            aria-label="From"
            type="datetime-local"
            className="w-56"
            value={filters.from}
            onChange={(e) => set({ from: e.target.value })}
          />
          <Input
            aria-label="To"
            type="datetime-local"
            className="w-56"
            value={filters.to}
            onChange={(e) => set({ to: e.target.value })}
          />
          <Chip on={filters.flagged} onClick={() => set({ flagged: !filters.flagged })}>
            Flagged only
          </Chip>
          <Button size="sm" variant="outline" onClick={() => setFilters(noFilters)}>
            Clear
          </Button>
        </div>

        {!page && !error ? (
          <EmptyRow>Loading…</EmptyRow>
        ) : rows.length === 0 ? (
          <EmptyRow>No calls.</EmptyRow>
        ) : (
          <div className="relative overflow-x-auto">
            <Table style={{ fontSize: 'var(--text-small)' }}>
              <TableHeader>
                <TableRow>
                  <TableHead>When</TableHead>
                  <TableHead>Feature</TableHead>
                  <TableHead>Model</TableHead>
                  <TableHead>Outcome</TableHead>
                  <TableHead className="text-right">In</TableHead>
                  <TableHead className="hidden text-right lg:table-cell">Cached</TableHead>
                  <TableHead className="text-right">Out</TableHead>
                  <TableHead className="text-right">Cost</TableHead>
                  <TableHead className="text-right">Took</TableHead>
                  <TableHead className="hidden md:table-cell">Asked by</TableHead>
                  <TableHead />
                </TableRow>
              </TableHeader>
              <TableBody>
                {rows.map((c) => (
                  <TableRow key={c.id}>
                    <TableCell className="font-mono whitespace-nowrap">{new Date(c.at).toLocaleString()}</TableCell>
                    <TableCell>{c.featureLabel}</TableCell>
                    <TableCell className="max-w-[18rem] whitespace-normal font-mono">
                      {c.modelAnswered ?? c.modelAsked}
                      {c.fallback && (
                        <Badge variant="outline" className="ml-1.5">
                          Fallback
                        </Badge>
                      )}
                    </TableCell>
                    <TableCell className={cn('whitespace-nowrap', tone(c.outcome))}>
                      {c.outcomeLabel}
                      {c.error && (
                        <div className="max-w-[22rem] whitespace-normal text-muted-foreground">{c.error}</div>
                      )}
                    </TableCell>
                    <TableCell className="text-right font-mono">{count(c.inputTokens)}</TableCell>
                    <TableCell className="hidden text-right font-mono lg:table-cell">
                      {count(c.cachedInputTokens)}
                    </TableCell>
                    <TableCell className="text-right font-mono">{count(c.outputTokens)}</TableCell>
                    <TableCell className="text-right font-mono">
                      {c.cost === null ? '—' : money(c.cost)}
                    </TableCell>
                    <TableCell className="text-right font-mono">{took(c.durationMs)}</TableCell>
                    <TableCell className="hidden md:table-cell">{c.username ?? '—'}</TableCell>
                    <TableCell className="text-right">
                      {c.flagged && <Badge variant="secondary">Flagged</Badge>}
                      {c.hasText && (
                        <Button size="sm" variant="outline" className="ml-1.5" onClick={() => setOpen(c.id)}>
                          Open
                        </Button>
                      )}
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </div>
        )}
      </SettingsCard>

      <Dialog open={open !== null} onOpenChange={(o) => !o && setOpen(null)}>
        {open !== null && <CallDialog id={open} />}
      </Dialog>
    </SettingsSection>
  )
}

function CallDialog({ id }: { id: string }) {
  const [detail, setDetail] = useState<AiCallDetail | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    let open = true

    api
      .aiCall(id)
      .then((d) => {
        if (open) setDetail(d)
      })
      .catch((e: unknown) => {
        if (open) {
          setError(
            e instanceof ApiError && e.status === 403
              ? 'You do not have permission to read prompts and answers.'
              : failure(e),
          )
        }
      })

    return () => {
      open = false
    }
  }, [id])

  return (
    <DialogContent
      title="Call"
      className="max-w-[900px]"
      bodyClassName="flex max-h-[76vh] flex-col gap-3 overflow-y-auto"
    >
      <Outcome tone="problem">{error}</Outcome>

      {!detail && !error ? (
        <EmptyRow className="px-0">Loading…</EmptyRow>
      ) : detail ? (
        <>
          <div className="flex flex-wrap gap-x-4 gap-y-1" style={{ fontSize: 'var(--text-small)' }}>
            <span>{detail.call.featureLabel}</span>
            <span className="font-mono">{detail.call.modelAnswered ?? detail.call.modelAsked}</span>
            <span className={tone(detail.call.outcome)}>{detail.call.outcomeLabel}</span>
            <span className="font-mono text-muted-foreground">{new Date(detail.call.at).toLocaleString()}</span>
          </div>

          <Block title="Sent" text={detail.prompt} />
          <Block title="Answered" text={detail.answer} />
        </>
      ) : null}
    </DialogContent>
  )
}

function Block({ title, text }: { title: string; text: string | null }) {
  if (!text) return null

  return (
    <div className="flex flex-col gap-1">
      <span className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
        {title}
      </span>
      <pre
        className="overflow-x-auto border border-(length:--hairline) bg-card px-3 py-2 whitespace-pre-wrap"
        style={{ fontSize: 'var(--text-small)' }}
      >
        {text}
      </pre>
    </div>
  )
}
