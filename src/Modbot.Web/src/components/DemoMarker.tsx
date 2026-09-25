import { useCallback, useEffect, useState } from 'react'
import { Badge } from '@/components/ui/badge'
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

  const percent = status.busy && status.total > 0 ? `${Math.round((status.done / status.total) * 100)}%` : null

  // The marker takes whatever the title leaves, and the step wraps inside it, so on a phone the
  // title keeps its name and Reset demo stays on screen.
  return (
    <div className="flex min-w-0 flex-1 items-center gap-3">
      <Badge variant="secondary" className="min-w-0 shrink justify-start gap-1.5 whitespace-normal text-muted-foreground">
        <span aria-hidden className={status.busy ? 'size-1.5 shrink-0 bg-warn' : 'size-1.5 shrink-0 bg-muted-foreground/60'} />
        <span>
          {status.busy ? status.step : 'Demo'}
          {percent && <span className="font-mono"> {percent}</span>}
        </span>
      </Badge>

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
    </div>
  )
}
