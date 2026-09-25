import { useCallback, useEffect, useState } from 'react'
import { X } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Card, CardHeader, CardTitle } from '@/components/ui/card'
import { api, type Alert } from '@/lib/api'
import { type LiveEvent } from '@/lib/liveStream'
import { followLink } from '@/lib/router'
import { useLiveStream } from '@/lib/useLiveStream'

/**
 * Recent unusual activity, at the top of My Group and Health (AI insights design §8.4).
 *
 * Renders nothing when there is nothing recent -- which is every deployment with no watcher on,
 * and most days of every deployment that has one. Reads again when the live stream says an
 * alert was raised, so a card appears without a reload.
 */
export function AlertsCard() {
  const [alerts, setAlerts] = useState<Alert[]>([])

  const load = useCallback(
    () =>
      api
        .alerts()
        .then((r) => setAlerts(r.alerts))
        .catch(() => setAlerts([])),
    [],
  )

  useEffect(() => {
    void load()
  }, [load])

  useLiveStream(
    useCallback(
      (event: LiveEvent) => {
        if (event.kind === 'alert') void load()
      },
      [load],
    ),
  )

  const dismiss = (id: string) => {
    setAlerts((all) => all.filter((a) => a.id !== id))
    api.dismissAlert(id).catch(() => void load())
  }

  if (alerts.length === 0) return null

  return (
    <Card>
      <CardHeader>
        <CardTitle>Unusual activity</CardTitle>
      </CardHeader>
      {alerts.map((alert) => (
        <Row key={alert.id} alert={alert} onDismiss={() => dismiss(alert.id)} />
      ))}
    </Card>
  )
}

function Row({ alert, onDismiss }: { alert: Alert; onDismiss: () => void }) {
  return (
    <div className="flex items-start gap-3 border-b border-b-(length:--hairline) px-(--panel-pad) py-2 last:border-0">
      <span aria-hidden className="mt-1.5 size-2 shrink-0 bg-warn" />

      <div className="min-w-0 flex-1">
        <div className="flex flex-wrap items-baseline gap-x-2">
          <span className="font-medium">{alert.label}</span>
          {alert.where && (
            <span className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
              {alert.where}
            </span>
          )}
          <span className="font-mono text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
            {new Date(alert.at).toLocaleString()}
          </span>
        </div>

        <div className="mt-0.5 tabular-nums" style={{ fontSize: 'var(--text-small)' }}>
          {figure(alert)}
        </div>

        {alert.text && (
          <p className="mt-1 max-w-3xl text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
            {alert.text}
          </p>
        )}
      </div>

      {alert.link && (
        <a
          href={alert.link}
          onClick={followLink(alert.link)}
          className="shrink-0 underline underline-offset-2"
          style={{ fontSize: 'var(--text-small)' }}
        >
          Open
        </a>
      )}

      <Button size="icon-sm" variant="ghost" className="shrink-0" aria-label="Hide" onClick={onDismiss}>
        <X className="size-4" />
      </Button>
    </div>
  )
}

/** "43 joins, normally 4 · 13:00–14:00" -- the figure, normal, and the stretch. */
function figure(alert: Alert): string {
  const counts = alert.counts ? ` ${alert.counts}` : ''
  return `${number(alert.now)}${counts}, normally ${number(alert.normal)} · ${stretch(alert)}`
}

function number(value: number): string {
  return Number.isInteger(value) ? String(value) : String(Math.round(value * 10) / 10)
}

function stretch(alert: Alert): string {
  const from = new Date(alert.windowStart)
  const to = new Date(alert.windowEnd)
  const days = (to.getTime() - from.getTime()) / 86_400_000

  return days >= 1
    ? `${from.toLocaleDateString()} – ${to.toLocaleDateString()}`
    : `${from.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })} – ${to.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}`
}
