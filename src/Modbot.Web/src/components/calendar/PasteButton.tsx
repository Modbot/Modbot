import { useEffect, useState } from 'react'
import { Check, ClipboardPaste, Copy } from 'lucide-react'

// What the clipboard holds, read again whenever the page comes back into focus. Reading needs the
// browser's permission: until it is given the answer is unknown (undefined), once refused it is ''.
function useClipboardText() {
  const [text, setText] = useState<string | undefined>(undefined)
  useEffect(() => {
    let stopped = false
    const read = async () => {
      try {
        const state = (await navigator.permissions.query({ name: 'clipboard-read' as PermissionName })).state
        if (state === 'denied') return !stopped && setText('')
        if (state !== 'granted') return
        const value = await navigator.clipboard.readText()
        if (!stopped) setText(value)
      } catch {
        // Not focused, or the browser can't say: keep what was known.
      }
    }
    read()
    window.addEventListener('focus', read)
    document.addEventListener('copy', read)
    document.addEventListener('cut', read)
    return () => {
      stopped = true
      window.removeEventListener('focus', read)
      document.removeEventListener('copy', read)
      document.removeEventListener('cut', read)
    }
  }, [])
  return [text, setText] as const
}

/** A paste button that only shows while the clipboard has text (or before the browser has said). */
export function PasteButton({ onPaste }: { onPaste: (text: string) => void }) {
  const [text, setText] = useClipboardText()
  // Hidden, not removed, so the text beside it never moves.
  const hidden = text !== undefined && text.trim() === ''
  return (
    <button
      disabled={hidden}
      style={hidden ? { visibility: 'hidden' } : undefined}
      type="button"
      aria-label="Paste"
      title="Paste"
      onMouseDown={(e) => e.preventDefault()}
      onClick={async () => {
        try {
          const value = await navigator.clipboard.readText()
          setText(value)
          if (value) onPaste(value)
        } catch {
          setText('')
        }
      }}
      className="shrink-0 rounded p-0.5 opacity-60 transition-opacity hover:opacity-100"
    >
      <ClipboardPaste className="size-[1em]" />
    </button>
  )
}

/** Copies a box's words; hidden (space kept) while the box is empty, a tick for a moment once copied. */
export function CopyButton({ text }: { text: string }) {
  const [copied, setCopied] = useState(false)
  const hidden = text.trim() === ''
  return (
    <button
      type="button"
      aria-label="Copy"
      title="Copy"
      disabled={hidden}
      style={hidden ? { visibility: 'hidden' } : undefined}
      onMouseDown={(e) => e.preventDefault()}
      onClick={async () => {
        try {
          await navigator.clipboard.writeText(text)
          setCopied(true)
          setTimeout(() => setCopied(false), 1200)
        } catch {
          // The browser refused; nothing to show.
        }
      }}
      className="shrink-0 rounded p-0.5 opacity-60 transition-opacity hover:opacity-100"
    >
      {copied ? <Check className="size-[1em]" /> : <Copy className="size-[1em]" />}
    </button>
  )
}
