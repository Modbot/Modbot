import { Children, isValidElement, useState, type ReactNode } from 'react'
import { Check, Copy } from 'lucide-react'
import { cn } from '@/lib/utils'

/**
 * A fenced code block, with the language it was written in and a copy button.
 *
 * Highlighting is not done here: whoever renders the Markdown adds it (Chat does), so a screen
 * that shows a written reason does not carry a highlighter it never uses.
 */
export function CodeBlock({ children, className }: { children: ReactNode; className?: string }) {
  const [copied, setCopied] = useState(false)

  const code = textOf(children)
  const language = languageOf(children)

  return (
    <div className={cn('group/code my-2 overflow-hidden rounded-xl bg-muted', className)}>
      <div
        className="flex items-center justify-between gap-2 px-3 pt-1.5 text-muted-foreground"
        style={{ fontSize: 'var(--text-small)' }}
      >
        <span className="truncate font-mono">{language ?? ''}</span>
        <button
          type="button"
          aria-label={copied ? 'Copied' : 'Copy code'}
          onClick={() => {
            void navigator.clipboard?.writeText(code).then(() => {
              setCopied(true)
              window.setTimeout(() => setCopied(false), 1500)
            })
          }}
          className={cn(
            'shrink-0 rounded-md p-1 opacity-0 transition motion-reduce:transition-none',
            'hover:bg-accent hover:text-accent-foreground focus-visible:opacity-100 focus-visible:ring-[3px] focus-visible:ring-ring/50',
            'group-hover/code:opacity-100 [@media(hover:none)]:opacity-100',
          )}
        >
          {copied ? <Check className="size-3.5" /> : <Copy className="size-3.5" />}
        </button>
      </div>
      <pre className="overflow-x-auto px-3 pb-3">{children}</pre>
    </div>
  )
}

/** The text inside, however deeply the highlighter wrapped it in spans. */
function textOf(node: ReactNode): string {
  if (typeof node === 'string') return node
  if (typeof node === 'number') return String(node)
  if (Array.isArray(node)) return node.map(textOf).join('')

  if (isValidElement<{ children?: ReactNode }>(node)) return textOf(node.props.children)

  return ''
}

/** Markdown writes the language on the `code` element as `language-sql`. */
function languageOf(children: ReactNode): string | null {
  for (const child of Children.toArray(children)) {
    if (!isValidElement<{ className?: string }>(child)) continue

    const match = /(?:^|\s)language-([\w+-]+)/.exec(child.props.className ?? '')
    if (match) return match[1]
  }

  return null
}
