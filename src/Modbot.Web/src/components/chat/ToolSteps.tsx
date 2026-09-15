import { useState } from 'react'
import { AlertCircle, Check, ChevronRight, Loader2 } from 'lucide-react'
import { SourceChip } from '@/components/chat/Sources'
import { uniqueSources } from '@/components/chat/sourceLinks'
import type { ChatMessage, ChatReference, ChatToolCall } from '@/lib/api'
import { cn } from '@/lib/utils'

/** One lookup: what the model asked for, and what came back — or nothing yet, while it runs. */
export type ToolStep = { call: ChatToolCall; result: ChatMessage | undefined }

/** How many of the things one lookup found are shown before the rest fold behind a count. */
const REFERENCES_SHOWN = 12

/**
 * The lookups a reply made, drawn where the model made them.
 *
 * A moderator reading an answer wants to know what it was built from without reading JSON, so a
 * step says what it looked up and how it went, and opens to the same thing in words. The raw
 * result is one more click down, for when the words are not enough.
 */
export function ToolSteps({ steps }: { steps: readonly ToolStep[] }) {
  const [open, setOpen] = useState(false)

  const running = steps.find((s) => !s.result)
  const failed = steps.some((s) => s.result?.worked === false)

  if (steps.length === 0) return null

  if (steps.length === 1) return <Step step={steps[0]} />

  return (
    <div className="my-2 flex flex-col gap-1">
      <button
        type="button"
        aria-expanded={open}
        onClick={() => setOpen((o) => !o)}
        className="flex items-center gap-1.5 self-start rounded-md px-1.5 py-1 text-muted-foreground transition-colors hover:bg-accent hover:text-accent-foreground"
        style={{ fontSize: 'var(--text-small)' }}
      >
        <ChevronRight className={cn('size-3.5 transition-transform motion-reduce:transition-none', open && 'rotate-90')} />
        <Mark running={!!running} failed={failed} />
        <span>{running ? running.call.label : `${steps.length} steps`}</span>
      </button>

      {open && (
        <div className="ml-3 flex flex-col gap-1 border-l pl-3" style={{ borderLeftWidth: 'var(--hairline)' }}>
          {steps.map((step) => (
            <Step key={step.call.id} step={step} />
          ))}
        </div>
      )}
    </div>
  )
}

function Step({ step }: { step: ToolStep }) {
  const [open, setOpen] = useState(false)
  const [raw, setRaw] = useState(false)

  const { call, result } = step
  const failed = result?.worked === false

  return (
    <div className="my-1 flex flex-col gap-1">
      <button
        type="button"
        aria-expanded={open}
        onClick={() => setOpen((o) => !o)}
        className={cn(
          'flex items-center gap-1.5 self-start rounded-md px-1.5 py-1 transition-colors hover:bg-accent hover:text-accent-foreground',
          failed ? 'text-destructive' : 'text-muted-foreground',
        )}
        style={{ fontSize: 'var(--text-small)' }}
        title={result?.durationMs ? took(result.durationMs) : undefined}
      >
        <ChevronRight className={cn('size-3.5 transition-transform motion-reduce:transition-none', open && 'rotate-90')} />
        <Mark running={!result} failed={failed} />
        <span className="truncate">{call.label}</span>
      </button>

      {open && (
        <div
          className="ml-3 flex flex-col gap-2 rounded-lg border bg-muted/40 p-2.5"
          style={{ borderWidth: 'var(--hairline)', fontSize: 'var(--text-small)' }}
        >
          <Facts heading="Asked for" json={call.arguments} />

          {result && (
            <>
              {raw ? (
                <pre className="max-h-64 overflow-auto rounded-md bg-card p-2 font-mono whitespace-pre-wrap">
                  {pretty(result.content)}
                </pre>
              ) : (
                <Facts heading="Found" json={result.content} />
              )}

              {result.references.length > 0 && <References references={result.references} />}

              <div className="flex items-center gap-3 text-muted-foreground">
                <button
                  type="button"
                  onClick={() => setRaw((r) => !r)}
                  className="rounded-sm underline underline-offset-2 hover:text-foreground focus-visible:outline-2 focus-visible:outline-ring"
                >
                  {raw ? 'Summary' : 'Raw'}
                </button>
                {result.durationMs !== null && <span>{took(result.durationMs)}</span>}
              </div>
            </>
          )}
        </div>
      )}
    </div>
  )
}

