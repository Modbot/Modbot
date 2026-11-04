import { useEffect, type RefObject } from 'react'
import { ArrowUp, Mic, Square } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { useDictation } from '@/components/chat/useDictation'
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
  maxLength: number
  inputRef: RefObject<HTMLTextAreaElement | null>
}) {
  const disabled = disabledReason !== null
  const dictation = useDictation(value, onChange, maxLength)
  const { cancel } = dictation

  // A box that stops taking messages stops listening too.
  useEffect(() => {
    if (disabled) cancel()
  }, [disabled, cancel])

  const send = () => {
    cancel()
    onSend()
  }

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
        send()
      }}
    >
      <div
        className={cn(
          'flex items-end gap-2 rounded-sm border border-(length:--hairline) border-input bg-card p-1.5 transition-colors',
          'focus-within:border-ring focus-within:ring-1 focus-within:ring-ring',
          disabled && 'opacity-60',
        )}
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
              send()
            }
          }}
          className="max-h-[200px] min-h-(--control-h) flex-1 resize-none bg-transparent px-2 py-1.5 text-base outline-none placeholder:text-muted-foreground md:text-(length:--text-base)"
        />

        {dictation.supported && (
          <Button
            type="button"
            variant={dictation.listening ? 'secondary' : 'ghost'}
            size="icon"
            aria-label="Dictate"
            aria-pressed={dictation.listening}
            disabled={disabled}
            onClick={dictation.toggle}
          >
            <Mic
              className={cn(
                'size-4',
                dictation.listening && 'animate-pulse text-destructive motion-reduce:animate-none',
              )}
            />
          </Button>
        )}

        {busy ? (
          <Button type="button" variant="secondary" size="icon" aria-label="Stop" onClick={onStop}>
            <Square className="size-3.5 fill-current" />
          </Button>
        ) : (
          <Button type="submit" size="icon" aria-label="Send" disabled={disabled || value.trim().length === 0}>
            <ArrowUp className="size-4" />
          </Button>
        )}
      </div>

      {dictation.blocked && (
        <p role="alert" className="px-1 text-destructive" style={{ fontSize: 'var(--text-small)' }}>
          Microphone blocked
        </p>
      )}
    </form>
  )
}
