import { useEffect, type RefObject } from 'react'
import { ArrowUp, Square } from 'lucide-react'
import { cn } from '@/lib/utils'

/** The message box grows with what is typed, up to this, and then scrolls. */
const MAX_HEIGHT = 200

export function Composer({
  value,
  onChange,
  onSend,
  onStop,
  busy,
  disabledReason,
  model,
  maxLength,
  inputRef,
}: {
  value: string
  onChange: (value: string) => void
  onSend: () => void
  onStop: () => void
  busy: boolean
  /** What to say instead of taking a message: Chat off, conversation full, a spend limit. */
  disabledReason: string | null
  model: string | null
  maxLength: number
  inputRef: RefObject<HTMLTextAreaElement | null>
}) {
  const disabled = disabledReason !== null

  // Grows with the text, never past MAX_HEIGHT.
  useEffect(() => {
    const box = inputRef.current
    if (!box) return

    box.style.height = 'auto'
    box.style.height = `${Math.min(box.scrollHeight, MAX_HEIGHT)}px`
  }, [value, inputRef])

  return (
    <form
      className="flex flex-col gap-1.5"
      onSubmit={(e) => {
        e.preventDefault()
        onSend()
      }}
    >
      <div
        className={cn(
          'flex items-end gap-2 rounded-2xl border bg-card p-2 shadow-sm transition-colors',
          'focus-within:border-ring focus-within:ring-[3px] focus-within:ring-ring/40',
          disabled && 'opacity-60',
        )}
        style={{ borderWidth: 'var(--hairline)' }}
      >
        <textarea
          ref={inputRef}
          aria-label="Message"
          rows={1}
          value={value}
          disabled={disabled}
          maxLength={maxLength}
          placeholder={disabledReason ?? 'Ask about your group'}
          onChange={(e) => onChange(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === 'Enter' && !e.shiftKey && !e.nativeEvent.isComposing) {
              e.preventDefault()
              onSend()
            }
          }}
          className="max-h-[200px] min-h-9 flex-1 resize-none bg-transparent px-2 py-1.5 text-base outline-none placeholder:text-muted-foreground md:text-sm"
        />

        {busy ? (
          <button
            type="button"
            aria-label="Stop"
            onClick={onStop}
            className="flex size-9 shrink-0 items-center justify-center rounded-full bg-secondary text-secondary-foreground transition-colors hover:bg-secondary/80 focus-visible:ring-[3px] focus-visible:ring-ring/50"
          >
            <Square className="size-3.5 fill-current" />
          </button>
        ) : (
          <button
            type="submit"
            aria-label="Send"
            disabled={disabled || value.trim().length === 0}
            className="flex size-9 shrink-0 items-center justify-center rounded-full bg-primary text-primary-foreground transition-colors hover:bg-primary/90 focus-visible:ring-[3px] focus-visible:ring-ring/50 disabled:opacity-40"
          >
            <ArrowUp className="size-4" />
          </button>
        )}
      </div>

      {model && (
        <span className="px-2 text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
          {model}
        </span>
      )}
    </form>
  )
}
