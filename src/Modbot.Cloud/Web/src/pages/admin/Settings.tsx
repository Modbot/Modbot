import { useState, type FormEvent } from 'react'
import { Button } from '@/components/ui/button'
import { Card } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { ApiError, api, type Settings as SettingsValues } from '@/lib/api'
import { useAdminLoad } from '@/lib/useLoad'

export function Settings() {
  const { data, error } = useAdminLoad(() => api.settings(), [])

  if (error && error.status !== 401) return <p className="text-destructive">{error.message}</p>
  if (!data) return <p className="text-muted-foreground">Loading</p>

  return (
    <>
      <h1 className="text-lg font-semibold">Settings</h1>
      <RetentionForm initial={data} />
    </>
  )
}

function RetentionForm({ initial }: { initial: SettingsValues }) {
  const [eventDays, setEventDays] = useState(String(initial.eventKeepDays))
  const [status, setStatus] = useState<string | null>(null)
  const [failure, setFailure] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const save = async (e: FormEvent) => {
    e.preventDefault()
    setBusy(true)
    setStatus(null)
    setFailure(null)
    try {
      const saved = await api.saveSettings({ eventKeepDays: Number(eventDays) })
      setEventDays(String(saved.eventKeepDays))
      setStatus('Saved')
    } catch (e) {
      setFailure(e instanceof ApiError ? e.message : 'Could not reach the server.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <Card className="px-6">
      <form onSubmit={save} className="flex max-w-sm flex-col gap-4">
        <h2 className="font-semibold">Retention</h2>
        <div className="flex flex-col gap-1.5">
          <label htmlFor="event-days" className="font-medium">
            Keep events (days, 0 = forever)
          </label>
          <Input id="event-days" type="number" min={0} value={eventDays} onChange={(e) => setEventDays(e.target.value)} />
        </div>
        {failure && (
          <p role="alert" className="text-destructive" style={{ fontSize: 'var(--text-small)' }}>
            {failure}
          </p>
        )}
        <div className="flex items-center gap-3">
          <Button type="submit" disabled={busy || eventDays === ''}>
            Save
          </Button>
          {status && <span className="text-muted-foreground">{status}</span>}
        </div>
      </form>
    </Card>
  )
}