function Mark({ running, failed }: { running: boolean; failed: boolean }) {
  if (running) return <Loader2 className="size-3.5 shrink-0 animate-spin motion-reduce:animate-none" />
  if (failed) return <AlertCircle className="size-3.5 shrink-0" />
  return <Check className="size-3.5 shrink-0 text-[color:var(--ok)]" />
}

function References({ references }: { references: readonly ChatReference[] }) {
  const [all, setAll] = useState(false)

  const sources = uniqueSources(references)
  const shown = all ? sources : sources.slice(0, REFERENCES_SHOWN)

  return (
    <div className="flex flex-wrap gap-1.5">
      {shown.map((r) => (
        <SourceChip key={`${r.kind}:${r.id}`} reference={r} />
      ))}
      {!all && sources.length > shown.length && (
        <button
          type="button"
          onClick={() => setAll(true)}
          className="rounded-full border px-2.5 py-0.5 text-muted-foreground hover:bg-accent hover:text-accent-foreground"
          style={{ borderWidth: 'var(--hairline)' }}
        >
          +{sources.length - shown.length}
        </button>
      )}
    </div>
  )
}

/**
 * A tool's arguments or its result, written out as lines rather than JSON: the same values, with
 * the punctuation removed.
 */
function Facts({ heading, json }: { heading: string; json: string }) {
  const value = parse(json)

  if (value === undefined) return null

  return (
    <div className="flex flex-col gap-1">
      <span className="text-muted-foreground">{heading}</span>
      <Value value={value} />
    </div>
  )
}

function Value({ value }: { value: unknown }) {
  if (Array.isArray(value)) {
    return (
      <div className="flex flex-col gap-0.5">
        <span>{value.length === 1 ? '1 item' : `${value.length} items`}</span>
        {value.slice(0, 5).map((item, index) => (
          <div key={index} className="border-l pl-2" style={{ borderLeftWidth: 'var(--hairline)' }}>
            <Value value={item} />
          </div>
        ))}
      </div>
    )
  }

  if (value !== null && typeof value === 'object') {
    const entries = Object.entries(value as Record<string, unknown>)

    // Wraps rather than holding a column width: at phone width the value goes under its name
    // instead of down a one-letter-wide channel.
    return (
      <dl className="flex flex-col gap-0.5">
        {entries.map(([key, item]) => (
          <div key={key} className="flex flex-wrap gap-x-3">
            <dt className="w-32 max-w-full shrink-0 text-muted-foreground">{words(key)}</dt>
            <dd className="min-w-0 break-words">{short(item)}</dd>
          </div>
        ))}
      </dl>
    )
  }

  return <span className="break-words">{short(value)}</span>
}

/** One value on one line: a list becomes its length, an object its keys. */
function short(value: unknown): string {
  if (value === null || value === undefined) return '—'
  if (typeof value === 'boolean') return value ? 'yes' : 'no'
  if (typeof value === 'number') return value.toLocaleString()
  if (typeof value === 'string') return value.length > 300 ? `${value.slice(0, 300)}…` : value

  if (Array.isArray(value)) {
    if (value.every((v) => typeof v !== 'object' || v === null)) return value.map(short).join(', ') || 'none'
    return value.length === 1 ? '1 item' : `${value.length} items`
  }

  const keys = Object.keys(value as Record<string, unknown>)
  return keys.length === 0 ? 'none' : keys.map(words).join(', ')
}

/** `displayName` reads as "Display name". */
function words(key: string): string {
  const spaced = key.replace(/([a-z\d])([A-Z])/g, '$1 $2').replace(/[_-]+/g, ' ')
  return spaced.charAt(0).toUpperCase() + spaced.slice(1).toLowerCase()
}

function parse(json: string): unknown {
  try {
    return JSON.parse(json)
  } catch {
    return json.trim().length > 0 ? json : undefined
  }
}

function pretty(json: string): string {
  const value = parse(json)
  return value === undefined ? '' : typeof value === 'string' ? value : JSON.stringify(value, null, 2)
}

function took(ms: number): string {
  return ms < 1000 ? `${ms} ms` : `${(ms / 1000).toFixed(1)} s`
}
