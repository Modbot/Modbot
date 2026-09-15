import { useState } from 'react'
import { Button } from '@/components/ui/button'
import { ApiError } from '@/lib/api'

/** Delete, then Confirm delete. Nothing is removed on the first click. */
export function DeleteButton({ onConfirm }: { onConfirm: () => Promise<void> }) {
  const [armed, setArmed] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  if (!armed) {
    return (
      <Button variant="outline" size="sm" onClick={() => setArmed(true)}>
        Delete
      </Button>
    )
  }

  const confirm = async () => {
    setBusy(true)
    setError(null)
    try {
      await onConfirm()
      setArmed(false)
    } catch (failure) {
      setError(failure instanceof ApiError ? failure.message : 'Delete failed.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <span className="inline-flex items-center gap-1">
      {error && <span className="text-destructive">{error}</span>}
      <Button variant="destructive" size="sm" disabled={busy} onClick={confirm}>
        Confirm delete
      </Button>
      <Button variant="ghost" size="sm" disabled={busy} onClick={() => setArmed(false)}>
        Cancel
      </Button>
    </span>
  )
}
