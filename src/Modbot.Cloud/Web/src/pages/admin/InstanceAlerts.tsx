import { useEffect, useState, type FormEvent } from 'react'
import { Button } from '@/components/ui/button'
import { Card } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { ApiError, api, type InstanceAlertView } from '@/lib/api'
import { when } from '@/lib/format'

/**
 * Cloud watching one deployment from outside, and who it emails.
 *
 * The one thing a Modbot cannot report about itself is that it is not running. Cloud sees it,
 * because the logs stop arriving.
 */
export function InstanceAlerts({ installId }: { installId: string }) {
  const [view, setView] = useState<InstanceAlertView | null>(null)
  const [on, setOn] = useState(false)
  const [email, setEmail] = useState('')
  const [silentAfter, setSilentAfter] = useState('60')
  const [errorsAnHour, setErrorsAnHour] = useState('0')
  const [quietHours, setQuietHours] = useState('6')
  const [status, setStatus] = useState<string | null>(null)
  const [failure, setFailure] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    let cancelled = false

    api
      .instanceAlerts(installId)
      .then((next) => {
        if (cancelled) return
        setView(next)
        setOn(next.on)
        setEmail(next.email)
        setSilentAfter(String(next.silentAfterMinutes))
        setErrorsAnHour(String(next.errorsAnHour))
        setQuietHours(String(next.quietHours))
      })
      .catch(() => {
        if (!cancelled) setFailure('Could not load the alerts.')
      })

    return () => {
      cancelled = true
    }
  }, [installId])

  const save = async (e: FormEvent) => {
    e.preventDefault()
    setBusy(true)
    setStatus(null)
    setFailure(null)

    try {
      const saved = await api.saveInstanceAlerts(installId, {
        on,
        email,
        silentAfterMinutes: Number(silentAfter) || 0,
        errorsAnHour: Number(errorsAnHour) || 0,
        quietHours: Number(quietHours) || 0,
      })
      setView(saved)
      setStatus('Saved')
    } catch (e) {
      setFailure(e instanceof ApiError ? e.message : 'Could not reach the server.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <Card className="gap-0 py-0">
      <h2 className="font-display border-b px-4 py-3 text-base">Alerts</h2>

      <form onSubmit={save} className="flex flex-col gap-4 px-4 py-4">
        <label className="flex items-center gap-2">
          <input type="checkbox" checked={on} onChange={(e) => setOn(e.target.checked)} />
          Email about this deployment
        </label>

        <div className="grid max-w-3xl gap-3 sm:grid-cols-2 lg:grid-cols-4">
          <Labelled id="alert-email" label="Send to">
            <Input id="alert-email" type="email" value={email} onChange={(e) => setEmail(e.target.value)} />
          </Labelled>
          <Labelled id="alert-silent" label="Quiet for (minutes)">
            <Input
              id="alert-silent"
              type="number"
              min={5}
              value={silentAfter}
              onChange={(e) => setSilentAfter(e.target.value)}
            />
          </Labelled>
          <Labelled id="alert-errors" label="Errors an hour (0 = off)">
            <Input
              id="alert-errors"
              type="number"
              min={0}
              value={errorsAnHour}
              onChange={(e) => setErrorsAnHour(e.target.value)}
            />
          </Labelled>
          <Labelled id="alert-quiet" label="Quiet time (hours)">
            <Input
              id="alert-quiet"
              type="number"
              min={0}
              value={quietHours}
              onChange={(e) => setQuietHours(e.target.value)}
            />
          </Labelled>
        </div>

        {view?.problem && (
          <p className="text-destructive">
            {view.detail} {view.since && `Since ${when(view.since)}.`}
          </p>
        )}
        {view && !view.mailConfigured && <p className="text-muted-foreground">Cloud has no mail key set.</p>}
        {view?.lastError && <p className="text-destructive">{view.lastError}</p>}
        {failure && (
          <p role="alert" className="text-destructive">
            {failure}
          </p>
        )}

        <div className="flex items-center gap-3">
          <Button type="submit" disabled={busy}>
            Save
          </Button>
          {status && <span className="text-muted-foreground">{status}</span>}
        </div>
      </form>
    </Card>
  )
}

function Labelled({ id, label, children }: { id: string; label: string; children: React.ReactNode }) {
  return (
    <div className="flex flex-col gap-1.5">
      <label htmlFor={id} className="font-medium">
        {label}
      </label>
      {children}
    </div>
  )
}
