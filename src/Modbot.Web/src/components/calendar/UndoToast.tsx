import { useEffect } from 'react'
import { Button } from '@/components/ui/button'
import { cn } from '@/lib/utils'

export type Toast = {
  /** Tells one toast from the next, so a second drag starts the timer again. */
  id: number
  text: string
  tone: 'done' | 'problem'
  undo?: () => void
}

/** How long a toast stays: long enough to reach Undo, short enough not to linger over the grid. */
const SHOWN_MS = 6000

/**
 * The small note at the foot of the screen after a drag: "Event moved · Undo", or what went wrong.
 * It goes by itself after a few seconds.
 */
export function UndoToast({ toast, onClose }: { toast: Toast; onClose: () => void }) {
  useEffect(() => {
    const timer = window.setTimeout(onClose, toast.tone === 'problem' ? SHOWN_MS * 2 : SHOWN_MS)
    return () => window.clearTimeout(timer)
  }, [toast.id, toast.tone, onClose])

  return (
    <div
      role="status"
      className={cn(
        // Above the phone's bar along the foot of the app, which is 3.25rem and the safe area.
        'fixed bottom-[calc(4rem+env(safe-area-inset-bottom))] left-1/2 z-50 flex max-w-[calc(100vw-2rem)] -translate-x-1/2 items-center gap-3 rounded-sm border-(length:--hairline) px-3 py-1 shadow-sm lg:bottom-6',
        toast.tone === 'problem' ? 'border-destructive/40 bg-card text-destructive' : 'bg-foreground text-background',
      )}
      style={{ fontSize: 'var(--text-small)' }}
    >
      <span className="min-w-0 [overflow-wrap:anywhere]">{toast.text}</span>
      {toast.undo && (
        <Button
          size="sm"
          variant="ghost"
          className="text-inherit hover:bg-background/15 hover:text-inherit"
          onClick={() => {
            toast.undo?.()
            onClose()
          }}
        >
          Undo
        </Button>
      )}
    </div>
  )
}
