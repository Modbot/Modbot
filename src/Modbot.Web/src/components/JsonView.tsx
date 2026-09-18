import { useMemo, useState } from 'react'
import { Check, Copy } from 'lucide-react'
import { jsonPieces, type JsonPieceKind } from '@/lib/jsonPieces'
import { cn } from '@/lib/utils'

/**
 * Past this many characters the document is drawn as plain text.
 *
 * Splitting the text is cheap at any size; the spans are not. The biggest record the app shows is
 * the raw VRChat user object, which prints to a few hundred lines — around 10,000 characters —
 * so this is roughly ten times the largest real document, and past it the page matters more than
 * the colour.
 */
const PLAIN_ABOVE = 100_000

/** The hue each kind of piece is drawn in, checked against the viewer's surface in both themes. */
const COLOUR: Record<JsonPieceKind, string> = {
  key: 'text-[color:var(--info)]',
  string: 'text-[color:var(--ok)]',
  // Borrowed for its hue, not its meaning: the amber is the one warm colour in the set that stays
  // readable on both surfaces, and nothing about a number is a warning.
  number: 'text-[color:var(--warn)]',
  boolean: 'text-[color:var(--primary)] dark:text-[color:var(--accent-foreground)]',
  // Nothing, drawn as nothing much — and italic, so it is not read as another comma.
  null: 'text-muted-foreground italic',
  punctuation: 'text-muted-foreground',
  other: '',
}

/**
 * A record as JSON, pretty-printed and coloured, with a copy button.
 *
 * What is shown is what the server answered, verbatim: the point of the view is that nothing
 * between the stored record and the screen has reworded it. The colouring only splits that text
 * into pieces and renders each as text, so a value containing markup stays a value containing
 * markup.
 *
 * Pass `value` for a record, or `text` for a body that arrived as JSON already written out — a
 * body that will not parse is shown as it came, uncoloured, rather than guessed at.
 */
export function JsonView({
  value,
  text,
  title,
  className,
}: {
  value?: unknown
  text?: string
  title?: string
  className?: string
}) {
  const [copied, setCopied] = useState(false)

  const { body, isJson } = useMemo(() => read(value, text), [value, text])
  const pieces = useMemo(
    () => (isJson && body.length <= PLAIN_ABOVE ? jsonPieces(body) : null),
    [body, isJson],
  )

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
            void navigator.clipboard?.writeText(body).then(() => {
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
        {pieces
          ? pieces.map((piece, index) => (
              <span key={index} className={COLOUR[piece.kind]}>
                {piece.text}
              </span>
            ))
          : body}
      </pre>
    </div>
  )
}

/** The text to show, and whether it is JSON and so worth colouring. */
function read(value: unknown, text: string | undefined): { body: string; isJson: boolean } {
  if (text === undefined) return { body: JSON.stringify(value ?? null, null, 2) ?? 'null', isJson: true }

  try {
    return { body: JSON.stringify(JSON.parse(text), null, 2), isJson: true }
  } catch {
    return { body: text, isJson: false }
  }
}
