import { useState } from 'react'
import { Check, Copy } from 'lucide-react'
import { cn } from '@/lib/utils'

/**
 * A record as JSON, pretty-printed, with a copy button.
 *
 * What is shown is what the server answered, verbatim: the point of the view is that nothing
 * between the stored record and the screen has reworded it.
 */
export function JsonView({ value, title, className }: { value: unknown; title?: string; className?: string }) {
  const [copied, setCopied] = useState(false)
  const text = JSON.stringify(value ?? null, null, 2)

  return (
    <div className={cn('group/json overflow-hidden rounded-md border bg-muted/40', className)} style={{ borderWidth: 'var(--hairline)' }}>
      <div
        className="flex items-center justify-between gap-2 border-b px-3 py-1 text-muted-foreground"
        style={{ borderBottomWidth: 'var(--hairline)', fontSize: 'var(--text-small)' }}
      >
        <span className="truncate font-medium">{title ?? 'JSON'}</span>
        <button
          type="button"
          aria-label={copied ? 'Copied' : 'Copy JSON'}
          onClick={() => {
            void navigator.clipboard?.writeText(text).then(() => {
              setCopied(true)
              window.setTimeout(() => setCopied(false), 1500)
            })
          }}
          className="shrink-0 rounded-md p-1 hover:bg-accent hover:text-accent-foreground focus-visible:ring-[3px] focus-visible:ring-ring/50"
        >
          {copied ? <Check className="size-3.5" /> : <Copy className="size-3.5" />}
        </button>
      </div>
      <pre className="max-h-[32rem] overflow-auto px-3 py-2 font-mono whitespace-pre-wrap break-all" style={{ fontSize: 'var(--text-small)' }}>
        {text}
      </pre>
    </div>
  )
}
