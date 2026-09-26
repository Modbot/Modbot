import { useState, type ReactNode } from 'react'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent } from '@/components/ui/dialog'
import { Outcome } from '@/components/settings/fields'
import { ApiError } from '@/lib/api'

/**
 * The confirmation before a small action that takes something away: dismissing a flag, unlinking
 * a Discord account, taking a note back. The same shape as the kick and ban confirmation -- the
 * title names the action and the person, Cancel beside a red button, and the reason underneath
 * when it fails -- without the reasons and the note, which these actions do not take.
 *
 * Each of these was one tap before, and each sits near a button a moderator presses often, so a
 * slip on a phone did it with nothing in the way (UX review 2026-09-25, finding 8).
 *
 * The dialog closes only when the server has said yes. A failure keeps it open with the server's
 * message, so the moderator knows nothing changed.
 */
export function ConfirmDialog({
  open,
  onOpenChange,
  title,
  subtitle,
  action,
  failed,
  onConfirm,
  onDone,
}: {
  open: boolean
  onOpenChange: (open: boolean) => void
  /** Names the action and the person: "Dismiss the flag on Ada?" */
  title: string
  subtitle?: ReactNode
  /** The red button's label. */
  action: string
  /** What to say when the request fails without a message of its own. */
  failed: string
  onConfirm: () => Promise<unknown>
  /** Called once the server has accepted. */
  onDone?: () => void
}) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      {/* Mounted only while open, so each opening starts with no leftover error. */}
      {open && (
        <ConfirmBody
          title={title}
          subtitle={subtitle}
          action={action}
          failed={failed}
          onConfirm={onConfirm}
          onClose={() => onOpenChange(false)}
          onDone={onDone}
        />
      )}
    </Dialog>
  )
}

function ConfirmBody({
  title,
  subtitle,
  action,
  failed,
  onConfirm,
  onClose,
  onDone,
}: {
  title: string
  subtitle?: ReactNode
  action: string
  failed: string
  onConfirm: () => Promise<unknown>
  onClose: () => void
  onDone?: () => void
}) {
  const [sending, setSending] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)

  const send = () => {
    setSending(true)
    setProblem(null)

    onConfirm()
      .then(() => {
        onClose()
        onDone?.()
      })
      .catch((e: unknown) => setProblem(e instanceof ApiError ? e.message : failed))
      .finally(() => setSending(false))
  }

  return (
    <DialogContent title={title} subtitle={subtitle} className="max-w-[460px]">
      <div className="flex flex-col gap-3">
        <div className="flex flex-wrap items-center justify-end gap-2">
          <Button size="sm" variant="outline" onClick={onClose} disabled={sending}>
            Cancel
          </Button>
          <Button size="sm" variant="destructive" onClick={send} disabled={sending}>
            {sending ? 'Sending…' : action}
          </Button>
        </div>

        <Outcome tone="problem">{problem}</Outcome>
      </div>
    </DialogContent>
  )
}
