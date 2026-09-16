import { useCallback, useEffect, useState } from 'react'
import { Button } from '@/components/ui/button'
import { api, type DemoStatus } from '@/lib/api'

/**
 * The demo's label in the top bar, and the control that puts its data back.
 *
 * Renders nothing at all on a deployment that is not a demo, which is every real one. While the
 * data is being filled in or reset it shows the step instead of the label, because a page whose
 * charts are still filling in should say so rather than look broken.
 */
export function DemoMarker() {
  const [status, setStatus] = useState<DemoStatus | null>(null)
  const [asked, setAsked] = useState(false)

  const load = useCallback(() => {
    api
      .demoStatus()
      .then((next) => {
        setStatus(next)

        // The reset is picked up a few seconds after it is asked for, so the button stays out of
        // use until the work is visibly under way, and then until it finishes.
        if (next.busy) setAsked(false)
      })
      .catch(() => undefined)
  }, [])

  useEffect(() => {
    load()
  }, [load])

  // Polled only while something is happening, so an idle demo asks once.
  const busy = status?.busy ?? false

  useEffect(() => {
    if (!busy && !asked) return

    const timer = setInterval(load, 2000)
    return () => clearInterval(timer)
  }, [busy, asked, load])

  if (!status?.on) return null

  const label = status.busy
    ? status.total > 0
      ? `${status.step} ${Math.round((status.done / status.total) * 100)}%`
      : status.step
    : 'Demo'

  return (
    <>
      <span
        className="rounded-full border px-2 py-0.5 font-medium text-muted-foreground"
        style={{ fontSize: 'var(--text-small)', borderWidth: 'var(--hairline)' }}
      >
        {label}
      </span>

      <Button
        variant="ghost"
        size="sm"
        disabled={status.busy || asked}
        onClick={() => {
          setAsked(true)
          void api
            .resetDemo()
            .then(() => load())
            .catch(() => setAsked(false))
        }}
      >
        Reset demo
      </Button>
    </>
  )
}
